using LocalTranscriber.Engine;
using LocalTranscriber.Shared;
using LocalTranscriber.Speakers;
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

    private ITranscriptionEngine _current;
    private RealTranscriptionEngine? _realEngine;
    private readonly FakeTranscriptionEngine _fakeEngine;

    public TranscriberService(ConfigService configService)
    {
        _configService = configService;
        var config = configService.Load();
        _db = new SqliteDatabase(config.DatabasePath);
        _fakeEngine = new FakeTranscriptionEngine(new SqliteSessionStore(_db), new SqliteTranscriptEventStore(_db));
        _current = _fakeEngine;
        SessionStore = new SqliteSessionStore(_db);
        SpeakerStore = new SqliteKnownSpeakerStore(_db);
        Recognition = new SpeakerRecognitionService(
            SpeakerStore,
            new SqliteSpeakerEmbeddingStore(_db),
            new SpeakerMemoryOptions
            {
                MatchThreshold = config.SpeakerMatchThreshold,
                UncertainThreshold = config.SpeakerUncertainThreshold
            });
    }

    public ITranscriptionEngine Engine => _current;
    public ISessionStore SessionStore { get; }
    public IKnownSpeakerStore SpeakerStore { get; }
    public ISpeakerRecognitionService Recognition { get; }

    public SpeakerModelConfig SpeakerModels => new() { ModelDir = _configService.Load().SpeakerModelPath };

    public TranscriptionSessionOptions? CurrentOptions { get; private set; }

    public string TranscriptFolder => Path.GetFullPath(_configService.Load().TranscriptFolder);

    public void SetTranscriptFolder(string folder)
    {
        var config = _configService.Load();
        config.TranscriptFolder = folder;
        _configService.Save(config);
    }

    /// <summary>Exports a session in minutes format; same composition as the CLI export-minutes command.</summary>
    public Task<string> ExportMinutesAsync(string? sessionId = null, string? outputFolder = null, CancellationToken cancellationToken = default)
        => new MinutesExportService(_configService.Load(), SessionStore, new SqliteTranscriptEventStore(_db))
            .ExportAsync(sessionId, outputFolder, ct: cancellationToken);

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
        _current = _fakeEngine;
        await Engine.StartAsync(options, cancellationToken);
        CurrentOptions = options;
        return options;
    }

    /// <summary>Starts a real (audio + whisper + diarization) session in this MCP process.</summary>
    public async Task<TranscriptionSessionOptions> StartRealSessionAsync(CancellationToken cancellationToken = default)
    {
        var config = _configService.Load();
        _realEngine ??= EngineFactory.CreateReal(config);
        var options = EngineFactory.CreateSessionOptions(config, TranscriptFolder);
        _current = _realEngine;
        await _current.StartAsync(options, cancellationToken);
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
