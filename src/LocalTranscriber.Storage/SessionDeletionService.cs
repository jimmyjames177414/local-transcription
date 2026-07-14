using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage;

/// <summary>A file a session delete would remove, for the confirm dialog's exact-file list.</summary>
public sealed record SessionFileInfo(string Path, string Name, long SizeBytes);

/// <summary>
/// Deletes a recorded session: its transcript files, notes file, SQLite rows, and (optionally)
/// its exported minutes files. Voice memory (known_speakers/embeddings) is never touched.
/// Shared by the app's delete dialog and the CLI delete-session command.
/// </summary>
public sealed class SessionDeletionService
{
    private readonly ISessionStore _sessions;
    private readonly ITranscriptEventStore _events;
    private readonly AppConfig _config;

    public SessionDeletionService(AppConfig config, ISessionStore? sessions = null, ITranscriptEventStore? events = null)
    {
        _config = config;
        var db = new SqliteDatabase(config.DatabasePath);
        _sessions = sessions ?? new SqliteSessionStore(db);
        _events = events ?? new SqliteTranscriptEventStore(db);
    }

    /// <summary>Files that exist on disk for this session (transcript .txt/.jsonl + notes).</summary>
    public async Task<IReadOnlyList<SessionFileInfo>> ListFilesAsync(string sessionId, CancellationToken ct = default)
    {
        var session = await _sessions.GetAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found.");

        var candidates = new[]
        {
            session.OutputTextPath,
            session.OutputJsonlPath,
            Path.Combine(_config.Agent.AgentOutputFolder, $"notes-{sessionId}.md")
        };

        return candidates
            .Where(File.Exists)
            .Select(p =>
            {
                var info = new FileInfo(p);
                return new SessionFileInfo(info.FullName, info.Name, info.Length);
            })
            .ToList();
    }

    /// <summary>Minutes files previously exported for this session (empty when none/sync disabled).</summary>
    public string[] FindMinutesFiles(string sessionId)
        => MinutesExporter.FindExportedFiles(_config.MinutesExport.Folder, sessionId);

    /// <summary>Removes files first (missing files are fine), then event rows, then the session row.</summary>
    public async Task DeleteAsync(string sessionId, bool alsoRemoveMinutes, CancellationToken ct = default)
    {
        foreach (var file in await ListFilesAsync(sessionId, ct).ConfigureAwait(false))
        {
            TryDelete(file.Path);
        }

        if (alsoRemoveMinutes)
        {
            foreach (string path in FindMinutesFiles(sessionId))
            {
                TryDelete(path);
            }
        }

        await _events.DeleteBySessionAsync(sessionId, ct).ConfigureAwait(false);
        await _sessions.DeleteAsync(sessionId, ct).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("storage", $"Could not delete {path}: {ex.Message}");
        }
    }
}
