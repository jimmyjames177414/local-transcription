using System.Text.Json;
using LocalTranscriber.Agent.OpenAI;
using LocalTranscriber.Audio;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Voice;

/// <summary>Speaks assistant reply text aloud. The "mouth" of the hybrid backend.</summary>
public interface IReplySpeaker : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Speaks the given text (verbatim). Returns once the request is sent; audio plays async.</summary>
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Cuts off any current playback (barge-in when the user starts a new turn).</summary>
    void StopSpeaking();

    event EventHandler<string>? ErrorOccurred;
}

public sealed record RealtimeSpeakerOptions
{
    public required string ApiKey { get; init; }
    public string Model { get; init; } = "gpt-realtime-2.1-mini";
    public string BaseUrl { get; init; } = "wss://api.openai.com/v1/realtime";
    public string Voice { get; init; } = "marin";
    public string? OutputAudioDeviceId { get; init; }
    public int MaxReconnectAttempts { get; init; } = 3;
}

/// <summary>
/// Uses the OpenAI Realtime API purely as a text-to-speech engine: it connects once, is configured
/// with a "read the user's message verbatim" persona, and for each reply sends the text as a user
/// item + response.create, playing the returned audio. No microphone audio is ever sent — this is
/// output only. The brain (Claude CLI) does all the thinking; this only voices its replies.
/// </summary>
public sealed class RealtimeSpeaker : IReplySpeaker
{
    private const string TtsPersona =
        "You are a text-to-speech engine. Read the user's message aloud verbatim, exactly as written. " +
        "Do not add, omit, summarise, translate, answer, or comment — output only speech of the exact text.";

    private readonly RealtimeSpeakerOptions _options;
    private readonly Func<IRealtimeTransport> _transportFactory;
    private readonly IAgentAudioOutput _audio;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private IRealtimeTransport? _transport;
    private Task? _receiveLoop;

    public RealtimeSpeaker(
        RealtimeSpeakerOptions options,
        Func<IRealtimeTransport>? transportFactory = null,
        IAgentAudioOutput? audioOutput = null)
    {
        _options = options;
        _transportFactory = transportFactory ?? (() => new ClientWebSocketRealtimeTransport());
        _audio = audioOutput ?? new NAudioAgentAudioOutput(options.OutputAudioDeviceId);
    }

    public event EventHandler<string>? ErrorOccurred;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;

        _transport = _transportFactory();
        await ConnectWithRetryAsync(ct).ConfigureAwait(false);
        await SendAsync(BuildSessionUpdate(), ct).ConfigureAwait(false);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(ct), CancellationToken.None);
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_transport is null)
        {
            return;
        }

        text = text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var ct = _cts?.Token ?? cancellationToken;
        _audio.Stop(); // drop any earlier reply still playing
        await SendAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new[] { new { type = "input_text", text } }
            }
        }, ct).ConfigureAwait(false);
        await SendAsync(new { type = "response.create" }, ct).ConfigureAwait(false);
    }

    public void StopSpeaking()
    {
        _audio.Stop();
        if (_transport is not null)
        {
            _ = SendAsync(new { type = "response.cancel" }, _cts?.Token ?? CancellationToken.None);
        }
    }

    // Mirrors RealtimeVoiceSession's retry: the first outbound TLS handshake on enterprise machines is
    // often reset, then succeeds on retry.
    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        var uri = new Uri($"{_options.BaseUrl}?model={Uri.EscapeDataString(_options.Model)}");
        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {_options.ApiKey}" };
        int attempts = Math.Max(1, _options.MaxReconnectAttempts);

        for (int attempt = 1; ; attempt++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await _transport!.ConnectAsync(uri, headers, linked.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < attempts && !ct.IsCancellationRequested)
            {
                AppLog.Warn("speaker", $"Realtime TTS connect attempt {attempt}/{attempts} failed ({ex.Message}); retrying.");
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private object BuildSessionUpdate() => new
    {
        type = "session.update",
        session = new
        {
            type = "realtime",
            output_modalities = new[] { "audio" },
            instructions = TtsPersona,
            audio = new
            {
                output = new
                {
                    voice = _options.Voice,
                    format = new { type = "audio/pcm", rate = 24000 }
                }
            }
        }
    };

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? message = await _transport!.ReceiveAsync(ct).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                var evt = RealtimeVoiceEventMapper.Map(message);
                switch (evt.Kind)
                {
                    case RealtimeVoiceEventKind.OutputAudioDelta when evt.Text is not null:
                        _audio.EnqueueBase64(evt.Text);
                        break;
                    case RealtimeVoiceEventKind.ResponseDone:
                        _audio.Flush();
                        break;
                    case RealtimeVoiceEventKind.Error:
                        ErrorOccurred?.Invoke(this, evt.Text ?? "Realtime TTS error.");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn("speaker", $"TTS receive loop ended: {ex.Message}");
        }
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(payload);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _transport!.SendAsync(json, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _audio.Stop();

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }

        _audio.Dispose();
        _cts?.Dispose();
        _sendLock.Dispose();
    }
}
