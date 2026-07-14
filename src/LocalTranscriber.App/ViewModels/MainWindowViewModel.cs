using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using LocalTranscriber.App.Mvvm;
using LocalTranscriber.App.Services;
using LocalTranscriber.Engine;
using LocalTranscriber.Engine.Ipc;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.App.ViewModels;

/// <summary>Top-level screens hosted by the window shell.</summary>
public enum AppScreen
{
    Meeting = 0,
    Sessions = 1,
    Speakers = 2,
    Settings = 3
}

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly ITranscriptionEngine _engine;
    private readonly AppConfig _config;
    private readonly EngineIpcServer? _ipcServer;
    private readonly SynchronizationContext? _uiContext;
    private readonly Stopwatch _recordingWatch = new();
    private DispatcherTimer? _elapsedTimer;
    private CancellationTokenSource? _streamCts;

    private string _statusText = "Not recording";
    private string _sessionId = "";
    private string _sessionTitle = "";
    private string _outputFolder;
    private string _errorText = "";
    private string _elapsed = "00:00:00";
    private int _selectedScreenIndex = (int)AppScreen.Meeting;
    private TranscriptionSessionState _state = TranscriptionSessionState.NotStarted;

    public MainWindowViewModel(ITranscriptionEngine? engine = null, ConfigService? configService = null)
    {
        _config = (configService ?? new ConfigService()).Load();
        if (engine is null)
        {
            engine = EngineFactory.CreateReal(_config);
            _ipcServer = new EngineIpcServer(engine);
            _ipcServer.Start();
        }

        _engine = engine;
        _uiContext = SynchronizationContext.Current;
        _outputFolder = _config.TranscriptFolder;

        StartCommand = new AsyncRelayCommand(StartAsync, () => _state is TranscriptionSessionState.NotStarted or TranscriptionSessionState.Stopped or TranscriptionSessionState.Faulted);
        StopCommand = new AsyncRelayCommand(StopAsync, () => _state is TranscriptionSessionState.Recording or TranscriptionSessionState.Paused);
        PauseCommand = new AsyncRelayCommand(PauseAsync, () => _state == TranscriptionSessionState.Recording);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => _state == TranscriptionSessionState.Paused);
        CloseReviewCommand = new RelayCommand(CloseReview);
        NextMatchCommand = new RelayCommand(() => StepMatch(+1));
        PrevMatchCommand = new RelayCommand(() => StepMatch(-1));
        CitationCommand = new RelayCommand<string>(NavigateToTimestamp);
        RenameSpeakerCommand = new AsyncRelayCommand<TranscriptRowViewModel>(ExecuteRenameSpeakerAsync);

        Transcript.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowIdlePanel));
        RunPreflightChecks();
    }

    // === Speaker rename (click speaker name in transcript) ===

    private readonly List<string> _sessionSpeakerNames = new();

    /// <summary>Shell sets this to show a rename input dialog; returns the new name or null on cancel.</summary>
    public Func<string, IReadOnlyList<string>, string?>? ShowSpeakerRenameDialog { get; set; }

    public AsyncRelayCommand<TranscriptRowViewModel> RenameSpeakerCommand { get; }

    private async Task ExecuteRenameSpeakerAsync(TranscriptRowViewModel row)
    {
        if (!row.CanRename) return;

        string? newName = ShowSpeakerRenameDialog?.Invoke(row.SpeakerName, _sessionSpeakerNames);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == row.SpeakerName) return;

        string trimmed = newName.Trim();

        if (row.IsUnknownSpeaker)
        {
            bool ok = await _engine.NameSessionSpeakerAsync(row.SpeakerName, trimmed);
            if (!ok) return;
        }
        else
        {
            await _engine.RenameKnownSpeakerAsync(row.SpeakerName, trimmed);
        }

        // Determine which rows to rename: unknowns by session ID, knowns by display name.
        string matchId = row.SpeakerId;
        string oldName = row.SpeakerName;
        bool wasUnknown = row.IsUnknownSpeaker;
        var newBrush = Services.SpeakerPalette.GetBrushForName(trimmed);

        foreach (var r in Transcript)
        {
            bool isTarget = wasUnknown ? r.SpeakerId == matchId : r.SpeakerName == oldName;
            if (isTarget)
            {
                r.SpeakerName = trimmed;
                r.IsUnknownSpeaker = false;
                r.SpeakerBrush = newBrush;
            }
            else if (r.SpeakerName == trimmed)
            {
                // Another speaker already bearing this name — unify their brush too.
                r.SpeakerBrush = newBrush;
            }
        }

        if (!_sessionSpeakerNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            _sessionSpeakerNames.Add(trimmed);
    }

    // === Session title ===

    public string SessionTitle
    {
        get => _sessionTitle;
        set
        {
            if (!SetProperty(ref _sessionTitle, value)) return;
            if (!string.IsNullOrEmpty(_sessionId))
            {
                string? stored = value.Trim().Length == 0 ? null : value.Trim();
                _ = _engine.UpdateSessionTitleAsync(_sessionId, stored);
            }
        }
    }

    // === Review mode ("Viewing archive", design 4l) ===

    private bool _isReviewing;
    private string _reviewMetaText = "";
    private string _reviewFooterText = "";
    private string? _reviewJsonlPath;
    private string _reviewSearchText = "";
    private string _matchCountText = "";
    private List<int> _matchIndices = new();
    private int _currentMatch = -1;

    public RelayCommand CloseReviewCommand { get; }
    public RelayCommand NextMatchCommand { get; }
    public RelayCommand PrevMatchCommand { get; }
    public RelayCommand<string> CitationCommand { get; }

    /// <summary>Raised when the transcript list should scroll to (and highlight) a row index.</summary>
    public event Action<int>? ScrollToRowRequested;

    public bool IsReviewing
    {
        get => _isReviewing;
        private set
        {
            if (SetProperty(ref _isReviewing, value))
            {
                OnPropertyChanged(nameof(ShowIdlePanel));
                OnPropertyChanged(nameof(ShowIdleStatusPill));
                OnPropertyChanged(nameof(GroundingJsonlPath));
            }
        }
    }

    /// <summary>Id of the archived session being reviewed (null when live).</summary>
    public string? ReviewSessionId { get; private set; }

    public string ReviewMetaText
    {
        get => _reviewMetaText;
        private set => SetProperty(ref _reviewMetaText, value);
    }

    public string ReviewFooterText
    {
        get => _reviewFooterText;
        private set => SetProperty(ref _reviewFooterText, value);
    }

    /// <summary>Idle status pill hides while recording OR reviewing (the archive pill shows instead).</summary>
    public bool ShowIdleStatusPill => !IsRecording && !IsReviewing;

    /// <summary>What the assistant grounds on: the live session's jsonl, or the loaded archive's.</summary>
    public string? GroundingJsonlPath => IsReviewing ? _reviewJsonlPath : CurrentJsonlPath;

    /// <summary>Fills the Meeting screen with an archived session (read-only review).</summary>
    public void LoadArchive(SessionRecord record, IReadOnlyList<TranscriptEvent> events)
    {
        if (IsRecording)
        {
            return;
        }

        Transcript.Clear();
        SpeakerPalette.Reset();
        foreach (var e in events)
        {
            Transcript.Add(new TranscriptRowViewModel(e, _config.SpeakerMatchThreshold));
        }

        var started = record.StartedAt.ToLocalTime();
        string when = started.Date == DateTime.Today ? "Today" : started.ToString("yyyy-MM-dd");
        string title = string.IsNullOrWhiteSpace(record.Title) ? $"Meeting {started:HH:mm}" : record.Title!;
        ReviewSessionId = record.Id;
        ReviewMetaText = $"{title} — {when} {started:HH:mm}, {SessionListItemViewModel.FormatDuration(record)}. You're reviewing a saved session; nothing is recording.";
        ReviewFooterText = $"read-only · {Path.GetFileName(record.OutputTextPath)}";
        _reviewJsonlPath = File.Exists(record.OutputJsonlPath) ? record.OutputJsonlPath : null;
        ReviewSearchText = "";
        IsReviewing = true;
        SelectedScreenIndex = (int)AppScreen.Meeting;
    }

    private void CloseReview()
    {
        if (!IsReviewing)
        {
            return;
        }

        Transcript.Clear();
        ReviewSessionId = null;
        _reviewJsonlPath = null;
        ReviewMetaText = "";
        ReviewFooterText = "";
        ReviewSearchText = "";
        IsReviewing = false;
    }

    // === In-transcript search (review mode) ===

    public string ReviewSearchText
    {
        get => _reviewSearchText;
        set
        {
            if (SetProperty(ref _reviewSearchText, value))
            {
                RecomputeMatches();
            }
        }
    }

    public string MatchCountText
    {
        get => _matchCountText;
        private set => SetProperty(ref _matchCountText, value);
    }

    private void RecomputeMatches()
    {
        string query = _reviewSearchText.Trim();
        _matchIndices = query.Length == 0
            ? new List<int>()
            : Transcript.Select((row, i) => (row, i))
                .Where(t => t.row.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || t.row.SpeakerName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.i)
                .ToList();
        _currentMatch = _matchIndices.Count > 0 ? 0 : -1;
        UpdateMatchCount();
        if (_currentMatch >= 0)
        {
            ScrollToRowRequested?.Invoke(_matchIndices[_currentMatch]);
        }
    }

    private void StepMatch(int direction)
    {
        if (_matchIndices.Count == 0)
        {
            return;
        }

        _currentMatch = (_currentMatch + direction + _matchIndices.Count) % _matchIndices.Count;
        UpdateMatchCount();
        ScrollToRowRequested?.Invoke(_matchIndices[_currentMatch]);
    }

    private void UpdateMatchCount()
        => MatchCountText = _matchIndices.Count == 0
            ? (_reviewSearchText.Trim().Length == 0 ? "" : "0/0")
            : $"{_currentMatch + 1}/{_matchIndices.Count}";

    /// <summary>Citation click: jump to the first row at/after the given HH:mm:ss timestamp.</summary>
    public void NavigateToTimestamp(string hhmmss)
    {
        // Fixed-width HH:mm:ss makes ordinal string comparison chronological.
        for (int i = 0; i < Transcript.Count; i++)
        {
            if (string.CompareOrdinal(Transcript[i].Time, hhmmss) >= 0)
            {
                ScrollToRowRequested?.Invoke(i);
                return;
            }
        }

        if (Transcript.Count > 0)
        {
            ScrollToRowRequested?.Invoke(Transcript.Count - 1);
        }
    }

    // === First-run / idle state (design 4g) ===

    private string _whisperCheckText = "";
    private string _speakerCheckText = "";
    private string _deviceCheckText = "checking audio devices…";
    private bool _whisperCheckOk;
    private bool _speakerCheckOk;
    private bool _deviceCheckOk;

    /// <summary>Idle layout (big Start + preflight list) shown when nothing is recorded yet.</summary>
    public bool ShowIdlePanel => !IsRecording && !IsReviewing && Transcript.Count == 0;

    public string WhisperCheckText { get => _whisperCheckText; private set => SetProperty(ref _whisperCheckText, value); }
    public string SpeakerCheckText { get => _speakerCheckText; private set => SetProperty(ref _speakerCheckText, value); }
    public string DeviceCheckText { get => _deviceCheckText; private set => SetProperty(ref _deviceCheckText, value); }
    public bool WhisperCheckOk { get => _whisperCheckOk; private set => SetProperty(ref _whisperCheckOk, value); }
    public bool SpeakerCheckOk { get => _speakerCheckOk; private set => SetProperty(ref _speakerCheckOk, value); }
    public bool DeviceCheckOk { get => _deviceCheckOk; private set => SetProperty(ref _deviceCheckOk, value); }

    private void RunPreflightChecks()
    {
        string whisperName = Path.GetFileName(_config.WhisperModelPath ?? "");
        WhisperCheckOk = File.Exists(_config.WhisperModelPath);
        WhisperCheckText = WhisperCheckOk ? $"whisper {whisperName} found" : $"whisper model missing — {_config.WhisperModelPath}";
        SpeakerCheckOk = File.Exists(_config.SpeakerModelPath) || Directory.Exists(_config.SpeakerModelPath);
        SpeakerCheckText = SpeakerCheckOk ? "speaker model found" : "speaker model missing — labels will be generic";

        if (!WhisperCheckOk)
        {
            AddBanner(new BannerViewModel(BannerSeverity.Error,
                $"Whisper model not found — {_config.WhisperModelPath}. Can't transcribe.",
                "Open settings", () => NavigateToSettings?.Invoke(2), RemoveBanner)
            { Key = "whisper-missing" });
        }

        // Device enumeration can touch hardware; keep it off the UI thread.
        _ = Task.Run(() =>
        {
            string text;
            bool ok;
            try
            {
                var devices = new LocalTranscriber.Audio.AudioDeviceService();
                bool mic = devices.ListInputDevices().Any();
                bool output = devices.ListOutputDevices().Any();
                ok = (mic || !_config.EnableMicCapture) && (output || !_config.EnableSystemCapture);
                text = (mic, output) switch
                {
                    (true, true) => "mic + loopback ready",
                    (false, true) => "no microphone found — system audio only",
                    (true, false) => "no output device — mic only",
                    _ => "no audio devices found"
                };
            }
            catch (Exception ex)
            {
                ok = false;
                text = $"device check failed: {ex.Message}";
            }

            PostToUi(() =>
            {
                DeviceCheckOk = ok;
                DeviceCheckText = text;
            });
        });
    }

    // === Mic-lost watch (engine aggregates warnings while recording) ===

    private DispatcherTimer? _healthTimer;
    private string _lastHealthWarning = "";

    private void StartHealthWatch()
    {
        _healthTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _healthTimer.Tick -= OnHealthTick;
        _healthTimer.Tick += OnHealthTick;
        _healthTimer.Start();
    }

    private void StopHealthWatch() => _healthTimer?.Stop();

    private async void OnHealthTick(object? sender, EventArgs e)
    {
        try
        {
            var status = await _engine.GetStatusAsync();
            string warning = status.Error ?? "";
            if (warning.Length > 0 && warning != _lastHealthWarning)
            {
                _lastHealthWarning = warning;
                AddBanner(new BannerViewModel(BannerSeverity.Warning, warning,
                    "Pick device", () => NavigateToSettings?.Invoke(1), RemoveBanner)
                { Key = "capture-warning" });
            }
        }
        catch
        {
            // Status polling must never disturb the session.
        }
    }

    private void AddBanner(BannerViewModel banner)
    {
        if (banner.Key is { } key && Banners.Any(b => b.Key == key))
        {
            return;
        }
        Banners.Add(banner);
    }

    private void RemoveBanner(BannerViewModel banner) => Banners.Remove(banner);

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand PauseCommand { get; }
    public AsyncRelayCommand ResumeCommand { get; }

    /// <summary>Transcript turns of the current session, appended live on the UI thread.</summary>
    public ObservableCollection<TranscriptRowViewModel> Transcript { get; } = new();

    /// <summary>Inline banners under the transcript header (model missing, mic lost...).</summary>
    public ObservableCollection<BannerViewModel> Banners { get; } = new();

    /// <summary>Set by the shell: navigates to a Settings section (index into SettingsViewModel.Sections).</summary>
    public Action<int>? NavigateToSettings { get; set; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SessionId
    {
        get => _sessionId;
        private set => SetProperty(ref _sessionId, value);
    }

    /// <summary>Jsonl path of the active session; the Agent panel tails this.</summary>
    public string? CurrentJsonlPath { get; private set; }

    public string OutputFolder
    {
        get => _outputFolder;
        set => SetProperty(ref _outputFolder, value);
    }

    /// <summary>Capture/model summary for the transcript footer, e.g. "mic+sys · base.en · txt+jsonl".</summary>
    public string SourcesSummary
    {
        get
        {
            string sources = (_config.EnableMicCapture, _config.EnableSystemCapture) switch
            {
                (true, true) => "mic+sys",
                (true, false) => "mic",
                (false, true) => "sys",
                _ => "no capture"
            };
            string model = Path.GetFileNameWithoutExtension(_config.WhisperModelPath ?? "");
            if (model.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase))
            {
                model = model["ggml-".Length..];
            }

            return $"{sources} · {model} · txt+jsonl";
        }
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    /// <summary>Which top-level screen the shell shows (index of <see cref="AppScreen"/>).</summary>
    public int SelectedScreenIndex
    {
        get => _selectedScreenIndex;
        set => SetProperty(ref _selectedScreenIndex, value);
    }

    /// <summary>Recorded time shown in the header pill ("hh:mm:ss"); excludes paused time.</summary>
    public string Elapsed
    {
        get => _elapsed;
        private set => SetProperty(ref _elapsed, value);
    }

    /// <summary>True while a session is live (recording or paused) — drives the header pill.</summary>
    public bool IsRecording => _state is TranscriptionSessionState.Recording or TranscriptionSessionState.Paused;

    private void SetState(TranscriptionSessionState state)
    {
        _state = state;
        StatusText = state switch
        {
            TranscriptionSessionState.NotStarted => "Not recording",
            TranscriptionSessionState.Starting => "Starting...",
            TranscriptionSessionState.Recording => "Recording",
            TranscriptionSessionState.Paused => "Paused",
            TranscriptionSessionState.Stopping => "Stopping...",
            TranscriptionSessionState.Stopped => "Stopped",
            TranscriptionSessionState.Faulted => "Faulted",
            _ => state.ToString()
        };
        UpdateRecordingClock(state);
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(ShowIdlePanel));
        OnPropertyChanged(nameof(ShowIdleStatusPill));
        if (state == TranscriptionSessionState.Recording)
        {
            StartHealthWatch();
        }
        else if (state is TranscriptionSessionState.Stopped or TranscriptionSessionState.Faulted or TranscriptionSessionState.NotStarted)
        {
            StopHealthWatch();
            _lastHealthWarning = "";
        }
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
    }

    private void UpdateRecordingClock(TranscriptionSessionState state)
    {
        switch (state)
        {
            case TranscriptionSessionState.Recording:
                _recordingWatch.Start();
                _elapsedTimer ??= new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _elapsedTimer.Tick -= OnElapsedTick;
                _elapsedTimer.Tick += OnElapsedTick;
                _elapsedTimer.Start();
                break;
            case TranscriptionSessionState.Paused:
                _recordingWatch.Stop();
                break;
            case TranscriptionSessionState.Stopped:
            case TranscriptionSessionState.Faulted:
            case TranscriptionSessionState.NotStarted:
                _recordingWatch.Reset();
                _elapsedTimer?.Stop();
                Elapsed = "00:00:00";
                break;
        }
    }

    private void OnElapsedTick(object? sender, EventArgs e)
        => Elapsed = _recordingWatch.Elapsed.ToString(@"hh\:mm\:ss");

    private async Task StartAsync()
    {
        try
        {
            CloseReview(); // "Start new recording" from the review banner exits review first
            ErrorText = "";
            Transcript.Clear();
            SpeakerPalette.Reset();
            string folder = string.IsNullOrWhiteSpace(OutputFolder) ? "output/transcripts" : OutputFolder;
            var options = EngineFactory.CreateSessionOptions(_config, folder);

            await _engine.StartAsync(options);
            SessionId = options.SessionId;
            SessionTitle = "";
            _sessionSpeakerNames.Clear();
            CurrentJsonlPath = options.OutputJsonlPath;
            SetState(TranscriptionSessionState.Recording);

            // Surface any non-fatal start warnings (e.g. a skipped audio source).
            var status = await _engine.GetStatusAsync();
            if (!string.IsNullOrWhiteSpace(status.Error))
            {
                ErrorText = status.Error;
                AppLog.Warn("app", status.Error);
            }

            _streamCts = new CancellationTokenSource();
            _ = ConsumeEventsAsync(_streamCts.Token);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            AppLog.Error("app", $"Start failed: {ex.Message}");
            SetState(TranscriptionSessionState.Faulted);
        }
    }

    private async Task ConsumeEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var e in _engine.StreamEventsAsync(cancellationToken))
            {
                var captured = e;
                PostToUi(() => Transcript.Add(new TranscriptRowViewModel(captured, _config.SpeakerMatchThreshold)));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PostToUi(() => ErrorText = ex.Message);
        }
    }

    private void PostToUi(Action action)
    {
        if (_uiContext is not null)
        {
            _uiContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    private async Task StopAsync()
    {
        try
        {
            SetState(TranscriptionSessionState.Stopping);
            _streamCts?.Cancel();
            _streamCts = null;
            await _engine.StopAsync();
            SetState(TranscriptionSessionState.Stopped);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            SetState(TranscriptionSessionState.Faulted);
        }
    }

    private async Task PauseAsync()
    {
        await _engine.PauseAsync();
        SetState(TranscriptionSessionState.Paused);
    }

    private async Task ResumeAsync()
    {
        await _engine.ResumeAsync();
        SetState(TranscriptionSessionState.Recording);
    }

    public async Task ShutdownAsync()
    {
        _streamCts?.Cancel();
        await _engine.StopAsync();
        if (_ipcServer is not null)
        {
            await _ipcServer.DisposeAsync();
        }
    }
}
