namespace LocalTranscriber.Shared;

public sealed record TranscriptEvent(
    string Id,
    string SessionId,
    DateTimeOffset Timestamp,
    SpeakerLabel Speaker,
    AudioSourceType Source,
    string Text,
    double? Confidence = null,
    long? StartMs = null,
    long? EndMs = null
);
