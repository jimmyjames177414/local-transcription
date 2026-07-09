using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Agent.OpenAI;

public sealed record OpenAITextAgentOptions
{
    public required string ApiKey { get; init; }
    public string Model { get; init; } = "gpt-5.4-mini";
    public double Temperature { get; init; } = 0.2;
    public int MaxOutputTokens { get; init; } = 700;
    public string Endpoint { get; init; } = "https://api.openai.com/v1/chat/completions";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>Injectable HTTP layer so tests never touch the network.</summary>
public interface IOpenAIHttpTransport
{
    Task<string> PostJsonAsync(string url, string apiKey, string jsonBody, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class HttpClientOpenAITransport : IOpenAIHttpTransport
{
    private static readonly HttpClient Client = new();

    public async Task<string> PostJsonAsync(string url, string apiKey, string jsonBody, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            string detail = ex.InnerException is { } inner ? $" ({inner.GetType().Name}: {inner.Message}" +
                (inner.InnerException is { } inner2 ? $" -> {inner2.Message})" : ")") : "";
            throw new HttpRequestException($"{ex.Message}{detail}", ex);
        }

        using var _ = response;
        string body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Error bodies never include our key; safe to surface trimmed.
            throw new HttpRequestException($"OpenAI request failed ({(int)response.StatusCode}): {Truncate(body, 400)}");
        }

        return body;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>
/// Sends the rolling transcript window + context pack to an OpenAI text model and
/// maps the structured JSON reply to suggestions. Transcript TEXT only — never audio.
/// </summary>
public sealed class OpenAITextMeetingAgentProvider : IMeetingAgentProvider
{
    private readonly OpenAITextAgentOptions _options;
    private readonly IOpenAIHttpTransport _transport;

    public OpenAITextMeetingAgentProvider(OpenAITextAgentOptions options, IOpenAIHttpTransport? transport = null)
    {
        _options = options;
        _transport = transport ?? new HttpClientOpenAITransport();
    }

    public string Name => "openai";

    public async Task<AgentProviderResult> AnalyzeAsync(AgentProviderRequest request, CancellationToken cancellationToken = default)
    {
        string body = OpenAIRequestBuilder.Build(_options, request);
        string response = await _transport.PostJsonAsync(_options.Endpoint, _options.ApiKey, body, _options.Timeout, cancellationToken).ConfigureAwait(false);
        return OpenAIResponseParser.Parse(response, request.SessionId);
    }
}

public static class OpenAIRequestBuilder
{
    public const string SystemPrompt =
        "You are a private meeting sidecar for one user. You watch a live meeting transcript " +
        "(text only — you never hear audio) plus the user's local project context, and you produce " +
        "a small number of genuinely useful private suggestions: risks, blockers, decisions, action items, " +
        "contradictions with known context, questions worth asking, and suggested responses. " +
        "Rules: be selective — silence is better than noise; never fabricate speaker names; " +
        "transcripts contain recognition errors, so mark uncertainty in wording and confidence; " +
        "never claim you heard audio; keep each message under 40 words; " +
        "output ONLY the JSON matching the provided schema.";

    private static readonly object ResponseSchema = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "suggestions", "runningSummaryUpdate" },
        properties = new
        {
            suggestions = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "type", "priority", "title", "message", "confidence" },
                    properties = new
                    {
                        type = new { type = "string", @enum = Enum.GetNames<AgentSuggestionType>() },
                        priority = new { type = "string", @enum = Enum.GetNames<AgentSuggestionPriority>() },
                        title = new { type = "string" },
                        message = new { type = "string" },
                        confidence = new { type = "number" }
                    }
                }
            },
            runningSummaryUpdate = new { type = new[] { "string", "null" } }
        }
    };

    public static string Build(OpenAITextAgentOptions options, AgentProviderRequest request)
    {
        var transcript = new StringBuilder();
        foreach (var e in request.WindowEvents)
        {
            transcript.AppendLine($"[{e.Timestamp.ToLocalTime():HH:mm:ss}] {e.Speaker.DisplayName} ({(e.Source == AudioSourceType.Microphone ? "user mic" : "meeting audio")}): {e.Text}");
        }

        var user = new StringBuilder();
        user.AppendLine("## Project context");
        user.AppendLine(string.IsNullOrWhiteSpace(request.ContextSummary) ? "(no context pack provided)" : request.ContextSummary);
        user.AppendLine();
        user.AppendLine("## Running meeting summary so far");
        user.AppendLine(string.IsNullOrWhiteSpace(request.RunningSummary) ? "(none yet)" : request.RunningSummary);
        user.AppendLine();
        user.AppendLine("## Recent transcript window");
        user.AppendLine(transcript.Length == 0 ? "(empty)" : transcript.ToString());
        user.AppendLine();
        if (request.UserQuestion is not null)
        {
            user.AppendLine("## The user privately asks you");
            user.AppendLine(request.UserQuestion);
            user.AppendLine("Answer as one or more SuggestedResponse suggestions.");
        }
        else
        {
            user.AppendLine("Produce at most 3 suggestions, only if genuinely useful. Update the running summary (2-4 sentences).");
        }

        // gpt-5.x / o-series reject custom temperature and use max_completion_tokens.
        bool restricted = options.Model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || options.Model.StartsWith("o", StringComparison.OrdinalIgnoreCase);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = user.ToString() }
            },
            ["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = "meeting_suggestions", strict = true, schema = ResponseSchema }
            },
            ["max_completion_tokens"] = options.MaxOutputTokens
        };
        if (!restricted)
        {
            payload["temperature"] = options.Temperature;
        }

        return JsonSerializer.Serialize(payload);
    }
}

