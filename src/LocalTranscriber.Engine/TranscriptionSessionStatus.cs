namespace LocalTranscriber.Engine;

public sealed record TranscriptionSessionStatus
{
    public TranscriptionSessionState State { get; init; } = TranscriptionSessionState.NotStarted;
    public string? SessionId { get; init; }
    public string? OutputTextPath { get; init; }
    public string? OutputJsonlPath { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? LastEventAt { get; init; }
    public long EventCount { get; init; }
    public string? Error { get; init; }
}
