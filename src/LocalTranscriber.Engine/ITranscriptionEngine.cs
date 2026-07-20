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
    /// <summary>Clears a per-event speaker override, reverting the line to alias/known/baked resolution. Undoes <see cref="OverrideEventSpeakerAsync"/>. Returns false when storage is unavailable.</summary>
    Task<bool> ClearEventSpeakerOverrideAsync(string sessionId, string eventId, CancellationToken cancellationToken = default);
    /// <summary>Removes the session alias for a session speaker, reverting its display name to the baked label. Undoes the alias written by <see cref="NameSessionSpeakerAsync"/> (the enrolled voice sample is left in place). Returns false when storage is unavailable.</summary>
    Task<bool> ClearSessionSpeakerAliasAsync(string sessionId, string sessionSpeakerId, CancellationToken cancellationToken = default);
    /// <summary>Deletes a single transcript line (event) from storage. The append-only .txt/.jsonl files are left untouched as a raw log. Undoable via <see cref="RestoreEventAsync"/>. Returns false when storage is unavailable.</summary>
    Task<bool> DeleteEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default);
    /// <summary>Re-inserts a previously deleted transcript line. Undoes <see cref="DeleteEventAsync"/>. Returns false when storage is unavailable.</summary>
    Task<bool> RestoreEventAsync(TranscriptEvent transcriptEvent, CancellationToken cancellationToken = default);
    /// <summary>Persists a human-readable title for a session. Silently no-ops when storage is unavailable.</summary>
    Task UpdateSessionTitleAsync(string sessionId, string? title, CancellationToken cancellationToken = default);
}
