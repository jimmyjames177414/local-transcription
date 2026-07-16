using Microsoft.Data.Sqlite;

namespace LocalTranscriber.Storage;

/// <summary>
/// Owns the SQLite connection string and schema. All Sqlite* stores share one instance.
/// </summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;
    private bool _initialized;
    private readonly object _initLock = new();

    public string DatabasePath { get; }

    public SqliteDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        EnsureInitialized();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_initLock)
        {
            if (_initialized)
            {
                return;
            }

            string? dir = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
            MigrateSessionsTitle(connection);
            _initialized = true;
        }
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS known_speakers (
            id TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            last_seen_at TEXT,
            sample_count INTEGER NOT NULL DEFAULT 0,
            notes TEXT
        );

        CREATE TABLE IF NOT EXISTS speaker_embeddings (
            id TEXT PRIMARY KEY,
            speaker_id TEXT NOT NULL,
            embedding BLOB NOT NULL,
            dimensions INTEGER NOT NULL,
            model_name TEXT NOT NULL,
            created_at TEXT NOT NULL,
            source_session_id TEXT,
            FOREIGN KEY (speaker_id) REFERENCES known_speakers(id)
        );

        CREATE TABLE IF NOT EXISTS sessions (
            id TEXT PRIMARY KEY,
            started_at TEXT NOT NULL,
            ended_at TEXT,
            output_text_path TEXT NOT NULL,
            output_jsonl_path TEXT NOT NULL,
            status TEXT NOT NULL,
            title TEXT
        );

        CREATE TABLE IF NOT EXISTS transcript_events (
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            timestamp TEXT NOT NULL,
            speaker_id TEXT,
            speaker_name TEXT NOT NULL,
            source TEXT NOT NULL,
            text TEXT NOT NULL,
            confidence REAL,
            start_ms INTEGER,
            end_ms INTEGER,
            FOREIGN KEY (session_id) REFERENCES sessions(id)
        );

        CREATE INDEX IF NOT EXISTS idx_transcript_events_session ON transcript_events(session_id);
        CREATE INDEX IF NOT EXISTS idx_speaker_embeddings_speaker ON speaker_embeddings(speaker_id);

        CREATE TABLE IF NOT EXISTS agent_suggestions (
            id TEXT PRIMARY KEY,
            session_id TEXT,
            created_at TEXT NOT NULL,
            suggestion_type TEXT NOT NULL,
            priority TEXT NOT NULL,
            title TEXT NOT NULL,
            message TEXT NOT NULL,
            related_speaker TEXT,
            related_transcript_event_id TEXT,
            source TEXT NOT NULL,
            confidence REAL,
            is_dismissed INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS agent_state (
            id TEXT PRIMARY KEY,
            session_id TEXT,
            updated_at TEXT NOT NULL,
            running_summary TEXT,
            last_transcript_event_id TEXT,
            last_transcript_timestamp TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_agent_suggestions_created ON agent_suggestions(created_at);

        CREATE TABLE IF NOT EXISTS speaker_aliases (
            session_id TEXT NOT NULL,
            session_speaker_id TEXT NOT NULL,
            known_speaker_id TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (session_id, session_speaker_id)
        );

        CREATE TABLE IF NOT EXISTS event_speaker_overrides (
            session_id TEXT NOT NULL,
            event_id TEXT NOT NULL,
            display_name TEXT NOT NULL,
            known_speaker_id TEXT,
            created_at TEXT NOT NULL,
            PRIMARY KEY (session_id, event_id)
        );
        """;

    /// <summary>
    /// Databases created before the sessions screen lack the title column; CREATE IF NOT EXISTS
    /// won't add it, so probe and ALTER. Idempotent and fast (runs once per SqliteDatabase).
    /// </summary>
    private static void MigrateSessionsTitle(SqliteConnection connection)
    {
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name = 'title'";
        long count = (long)(probe.ExecuteScalar() ?? 0L);
        if (count == 0)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE sessions ADD COLUMN title TEXT";
            alter.ExecuteNonQuery();
        }
    }
}
