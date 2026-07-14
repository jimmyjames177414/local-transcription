using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage;

/// <summary>
/// Resolves a past session (by id, or the most recent) and exports it in minutes format.
/// One composition shared by the CLI command and the MCP tool.
/// </summary>
public sealed class MinutesExportService
{
    private readonly ISessionStore _sessions;
    private readonly ITranscriptEventStore _events;
    private readonly AppConfig _config;

    public MinutesExportService(AppConfig config, ISessionStore? sessions = null, ITranscriptEventStore? events = null)
    {
        _config = config;
        var db = new SqliteDatabase(config.DatabasePath);
        _sessions = sessions ?? new SqliteSessionStore(db);
        _events = events ?? new SqliteTranscriptEventStore(db);
    }

    /// <summary>Exports and returns the written file path. Throws with a friendly message when the session is unknown.</summary>
    public async Task<string> ExportAsync(
        string? sessionId = null,
        string? outputFolder = null,
        string? title = null,
        CancellationToken ct = default)
    {
        var all = await _sessions.ListAsync(ct).ConfigureAwait(false);
        var session = sessionId is null
            ? all.OrderByDescending(s => s.StartedAt).FirstOrDefault()
            : all.FirstOrDefault(s => s.Id == sessionId || s.Id.StartsWith(sessionId, StringComparison.OrdinalIgnoreCase));

        if (session is null)
        {
            throw new InvalidOperationException(sessionId is null
                ? "No recorded sessions found."
                : $"Session '{sessionId}' not found. Use 'localtranscriber sessions' to list ids.");
        }

        var events = await _events.ListBySessionAsync(session.Id, ct).ConfigureAwait(false);
        var notes = TryLoadNotes(session.Id);
        return MinutesExporter.Export(session, events, notes, outputFolder ?? _config.MinutesExport.Folder, title ?? session.Title);
    }

    /// <summary>
    /// Exports every finished session that has no minutes file yet ("Sync all now").
    /// Returns the written paths.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExportMissingAsync(CancellationToken ct = default)
    {
        var all = await _sessions.ListAsync(ct).ConfigureAwait(false);
        var written = new List<string>();
        foreach (var session in all.Where(s => s.Status != "recording"))
        {
            if (MinutesExporter.FindExportedFiles(_config.MinutesExport.Folder, session.Id).Length > 0)
            {
                continue;
            }

            var events = await _events.ListBySessionAsync(session.Id, ct).ConfigureAwait(false);
            written.Add(MinutesExporter.Export(session, events, TryLoadNotes(session.Id), _config.MinutesExport.Folder, session.Title));
        }

        return written;
    }

    private NotesDocument? TryLoadNotes(string sessionId)
    {
        try
        {
            string path = Path.Combine(_config.Agent.AgentOutputFolder, $"notes-{sessionId}.md");
            return File.Exists(path) ? NotesDocument.Parse(File.ReadAllText(path), sessionId) : null;
        }
        catch
        {
            return null;
        }
    }
}
