using System.Text.Json;
using System.Text.Json.Serialization;
using LocalTranscriber.Storage;
using Microsoft.Data.Sqlite;

namespace LocalTranscriber.Agent;

/// <summary>Receives suggestions and summary updates as the agent produces them.</summary>
public interface IAgentSuggestionSink
{
    Task WriteAsync(AgentSuggestion suggestion, CancellationToken cancellationToken = default);
    Task UpdateSummaryAsync(string? sessionId, string runningSummary, CancellationToken cancellationToken = default);
}

/// <summary>Reads stored suggestions back (CLI/MCP/UI).</summary>
public interface IAgentSuggestionStore
{
    Task<IReadOnlyList<AgentSuggestion>> GetRecentAsync(int count, bool includeDismissed = false, CancellationToken cancellationToken = default);
    Task<bool> DismissAsync(string suggestionId, CancellationToken cancellationToken = default);
    Task<string?> GetRunningSummaryAsync(CancellationToken cancellationToken = default);
}

public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>Appends one JSON object per suggestion to output/agent/suggestions.jsonl.</summary>
public sealed class JsonlAgentSuggestionWriter
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonlAgentSuggestionWriter(string agentOutputFolder)
    {
        Directory.CreateDirectory(agentOutputFolder);
        _path = Path.Combine(agentOutputFolder, "suggestions.jsonl");
    }

    public async Task WriteAsync(AgentSuggestion suggestion, CancellationToken cancellationToken = default)
    {
        string line = JsonSerializer.Serialize(suggestion, AgentJson.Options) + Environment.NewLine;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_path, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Regenerates meeting-summary.md, action-items.md, and risks.md from accumulated state.
/// </summary>
public sealed class MarkdownAgentOutputWriter
{
    private readonly string _folder;
    private readonly List<AgentSuggestion> _actionItems = new();
    private readonly List<AgentSuggestion> _risks = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MarkdownAgentOutputWriter(string agentOutputFolder)
    {
        _folder = agentOutputFolder;
        Directory.CreateDirectory(agentOutputFolder);
    }

    public async Task WriteAsync(AgentSuggestion suggestion, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (suggestion.Type)
            {
                case AgentSuggestionType.ActionItem:
                    _actionItems.Add(suggestion);
                    await WriteListAsync("action-items.md", "Action Items", _actionItems, cancellationToken).ConfigureAwait(false);
                    break;
                case AgentSuggestionType.Risk:
                case AgentSuggestionType.Blocker:
                    _risks.Add(suggestion);
                    await WriteListAsync("risks.md", "Risks and Blockers", _risks, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateSummaryAsync(string? sessionId, string runningSummary, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runningSummary))
        {
            return;
        }

        string content = $"# Meeting Summary\n\nSession: {sessionId ?? "-"}\nUpdated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\n\n{runningSummary}\n";
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_folder, "meeting-summary.md"), content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task WriteListAsync(string fileName, string heading, List<AgentSuggestion> items, CancellationToken cancellationToken)
    {
        var lines = new List<string> { $"# {heading}", "" };
        foreach (var s in items)
        {
            string speaker = s.RelatedSpeaker is null ? "" : $" (from {s.RelatedSpeaker})";
            lines.Add($"- [{s.Priority}] {s.Title}{speaker}: {s.Message}");
        }
        await File.WriteAllTextAsync(Path.Combine(_folder, fileName), string.Join(Environment.NewLine, lines) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>SQLite persistence for suggestions + agent state (schema in SqliteDatabase).</summary>
public sealed class SqliteAgentSuggestionStore : IAgentSuggestionStore
{
    private readonly SqliteDatabase _db;

    public SqliteAgentSuggestionStore(SqliteDatabase db) => _db = db;

    public async Task InsertAsync(AgentSuggestion s, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO agent_suggestions
            (id, session_id, created_at, suggestion_type, priority, title, message, related_speaker, related_transcript_event_id, source, confidence, is_dismissed)
            VALUES ($id, $session, $created, $type, $priority, $title, $message, $speaker, $eventId, $source, $confidence, $dismissed)
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$session", (object?)s.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", s.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$type", s.Type.ToString());
        cmd.Parameters.AddWithValue("$priority", s.Priority.ToString());
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$message", s.Message);
        cmd.Parameters.AddWithValue("$speaker", (object?)s.RelatedSpeaker ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$eventId", (object?)s.RelatedTranscriptEventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", s.Source);
        cmd.Parameters.AddWithValue("$confidence", (object?)s.Confidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dismissed", s.IsDismissed ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AgentSuggestion>> GetRecentAsync(int count, bool includeDismissed = false, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, session_id, created_at, suggestion_type, priority, title, message, related_speaker, related_transcript_event_id, source, confidence, is_dismissed
            FROM agent_suggestions
            {(includeDismissed ? "" : "WHERE is_dismissed = 0")}
            ORDER BY created_at DESC LIMIT $count
            """;
        cmd.Parameters.AddWithValue("$count", count);

        var result = new List<AgentSuggestion>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new AgentSuggestion(
                Id: reader.GetString(0),
                SessionId: reader.IsDBNull(1) ? null : reader.GetString(1),
                CreatedAt: DateTimeOffset.Parse(reader.GetString(2)),
                Type: Enum.TryParse<AgentSuggestionType>(reader.GetString(3), out var t) ? t : AgentSuggestionType.ContextReminder,
                Priority: Enum.TryParse<AgentSuggestionPriority>(reader.GetString(4), out var p) ? p : AgentSuggestionPriority.Low,
                Title: reader.GetString(5),
                Message: reader.GetString(6),
                RelatedSpeaker: reader.IsDBNull(7) ? null : reader.GetString(7),
                RelatedTranscriptEventId: reader.IsDBNull(8) ? null : reader.GetString(8),
                Source: reader.GetString(9),
                Confidence: reader.IsDBNull(10) ? null : reader.GetDouble(10),
                IsDismissed: reader.GetInt32(11) != 0));
        }
        return result;
    }

    public async Task<bool> DismissAsync(string suggestionId, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE agent_suggestions SET is_dismissed = 1 WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", suggestionId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task UpdateStateAsync(string? sessionId, string runningSummary, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO agent_state (id, session_id, updated_at, running_summary, last_transcript_event_id, last_transcript_timestamp)
            VALUES ('current', $session, $updated, $summary, NULL, NULL)
            """;
        cmd.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$summary", runningSummary);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetRunningSummaryAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT running_summary FROM agent_state WHERE id = 'current'";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string;
    }
}

/// <summary>Fans suggestions out to jsonl + markdown + SQLite.</summary>
public sealed class CompositeAgentSuggestionSink : IAgentSuggestionSink
{
    private readonly JsonlAgentSuggestionWriter _jsonl;
    private readonly MarkdownAgentOutputWriter _markdown;
    private readonly SqliteAgentSuggestionStore? _sqlite;

    public CompositeAgentSuggestionSink(string agentOutputFolder, SqliteAgentSuggestionStore? sqlite = null)
    {
        _jsonl = new JsonlAgentSuggestionWriter(agentOutputFolder);
        _markdown = new MarkdownAgentOutputWriter(agentOutputFolder);
        _sqlite = sqlite;
    }

    public async Task WriteAsync(AgentSuggestion suggestion, CancellationToken cancellationToken = default)
    {
        await _jsonl.WriteAsync(suggestion, cancellationToken).ConfigureAwait(false);
        await _markdown.WriteAsync(suggestion, cancellationToken).ConfigureAwait(false);
        if (_sqlite is not null)
        {
            await _sqlite.InsertAsync(suggestion, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateSummaryAsync(string? sessionId, string runningSummary, CancellationToken cancellationToken = default)
    {
        await _markdown.UpdateSummaryAsync(sessionId, runningSummary, cancellationToken).ConfigureAwait(false);
        if (_sqlite is not null && !string.IsNullOrWhiteSpace(runningSummary))
        {
            await _sqlite.UpdateStateAsync(sessionId, runningSummary, cancellationToken).ConfigureAwait(false);
        }
    }
}
