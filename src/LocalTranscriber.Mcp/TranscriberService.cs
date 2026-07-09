using LocalTranscriber.Engine;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Mcp;

/// <summary>
/// Holds the engine and current-session state for the MCP process, and enforces
/// that transcript file access stays inside the configured transcript folder.
/// </summary>
public sealed class TranscriberService
{
    private readonly ConfigService _configService;
    private readonly SqliteDatabase _db;

    public TranscriberService(ConfigService configService)
    {
        _configService = configService;
        var config = configService.Load();
        _db = new SqliteDatabase(config.DatabasePath);
        Engine = new FakeTranscriptionEngine(new SqliteSessionStore(_db), new SqliteTranscriptEventStore(_db));
        SessionStore = new SqliteSessionStore(_db);
        SpeakerStore = new SqliteKnownSpeakerStore(_db);
    }

    public ITranscriptionEngine Engine { get; }
    public ISessionStore SessionStore { get; }
    public IKnownSpeakerStore SpeakerStore { get; }

    public TranscriptionSessionOptions? CurrentOptions { get; private set; }

    public string TranscriptFolder => Path.GetFullPath(_configService.Load().TranscriptFolder);

    public void SetTranscriptFolder(string folder)
    {
        var config = _configService.Load();
        config.TranscriptFolder = folder;
        _configService.Save(config);
    }

    public async Task<TranscriptionSessionOptions> StartFakeSessionAsync(CancellationToken cancellationToken = default)
    {
        string folder = TranscriptFolder;
        Directory.CreateDirectory(folder);
        string baseName = $"session-{DateTime.Now:yyyyMMdd-HHmmss}";
        var options = new TranscriptionSessionOptions
        {
            OutputTextPath = Path.Combine(folder, baseName + ".txt"),
            OutputJsonlPath = Path.Combine(folder, baseName + ".jsonl")
        };
        await Engine.StartAsync(options, cancellationToken);
        CurrentOptions = options;
        return options;
    }

    /// <summary>
    /// Resolves a transcript path, restricted to the configured transcript folder.
    /// Returns null when the path escapes the folder.
    /// </summary>
    public string? ResolveTranscriptPath(string candidate)
        => SafePathValidator.ResolveInsideRoot(TranscriptFolder, candidate);

    /// <summary>
    /// Path of the transcript to read by default: current session, else newest .txt in folder.
    /// </summary>
    public string? DefaultTranscriptPath()
    {
        if (CurrentOptions is not null && File.Exists(CurrentOptions.OutputTextPath))
        {
            return Path.GetFullPath(CurrentOptions.OutputTextPath);
        }

        string folder = TranscriptFolder;
        if (!Directory.Exists(folder))
        {
            return null;
        }

        return Directory.EnumerateFiles(folder, "*.txt")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
