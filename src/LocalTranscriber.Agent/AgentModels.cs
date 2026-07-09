using LocalTranscriber.Shared;

namespace LocalTranscriber.Agent;

public enum AgentMode
{
    Off,
    SilentObserver,
    PrivateCoach,
    HotkeyOnly,
    InterruptWhenImportant,
    ExperimentalMeetingParticipant
}

public enum AgentSuggestionType
{
    ActionItem,
    Decision,
    Risk,
    Blocker,
    Contradiction,
    SuggestedResponse,
    ContextReminder,
    QuestionToAsk,
    SummaryUpdate
}

public enum AgentSuggestionPriority
{
    Low,
    Medium,
    High,
    Critical
}

public sealed record AgentSuggestion(
    string Id,
    string? SessionId,
    DateTimeOffset CreatedAt,
    AgentSuggestionType Type,
    AgentSuggestionPriority Priority,
    string Title,
    string Message,
    string Source,
    string? RelatedSpeaker = null,
    string? RelatedTranscriptEventId = null,
    double? Confidence = null,
    bool IsDismissed = false
);

public sealed record AgentProviderRequest
{
    public required IReadOnlyList<TranscriptEvent> WindowEvents { get; init; }
    public required string ContextSummary { get; init; }
    public string RunningSummary { get; init; } = "";
    public IReadOnlyList<string> KnownSpeakers { get; init; } = Array.Empty<string>();
    public AgentMode Mode { get; init; } = AgentMode.SilentObserver;
    public string? SessionId { get; init; }

    /// <summary>Set for on-demand questions (HotkeyOnly / ask); null for periodic analysis.</summary>
    public string? UserQuestion { get; init; }
}

public sealed record AgentProviderResult(
    IReadOnlyList<AgentSuggestion> Suggestions,
    string? RunningSummaryUpdate
);

public interface IMeetingAgentProvider
{
    string Name { get; }
    Task<AgentProviderResult> AnalyzeAsync(AgentProviderRequest request, CancellationToken cancellationToken = default);
}

public enum MeetingAgentState
{
    NotStarted,
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted
}

public sealed record MeetingAgentOptions
{
    public required string TranscriptJsonlPath { get; init; }
    public required string ContextFolder { get; init; }
    public required string AgentOutputFolder { get; init; }
    public AgentMode Mode { get; init; } = AgentMode.SilentObserver;
    public int RollingWindowMinutes { get; init; } = 5;
    public int SuggestionIntervalSeconds { get; init; } = 10;
    public int MaxTranscriptEventsPerPrompt { get; init; } = 80;
    public int MaxContextCharacters { get; init; } = 20000;
    public IReadOnlyList<string> RequiredContextFiles { get; init; } = new[] { "codename-summary.md" };
    public bool TailFromStart { get; init; } = true;
    public string? SessionId { get; init; }
}

public sealed record MeetingAgentStatus
{
    public MeetingAgentState State { get; init; } = MeetingAgentState.NotStarted;
    public AgentMode Mode { get; init; } = AgentMode.SilentObserver;
    public string? Provider { get; init; }
    public string? TranscriptPath { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? LastEventAt { get; init; }
    public DateTimeOffset? LastAnalysisAt { get; init; }
    public long EventsSeen { get; init; }
    public long SuggestionsEmitted { get; init; }
    public string RunningSummary { get; init; } = "";
    public string? Error { get; init; }
}

public interface IMeetingAgent
{
    Task StartAsync(MeetingAgentOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<MeetingAgentStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentSuggestion> StreamSuggestionsAsync(CancellationToken cancellationToken = default);
}
