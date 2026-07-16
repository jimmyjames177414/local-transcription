namespace LocalTranscriber.Storage;

/// <summary>
/// Repairs sessions the app abandoned without a clean stop. A session is created with
/// <c>status="recording"</c> and only finalized at the end of the engine's stop path; if the
/// process is killed, crashes, or is force-closed first, the row is left permanently in
/// <c>recording</c> with a null <c>ended_at</c>. That makes an otherwise-intact meeting look
/// truncated in the Sessions list (duration collapses to "1m", minutes never export).
///
/// Run once at startup — when nothing is recording yet — to finalize every such orphan:
/// backfill <c>ended_at</c> from the session's last transcript event (or its start time when it
/// has none) and mark it <c>interrupted</c>. The transcript data itself is never touched; it is
/// already safe in the .txt/.jsonl files and the transcript_events table.
/// </summary>
public sealed class SessionRecoveryService
{
    /// <summary>Status given to a session that was recording when the app went away.</summary>
    public const string InterruptedStatus = "interrupted";

    private readonly ISessionStore _sessions;
    private readonly ITranscriptEventStore _events;

    public SessionRecoveryService(ISessionStore sessions, ITranscriptEventStore events)
    {
        _sessions = sessions;
        _events = events;
    }

    /// <summary>
    /// Finalizes any session still marked <c>recording</c>. Returns the ids it repaired. Safe to
    /// call only when no live session is in flight (i.e. at startup) — it treats every
    /// <c>recording</c> row as orphaned.
    /// </summary>
    public async Task<IReadOnlyList<string>> RecoverOrphanedSessionsAsync(CancellationToken cancellationToken = default)
    {
        var all = await _sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        var recovered = new List<string>();

        foreach (var session in all)
        {
            if (!string.Equals(session.Status, "recording", StringComparison.Ordinal))
            {
                continue;
            }

            var lastEvent = await _events.GetLastTimestampAsync(session.Id, cancellationToken).ConfigureAwait(false);
            var endedAt = lastEvent ?? session.StartedAt;
            await _sessions.EndAsync(session.Id, endedAt, InterruptedStatus, cancellationToken).ConfigureAwait(false);
            recovered.Add(session.Id);
        }

        return recovered;
    }
}
