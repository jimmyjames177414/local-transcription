using LocalTranscriber.Shared;

namespace LocalTranscriber.Engine;

public interface ITranscriptionEngine
{
    Task StartAsync(TranscriptionSessionOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task<TranscriptionSessionStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<TranscriptEvent> StreamEventsAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns unnamed session speakers detected so far (excludes mic / already-enrolled voices).</summary>
    Task<IReadOnlyList<SessionSpeakerInfo>> ListSessionSpeakersAsync(CancellationToken cancellationToken = default);
    /// <summary>Enrolls a session speaker by name from their captured voice embeddings and writes an alias. Returns false when no session is active or the label has no captured embeddings.</summary>
    Task<bool> NameSessionSpeakerAsync(string sessionLabel, string newName, CancellationToken cancellationToken = default);
    /// <summary>Renames a globally-enrolled speaker (one already named in a previous session). Returns false when the store is unavailable or the name is not found.</summary>
    Task<bool> RenameKnownSpeakerAsync(string oldName, string newName, CancellationToken cancellationToken = default);
    /// <summary>Writes a per-event speaker override ("just this line"). Does not re-enroll the voice or change the global enrollment. Returns false when storage is unavailable.</summary>
    Task<bool> OverrideEventSpeakerAsync(string sessionId, string eventId, string newName, CancellationToken cancellationToken = default);
    /// <summary>Persists a human-readable title for a session. Silently no-ops when storage is unavailable.</summary>
    Task UpdateSessionTitleAsync(string sessionId, string? title, CancellationToken cancellationToken = default);
}
