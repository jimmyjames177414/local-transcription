namespace LocalTranscriber.Storage;

public sealed record SessionRecord(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string OutputTextPath,
    string OutputJsonlPath,
    string Status
);

public sealed record KnownSpeaker(
    string Id,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastSeenAt,
    int SampleCount,
    string? Notes
);

public sealed record StoredEmbedding(
    string Id,
    string SpeakerId,
    byte[] Embedding,
    int Dimensions,
    string ModelName,
    DateTimeOffset CreatedAt,
    string? SourceSessionId
);
