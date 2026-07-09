using LocalTranscriber.Shared;
using Microsoft.Data.Sqlite;

namespace LocalTranscriber.Storage;

public sealed class SqliteSessionStore : ISessionStore
{
    private readonly SqliteDatabase _db;

    public SqliteSessionStore(SqliteDatabase db) => _db = db;

    public async Task CreateAsync(SessionRecord session, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (id, started_at, ended_at, output_text_path, output_jsonl_path, status)
            VALUES ($id, $started, $ended, $txt, $jsonl, $status)
            """;
        cmd.Parameters.AddWithValue("$id", session.Id);
        cmd.Parameters.AddWithValue("$started", session.StartedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$ended", (object?)session.EndedAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$txt", session.OutputTextPath);
        cmd.Parameters.AddWithValue("$jsonl", session.OutputJsonlPath);
        cmd.Parameters.AddWithValue("$status", session.Status);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EndAsync(string sessionId, DateTimeOffset endedAt, string status, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET ended_at = $ended, status = $status WHERE id = $id";
        cmd.Parameters.AddWithValue("$ended", endedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$id", sessionId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, started_at, ended_at, output_text_path, output_jsonl_path, status FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSession(reader) : null;
    }

    public async Task<IReadOnlyList<SessionRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, started_at, ended_at, output_text_path, output_jsonl_path, status FROM sessions ORDER BY started_at DESC";
        var result = new List<SessionRecord>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadSession(reader));
        }
        return result;
    }

    private static SessionRecord ReadSession(SqliteDataReader reader) => new(
        reader.GetString(0),
        DateTimeOffset.Parse(reader.GetString(1)),
        reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5));
}

public sealed class SqliteTranscriptEventStore : ITranscriptEventStore
{
    private readonly SqliteDatabase _db;

    public SqliteTranscriptEventStore(SqliteDatabase db) => _db = db;

    public async Task InsertAsync(TranscriptEvent e, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO transcript_events (id, session_id, timestamp, speaker_id, speaker_name, source, text, confidence, start_ms, end_ms)
            VALUES ($id, $session, $ts, $spkId, $spkName, $source, $text, $conf, $start, $end)
            """;
        cmd.Parameters.AddWithValue("$id", e.Id);
        cmd.Parameters.AddWithValue("$session", e.SessionId);
        cmd.Parameters.AddWithValue("$ts", e.Timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("$spkId", e.Speaker.SpeakerId);
        cmd.Parameters.AddWithValue("$spkName", e.Speaker.DisplayName);
        cmd.Parameters.AddWithValue("$source", e.Source.ToString());
        cmd.Parameters.AddWithValue("$text", e.Text);
        cmd.Parameters.AddWithValue("$conf", (object?)e.Confidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start", (object?)e.StartMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$end", (object?)e.EndMs ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TranscriptEvent>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, timestamp, speaker_id, speaker_name, source, text, confidence, start_ms, end_ms
            FROM transcript_events WHERE session_id = $session ORDER BY timestamp
            """;
        cmd.Parameters.AddWithValue("$session", sessionId);
        var result = new List<TranscriptEvent>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new TranscriptEvent(
                Id: reader.GetString(0),
                SessionId: reader.GetString(1),
                Timestamp: DateTimeOffset.Parse(reader.GetString(2)),
                Speaker: new SpeakerLabel(
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.GetString(4),
                    IsKnown: false),
                Source: Enum.TryParse<AudioSourceType>(reader.GetString(5), out var src) ? src : AudioSourceType.Unknown,
                Text: reader.GetString(6),
                Confidence: reader.IsDBNull(7) ? null : reader.GetDouble(7),
                StartMs: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                EndMs: reader.IsDBNull(9) ? null : reader.GetInt64(9)));
        }
        return result;
    }
}

public sealed class SqliteKnownSpeakerStore : IKnownSpeakerStore
{
    private readonly SqliteDatabase _db;

    public SqliteKnownSpeakerStore(SqliteDatabase db) => _db = db;

    public async Task<KnownSpeaker> CreateAsync(string displayName, string? notes = null, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        var speaker = new KnownSpeaker(Guid.NewGuid().ToString("N"), displayName, now, now, null, 0, notes);

        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO known_speakers (id, display_name, created_at, updated_at, last_seen_at, sample_count, notes)
            VALUES ($id, $name, $created, $updated, NULL, 0, $notes)
            """;
        cmd.Parameters.AddWithValue("$id", speaker.Id);
        cmd.Parameters.AddWithValue("$name", speaker.DisplayName);
        cmd.Parameters.AddWithValue("$created", now.ToString("o"));
        cmd.Parameters.AddWithValue("$updated", now.ToString("o"));
        cmd.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return speaker;
    }

    public async Task<IReadOnlyList<KnownSpeaker>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, display_name, created_at, updated_at, last_seen_at, sample_count, notes FROM known_speakers ORDER BY display_name";
        var result = new List<KnownSpeaker>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadSpeaker(reader));
        }
        return result;
    }

    public async Task<KnownSpeaker?> GetByNameAsync(string displayName, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, display_name, created_at, updated_at, last_seen_at, sample_count, notes FROM known_speakers WHERE display_name = $name COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$name", displayName);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSpeaker(reader) : null;
    }

    public async Task<bool> RenameAsync(string fromName, string toName, CancellationToken cancellationToken = default)
    {
        var existing = await GetByNameAsync(fromName, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false; // Unknown speaker — use the session list to name a live speaker, or speakers enroll for a WAV sample.
        }

        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE known_speakers SET display_name = $to, updated_at = $updated WHERE id = $id";
        cmd.Parameters.AddWithValue("$to", toName);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$id", existing.Id);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ForgetAsync(string displayName, CancellationToken cancellationToken = default)
    {
        var existing = await GetByNameAsync(displayName, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM speaker_embeddings WHERE speaker_id = $id; DELETE FROM known_speakers WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", existing.Id);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task MarkSeenAsync(string speakerId, DateTimeOffset seenAt, int sampleCountDelta = 0, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE known_speakers
            SET last_seen_at = $seen, sample_count = sample_count + $delta, updated_at = $seen
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$seen", seenAt.ToString("o"));
        cmd.Parameters.AddWithValue("$delta", sampleCountDelta);
        cmd.Parameters.AddWithValue("$id", speakerId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static KnownSpeaker ReadSpeaker(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        DateTimeOffset.Parse(reader.GetString(2)),
        DateTimeOffset.Parse(reader.GetString(3)),
        reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
        reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetString(6));
}

public sealed class SqliteSpeakerEmbeddingStore : ISpeakerEmbeddingStore
{
    private readonly SqliteDatabase _db;

    public SqliteSpeakerEmbeddingStore(SqliteDatabase db) => _db = db;

    public async Task AddAsync(StoredEmbedding embedding, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO speaker_embeddings (id, speaker_id, embedding, dimensions, model_name, created_at, source_session_id)
            VALUES ($id, $speaker, $embedding, $dims, $model, $created, $session)
            """;
        cmd.Parameters.AddWithValue("$id", embedding.Id);
        cmd.Parameters.AddWithValue("$speaker", embedding.SpeakerId);
        cmd.Parameters.AddWithValue("$embedding", embedding.Embedding);
        cmd.Parameters.AddWithValue("$dims", embedding.Dimensions);
        cmd.Parameters.AddWithValue("$model", embedding.ModelName);
        cmd.Parameters.AddWithValue("$created", embedding.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$session", (object?)embedding.SourceSessionId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<StoredEmbedding>> ListBySpeakerAsync(string speakerId, CancellationToken cancellationToken = default)
        => QueryAsync("WHERE speaker_id = $speaker", cmd => cmd.Parameters.AddWithValue("$speaker", speakerId), cancellationToken);

    public Task<IReadOnlyList<StoredEmbedding>> ListAllAsync(CancellationToken cancellationToken = default)
        => QueryAsync("", _ => { }, cancellationToken);

    public async Task DeleteBySpeakerAsync(string speakerId, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM speaker_embeddings WHERE speaker_id = $speaker";
        cmd.Parameters.AddWithValue("$speaker", speakerId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<StoredEmbedding>> QueryAsync(string whereClause, Action<SqliteCommand> bind, CancellationToken cancellationToken)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT id, speaker_id, embedding, dimensions, model_name, created_at, source_session_id FROM speaker_embeddings {whereClause}";
        bind(cmd);
        var result = new List<StoredEmbedding>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new StoredEmbedding(
                reader.GetString(0),
                reader.GetString(1),
                (byte[])reader.GetValue(2),
                reader.GetInt32(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return result;
    }
}

public sealed class SqliteSpeakerAliasStore : ISpeakerAliasStore
{
    private readonly SqliteDatabase _db;

    public SqliteSpeakerAliasStore(SqliteDatabase db) => _db = db;

    public async Task UpsertAsync(string sessionId, string sessionSpeakerId, string knownSpeakerId, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO speaker_aliases (session_id, session_speaker_id, known_speaker_id, created_at)
            VALUES ($sid, $ssid, $kid, $created)
            ON CONFLICT (session_id, session_speaker_id) DO UPDATE SET known_speaker_id = $kid
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$ssid", sessionSpeakerId);
        cmd.Parameters.AddWithValue("$kid", knownSpeakerId);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.Now.ToString("o"));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ResolveAsync(string sessionId, string sessionSpeakerId, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT known_speaker_id FROM speaker_aliases WHERE session_id = $sid AND session_speaker_id = $ssid";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$ssid", sessionSpeakerId);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? reader.GetString(0) : null;
    }

    public async Task<IReadOnlyList<(string SessionSpeakerId, string KnownSpeakerId)>> ListForSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var connection = _db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT session_speaker_id, known_speaker_id FROM speaker_aliases WHERE session_id = $sid";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        var result = new List<(string, string)>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }
}
