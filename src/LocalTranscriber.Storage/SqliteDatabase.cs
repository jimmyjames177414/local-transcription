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
            status TEXT NOT NULL
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
        """;
}