public static class OpenAIResponseParser
{
    /// <summary>Parses a chat-completions response; malformed content degrades to zero suggestions.</summary>
    public static AgentProviderResult Parse(string responseJson, string? sessionId)
    {
        string? content;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            return new AgentProviderResult(Array.Empty<AgentSuggestion>(), null);
        }

        return ParseContent(content, sessionId, source: "openai");
    }

    /// <summary>Parses the model's structured JSON payload (shared by text + realtime providers).</summary>
    public static AgentProviderResult ParseContent(string? content, string? sessionId, string source)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new AgentProviderResult(Array.Empty<AgentSuggestion>(), null);
        }

        // Models occasionally wrap the JSON in fences or stray characters; carve out the
        // JSON value. Without strict schema enforcement (realtime), some replies are a
        // bare suggestions array instead of the wrapper object — accept both.
        int firstObj = content.IndexOf('{');
        int firstArr = content.IndexOf('[');
        int first = (firstObj, firstArr) switch
        {
            (-1, -1) => -1,
            (-1, _) => firstArr,
            (_, -1) => firstObj,
            _ => Math.Min(firstObj, firstArr)
        };
        char closer = first == firstArr && (firstObj == -1 || firstArr < firstObj) ? ']' : '}';
        int last = content.LastIndexOf(closer);
        if (first < 0 || last <= first)
        {
            return new AgentProviderResult(Array.Empty<AgentSuggestion>(), null);
        }
        content = content[first..(last + 1)];

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            JsonElement array = default;
            bool hasArray = root.ValueKind == JsonValueKind.Array;
            if (hasArray)
            {
                array = root;
            }
            else if (root.TryGetProperty("suggestions", out var arrayProp) && arrayProp.ValueKind == JsonValueKind.Array)
            {
                array = arrayProp;
                hasArray = true;
            }

            var suggestions = new List<AgentSuggestion>();
            if (hasArray)
            {
                foreach (var s in array.EnumerateArray())
                {
                    string? title = s.TryGetProperty("title", out var t) ? t.GetString() : null;
                    string? message = s.TryGetProperty("message", out var m) ? m.GetString() : null;
                    if (title is null || message is null)
                    {
                        continue;
                    }

                    suggestions.Add(new AgentSuggestion(
                        Id: Guid.NewGuid().ToString("N"),
                        SessionId: sessionId,
                        CreatedAt: DateTimeOffset.Now,
                        Type: s.TryGetProperty("type", out var ty) && Enum.TryParse<AgentSuggestionType>(ty.GetString(), true, out var type) ? type : AgentSuggestionType.ContextReminder,
                        Priority: s.TryGetProperty("priority", out var p) && Enum.TryParse<AgentSuggestionPriority>(p.GetString(), true, out var priority) ? priority : AgentSuggestionPriority.Low,
                        Title: title,
                        Message: message,
                        Source: source,
                        Confidence: ParseConfidence(s)));
                }
            }

            string? summary = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("runningSummaryUpdate", out var su) && su.ValueKind == JsonValueKind.String
                ? su.GetString()
                : null;

            return new AgentProviderResult(suggestions, summary);
        }
        catch (JsonException)
        {
            return new AgentProviderResult(Array.Empty<AgentSuggestion>(), null);
        }
    }

    /// <summary>Accepts numeric confidence, numeric strings, and loose labels like "Medium".</summary>
    private static double? ParseConfidence(JsonElement suggestion)
    {
        if (!suggestion.TryGetProperty("confidence", out var c))
        {
            return null;
        }

        return c.ValueKind switch
        {
            JsonValueKind.Number => c.GetDouble(),
            JsonValueKind.String when double.TryParse(c.GetString(), out double value) => value,
            JsonValueKind.String => c.GetString()?.ToLowerInvariant() switch
            {
                "high" => 0.85,
                "medium" => 0.6,
                "low" => 0.35,
                _ => null
            },
            _ => null
        };
    }
}
