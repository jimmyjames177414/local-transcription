using LocalTranscriber.Engine;
using LocalTranscriber.Engine.Ipc;
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

    // Set while THIS process owns a live session it started in-process. When false, control
    // verbs (status/stop/pause/resume) route to whichever process (app or CLI) owns the pipe.
    private bool _ownsSession;
    private EngineIpcServer? _ipcServer;

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
        _ownsSession = true;
        await TryHostIpcAsync(_current, cancellationToken).ConfigureAwait(false);
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
        _ownsSession = true;
        await TryHostIpcAsync(_current, cancellationToken).ConfigureAwait(false);
        return options;
    }

    /// <summary>
    /// Returns the running session status. When this process owns a session, reports its own
    /// engine; otherwise queries whichever process (app or CLI) owns the control pipe, falling
    /// back to the idle in-process engine when nothing is listening.
    /// </summary>
    public async Task<TranscriptionSessionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_ownsSession)
        {
            return await _current.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        var remote = await EngineIpcClient.TrySendAsync("status", cancellationToken: cancellationToken).ConfigureAwait(false);
        return remote?.Status ?? await _current.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the active session (in-process if this process owns it, else via the pipe).</summary>
    public Task<string> StopAsync(CancellationToken cancellationToken = default)
        => ControlAsync("stop", "Stopped.",
            async ct =>
            {
                await _current.StopAsync(ct).ConfigureAwait(false);
                _ownsSession = false;
                await DisposeIpcServerAsync().ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>Pauses the active session (in-process if this process owns it, else via the pipe).</summary>
    public Task<string> PauseAsync(CancellationToken cancellationToken = default)
        => ControlAsync("pause", "Paused.", ct => _current.PauseAsync(ct), cancellationToken);

    /// <summary>Resumes the active session (in-process if this process owns it, else via the pipe).</summary>
    public Task<string> ResumeAsync(CancellationToken cancellationToken = default)
        => ControlAsync("resume", "Resumed.", ct => _current.ResumeAsync(ct), cancellationToken);

    private async Task<string> ControlAsync(
        string ipcCommand, string okMessage, Func<CancellationToken, Task> inProcess, CancellationToken cancellationToken)
    {
        if (_ownsSession)
        {
            await inProcess(cancellationToken).ConfigureAwait(false);
            return okMessage;
        }

        var remote = await EngineIpcClient.TrySendAsync(ipcCommand, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (remote is not null)
        {
            return remote.Message ?? (remote.Ok ? okMessage : "Command failed.");
        }

        // Nothing owns the pipe — operate on the idle in-process engine so behavior is defined.
        await inProcess(cancellationToken).ConfigureAwait(false);
        return okMessage;
    }

    /// <summary>
    /// Hosts the control pipe for a session started here, so the CLI/app can control it — but only
    /// when no other process already owns the pipe (a single owner is required). Best-effort.
    /// </summary>
    private async Task TryHostIpcAsync(ITranscriptionEngine engine, CancellationToken cancellationToken)
    {
        if (_ipcServer is not null)
        {
            return;
        }

        var existing = await EngineIpcClient.TrySendAsync("status", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return; // another process (app/CLI) already owns the control pipe.
        }

        try
        {
            _ipcServer = new EngineIpcServer(engine);
            _ipcServer.Start();
        }
        catch
        {
            _ipcServer = null; // hosting is optional; never fail a start over it.
        }
    }

    private async Task DisposeIpcServerAsync()
    {
        if (_ipcServer is not null)
        {
            await _ipcServer.DisposeAsync().ConfigureAwait(false);
            _ipcServer = null;
        }
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
