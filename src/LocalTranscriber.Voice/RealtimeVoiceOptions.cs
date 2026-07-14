using LocalTranscriber.Shared;

namespace LocalTranscriber.Voice;

/// <summary>Lifecycle/status of a voice conversation, surfaced to the UI/CLI.</summary>
public enum RealtimeVoiceState
{
    Idle,
    Connecting,
    Ready,
    Capturing,
    Thinking,
    Speaking,
    Stopped,
    Faulted
}

/// <summary>A function tool the assistant can call. Parameters is a JSON-schema-shaped object.</summary>
public sealed record RealtimeToolDefinition(string Name, string Description, object Parameters);

/// <summary>One tool invocation from the server; ArgumentsJson is the raw arguments string.</summary>
public sealed record RealtimeToolCall(string Name, string CallId, string ArgumentsJson);

/// <summary>
/// All settings for one real-time voice conversation. Built by <see cref="RealtimeVoiceFactory"/>
/// from <c>AppConfig</c> + resolved secret. Grounding sources (transcript/context) are optional;
/// when absent the model simply has no live meeting context.
/// </summary>
public sealed record RealtimeVoiceOptions
{
    public required string ApiKey { get; init; }
    public string Model { get; init; } = "gpt-realtime-2.1-mini";
    public string BaseUrl { get; init; } = "wss://api.openai.com/v1/realtime";
    public RealtimeVoiceMode Mode { get; init; } = RealtimeVoiceMode.Hybrid;
    public string Voice { get; init; } = "marin";

    public double VadThreshold { get; init; } = 0.5;
    public int VadSilenceMs { get; init; } = 500;
    public int VadPrefixPaddingMs { get; init; } = 300;

    public string? InputAudioDeviceId { get; init; }
    public string? OutputAudioDeviceId { get; init; }

    /// <summary>
    /// Play the AI reply as audio. When false, replies are captions only (no playback), which
    /// keeps a Bluetooth headset mic in A2DP and prevents the hands-free profile flap that drops it.
    /// </summary>
    public bool SpeakReplies { get; init; } = true;

    public int GroundingIntervalSeconds { get; init; } = 15;
    public int MaxReconnectAttempts { get; init; } = 3;
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(45);

    // Grounding sources (all optional).
    public string? TranscriptJsonlPath { get; init; }
    public string ContextFolder { get; init; } = "context";
    public int MaxContextCharacters { get; init; } = 20000;
    public IReadOnlyList<string> RequiredContextFiles { get; init; } = new[] { "codename-summary.md" };
    public int RollingWindowMinutes { get; init; } = 5;
    public int MaxTranscriptEventsPerPrompt { get; init; } = 80;
    public string AgentOutputFolder { get; init; } = System.IO.Path.Combine("output", "agent");

    // Hybrid mode: local whisper STT of the held microphone audio.
    public string WhisperModelPath { get; init; } = "models/whisper/ggml-base.en.bin";

    /// <summary>Function tools offered to the model via session.update (empty = none).</summary>
    public IReadOnlyList<RealtimeToolDefinition> Tools { get; init; } = Array.Empty<RealtimeToolDefinition>();

    /// <summary>
    /// Executes a tool call and returns the JSON output sent back as function_call_output.
    /// Required when <see cref="Tools"/> is non-empty.
    /// </summary>
    public Func<RealtimeToolCall, Task<string>>? ToolHandler { get; init; }
}

/// <summary>
/// A standalone real-time voice conversation: owns its websocket, audio playback, and
/// (for pushToTalk/continuous) microphone streaming. Not routed through the removed
/// suggestion pipeline.
/// </summary>
public interface IRealtimeVoiceConversation : IAsyncDisposable
{
    RealtimeVoiceState State { get; }

    /// <summary>Raised with incremental assistant caption text (spoken-reply transcript).</summary>
    event EventHandler<string>? AssistantTextAvailable;

    event EventHandler<RealtimeVoiceState>? StateChanged;

    /// <summary>Raised with a human-readable message when a turn or the server reports an error.</summary>
    event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// Raised when a voice user turn's text is committed — hybrid mode's local STT result.
    /// Lets the UI show the user's own words in the conversation. Not raised for
    /// <see cref="SendUserTextAsync"/> (the caller already has that text).
    /// </summary>
    event EventHandler<string>? UserTextCommitted;

    /// <summary>Raised when the assistant finishes a reply (server response.done).</summary>
    event EventHandler? ResponseCompleted;

    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a typed user message as text over the same channel hybrid voice uses
    /// (conversation.item.create + response.create). No audio is involved.
    /// </summary>
    Task SendUserTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Begin a user turn (key/button down). Hybrid + pushToTalk only.</summary>
    void PushToTalkDown();

    /// <summary>End a user turn (key/button up). Hybrid transcribes locally; pushToTalk commits audio.</summary>
    void PushToTalkUp();

    Task StopAsync(CancellationToken cancellationToken = default);
}
