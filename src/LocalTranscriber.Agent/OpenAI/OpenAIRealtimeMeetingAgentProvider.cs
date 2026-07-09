using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Agent.OpenAI;

public sealed record RealtimeConnectionOptions
{
    public required string ApiKey { get; init; }
    public string Model { get; init; } = "gpt-realtime-2.1-mini";
    public string BaseUrl { get; init; } = "wss://api.openai.com/v1/realtime";
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public int MaxReconnectAttempts { get; init; } = 3;
}

/// <summary>Injectable websocket layer so tests never touch the network.</summary>
public interface IRealtimeTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default);
    Task SendAsync(string json, CancellationToken cancellationToken = default);
    /// <summary>Next complete server event as JSON text; null when the socket closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken = default);
}

public sealed class ClientWebSocketRealtimeTransport : IRealtimeTransport
{
    private ClientWebSocket? _socket;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        _socket?.Dispose();
        _socket = new ClientWebSocket();
        foreach (var (name, value) in headers)
        {
            _socket.Options.SetRequestHeader(name, value);
        }
        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync(string json, CancellationToken cancellationToken = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Realtime socket is not connected.");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is null)
        {
            return null;
        }

        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Low-latency provider over the OpenAI Realtime websocket. Sends transcript TEXT
/// events only — raw audio is never transmitted. Session instructions + context go
/// once per connection; each analysis sends only events not yet delivered, so
/// reconnects cannot duplicate transcript input.
/// </summary>
public sealed class OpenAIRealtimeMeetingAgentProvider : IMeetingAgentProvider, IAsyncDisposable
{
    private readonly RealtimeConnectionOptions _options;
    private readonly Func<IRealtimeTransport> _transportFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HashSet<string> _sentEventIds = new();

    private IRealtimeTransport? _transport;
    private bool _sessionConfigured;
    private string? _lastContextSummary;

    public OpenAIRealtimeMeetingAgentProvider(RealtimeConnectionOptions options, Func<IRealtimeTransport>? transportFactory = null)
    {
        _options = options;
        _transportFactory = transportFactory ?? (() => new ClientWebSocketRealtimeTransport());
    }

    public string Name => "realtime";

    public string ConnectionState => _transport?.IsConnected == true ? "connected" : "disconnected";

    public async Task<AgentProviderResult> AnalyzeAsync(AgentProviderRequest request, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await EnsureConnectedAsync(request, cancellationToken).ConfigureAwait(false);
                    return await SendAndCollectAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or IOException && attempt < _options.MaxReconnectAttempts)
                {
                    AppLog.Warn("agent", $"Realtime connection lost ({ex.GetType().Name}); reconnecting (attempt {attempt + 1})");
                    _transport = null;
                    _sessionConfigured = false;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureConnectedAsync(AgentProviderRequest request, CancellationToken cancellationToken)
    {
        if (_transport is { IsConnected: true } && _sessionConfigured)
        {
            return;
        }

        _transport = _transportFactory();
        await _transport.ConnectAsync(
            new Uri($"{_options.BaseUrl}?model={Uri.EscapeDataString(_options.Model)}"),
            new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {_options.ApiKey}"
            },
            cancellationToken).ConfigureAwait(false);

        string instructions = OpenAIRequestBuilder.SystemPrompt +
            "\n\n## Project context\n" + (string.IsNullOrWhiteSpace(request.ContextSummary) ? "(none)" : request.ContextSummary) +
            "\n\nWhen asked to respond, output ONLY a raw JSON object (no code fences, no escaping, no text before or after) with exactly two keys: " +
            "'suggestions' (array of objects with keys: type, priority, title, message, confidence) and 'runningSummaryUpdate' (string or null). " +
            $"type must be one of: {string.Join(", ", Enum.GetNames<AgentSuggestionType>())}. " +
            $"priority must be one of: {string.Join(", ", Enum.GetNames<AgentSuggestionPriority>())}. " +
            "confidence must be a NUMBER between 0 and 1.";

        // GA realtime schema: session.type is required, "output_modalities" replaced "modalities".
        var sessionUpdate = new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                output_modalities = new[] { "text" },
                instructions
            }
        };
        await _transport.SendAsync(JsonSerializer.Serialize(sessionUpdate), cancellationToken).ConfigureAwait(false);
        _sessionConfigured = true;
        _lastContextSummary = request.ContextSummary;
    }

