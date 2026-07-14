namespace LocalTranscriber.Storage;

public sealed record SessionRecord(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string OutputTextPath,
    string OutputJsonlPath,
    string Status,
    string? Title = null
);

/// <summary>A session plus cheap list metadata (one query for the whole sessions screen).</summary>
public sealed record SessionSummary(
    SessionRecord Session,
    int EventCount,
    IReadOnlyList<string> SpeakerNames
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
