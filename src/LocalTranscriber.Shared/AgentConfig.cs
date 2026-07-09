namespace LocalTranscriber.Shared;

/// <summary>
/// Configuration for the optional Live Meeting AI Sidecar. Disabled by default —
/// the offline transcriber must work with none of this configured.
/// </summary>
public sealed class AgentConfig
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "SilentObserver";
    public string Provider { get; set; } = "fake";
    public string ContextFolder { get; set; } = "context";
    public string AgentOutputFolder { get; set; } = Path.Combine("output", "agent");
    public int RollingWindowMinutes { get; set; } = 5;
    public int SuggestionIntervalSeconds { get; set; } = 10;
    public int MaxTranscriptEventsPerPrompt { get; set; } = 80;
    public int MaxContextCharacters { get; set; } = 20000;
    public List<string> RequiredContextFiles { get; set; } = new() { "codename-summary.md" };
    public OpenAIAgentConfig OpenAI { get; set; } = new();
    public RealtimeAgentConfig Realtime { get; set; } = new();
    public AgentVoiceConfig Voice { get; set; } = new();
    public MeetingParticipantConfig MeetingParticipant { get; set; } = new();
}

public sealed class OpenAIAgentConfig
{
    public bool Enabled { get; set; }
    public string ApiKeyEnvironmentVariable { get; set; } = "OPENAI_API_KEY";
    public string Model { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0.2;
    public int MaxOutputTokens { get; set; } = 700;
}

public sealed class RealtimeAgentConfig
{
    public bool Enabled { get; set; }
    public string ApiKeyEnvironmentVariable { get; set; } = "OPENAI_API_KEY";
    public string Model { get; set; } = "gpt-realtime-mini";
    public string Transport { get; set; } = "websocket";
    public bool VoiceOutputEnabled { get; set; }

    /// <summary>Must stay false: raw audio is never sent to the provider.</summary>
    public bool SendAudio { get; set; }
}

public sealed class AgentVoiceConfig
{
    public bool Enabled { get; set; }
    public string? OutputDeviceId { get; set; }
    public string MinimumPriorityToSpeak { get; set; } = "High";
    public List<string> SpeakOnlyInModes { get; set; } = new() { "PrivateCoach", "InterruptWhenImportant" };
}

public sealed class MeetingParticipantConfig
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "disabled";
    public bool RequiresExplicitUserAction { get; set; } = true;
}