    private async Task<AgentProviderResult> SendAndCollectAsync(AgentProviderRequest request, CancellationToken cancellationToken)
    {
        if (_transport is null)
        {
            throw new InvalidOperationException("Realtime transport unavailable.");
        }

        // Only transcript events this connection has not seen yet.
        var newEvents = request.WindowEvents.Where(e => !_sentEventIds.Contains($"{e.Id}")).ToList();
        var text = new StringBuilder();
        foreach (var e in newEvents)
        {
            text.AppendLine($"[{e.Timestamp.ToLocalTime():HH:mm:ss}] {e.Speaker.DisplayName} ({(e.Source == AudioSourceType.Microphone ? "user mic" : "meeting audio")}): {e.Text}");
        }

        if (request.UserQuestion is not null)
        {
            text.AppendLine($"\nThe user privately asks you: {request.UserQuestion}");
        }
        else if (newEvents.Count == 0)
        {
            return new AgentProviderResult(Array.Empty<AgentSuggestion>(), null);
        }
        else
        {
            text.AppendLine("\nAnalyze the new transcript lines above. At most 3 suggestions, only if genuinely useful. Include runningSummaryUpdate.");
        }

        var itemCreate = new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new[] { new { type = "input_text", text = text.ToString() } }
            }
        };
        await _transport.SendAsync(JsonSerializer.Serialize(itemCreate), cancellationToken).ConfigureAwait(false);
        await _transport.SendAsync("{\"type\":\"response.create\"}", cancellationToken).ConfigureAwait(false);

        string? outputText = await WaitForResponseAsync(cancellationToken).ConfigureAwait(false);

        // Mark delivered only after a successful round trip.
        foreach (var e in newEvents)
        {
            _sentEventIds.Add(e.Id);
        }

        return OpenAIResponseParser.ParseContent(outputText, request.SessionId, source: "realtime");
    }

    private async Task<string?> WaitForResponseAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.ResponseTimeout);

        // Some GA models deliver the text only in response.output_text.done and send
        // response.done with an empty content array — keep the latest text we saw.
        string? lastText = null;
        while (true)
        {
            string? message = await _transport!.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            if (message is null)
            {
                throw new WebSocketException("Realtime socket closed while waiting for a response.");
            }

            if (Environment.GetEnvironmentVariable("LT_REALTIME_DEBUG") == "1")
            {
                Console.Error.WriteLine($"[realtime<-] {message[..Math.Min(message.Length, 600)]}");
            }

            var parsed = RealtimeEventMapper.Map(message);
            switch (parsed.Kind)
            {
                case RealtimeEventKind.OutputTextDone:
                    lastText = parsed.Text ?? lastText;
                    continue;
                case RealtimeEventKind.ResponseDone:
                    return parsed.Text ?? lastText;
                case RealtimeEventKind.Error:
                    AppLog.Warn("agent", $"Realtime error event: {parsed.Text}");
                    throw new WebSocketException($"Realtime error: {parsed.Text}");
                default:
                    continue; // deltas, acks, etc.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        _lock.Dispose();
    }
}

public enum RealtimeEventKind
{
    Other,
    OutputTextDone,
    ResponseDone,
    Error
}

public sealed record RealtimeEvent(RealtimeEventKind Kind, string? Text);

/// <summary>Maps raw server events to the few kinds the provider cares about.</summary>
public static class RealtimeEventMapper
{
    public static RealtimeEvent Map(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "response.output_text.done":
                case "response.text.done":
                    return new RealtimeEvent(RealtimeEventKind.OutputTextDone,
                        root.TryGetProperty("text", out var textProp) ? textProp.GetString() : null);
                case "response.done":
                case "response.completed":
                    return new RealtimeEvent(RealtimeEventKind.ResponseDone, ExtractOutputText(root));
                case "error":
                    string? message = root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m)
                        ? m.GetString()
                        : json;
                    return new RealtimeEvent(RealtimeEventKind.Error, message);
                default:
                    return new RealtimeEvent(RealtimeEventKind.Other, null);
            }
        }
        catch (JsonException)
        {
            return new RealtimeEvent(RealtimeEventKind.Other, null);
        }
    }

    /// <summary>Concatenates all text content in response.output[].content[] (handles "text" and "output_text").</summary>
    private static string? ExtractOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || !response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                string? partType = part.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                if (partType is "text" or "output_text" && part.TryGetProperty("text", out var textProp))
                {
                    text.Append(textProp.GetString());
                }
            }
        }

        return text.Length > 0 ? text.ToString() : null;
    }
}
