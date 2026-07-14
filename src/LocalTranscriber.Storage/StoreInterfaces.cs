using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage;

public interface ISessionStore
{
    Task CreateAsync(SessionRecord session, CancellationToken cancellationToken = default);
    Task EndAsync(string sessionId, DateTimeOffset endedAt, string status, CancellationToken cancellationToken = default);
    Task<SessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets or clears the user-facing session title.</summary>
    Task UpdateTitleAsync(string sessionId, string? title, CancellationToken cancellationToken = default);

    /// <summary>Deletes the session row only; transcript events go through ITranscriptEventStore.</summary>
    Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>All sessions newest-first with per-session event counts and distinct speaker names.</summary>
    Task<IReadOnlyList<SessionSummary>> ListSummariesAsync(CancellationToken cancellationToken = default);
}

public interface ITranscriptEventStore
{
    Task InsertAsync(TranscriptEvent transcriptEvent, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TranscriptEvent>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Deletes all events belonging to a session.</summary>
    Task DeleteBySessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Ids of sessions whose transcript text contains the query (case-insensitive LIKE).</summary>
    Task<IReadOnlyList<string>> SearchSessionIdsAsync(string text, CancellationToken cancellationToken = default);
}

public interface IKnownSpeakerStore
{
    Task<KnownSpeaker> CreateAsync(string displayName, string? notes = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnownSpeaker>> ListAsync(CancellationToken cancellationToken = default);
    Task<KnownSpeaker?> GetByNameAsync(string displayName, CancellationToken cancellationToken = default);
    Task<bool> RenameAsync(string fromName, string toName, CancellationToken cancellationToken = default);
    Task<bool> ForgetAsync(string displayName, CancellationToken cancellationToken = default);
    Task MarkSeenAsync(string speakerId, DateTimeOffset seenAt, int sampleCountDelta = 0, CancellationToken cancellationToken = default);
}

public interface ISpeakerEmbeddingStore
{
    Task AddAsync(StoredEmbedding embedding, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredEmbedding>> ListBySpeakerAsync(string speakerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredEmbedding>> ListAllAsync(CancellationToken cancellationToken = default);
    Task DeleteBySpeakerAsync(string speakerId, CancellationToken cancellationToken = default);
}

public interface ISpeakerAliasStore
{
    Task UpsertAsync(string sessionId, string sessionSpeakerId, string knownSpeakerId, CancellationToken cancellationToken = default);
    Task<string?> ResolveAsync(string sessionId, string sessionSpeakerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(string SessionSpeakerId, string KnownSpeakerId)>> ListForSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
