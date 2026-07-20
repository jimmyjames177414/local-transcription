using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Threading;
using LocalTranscriber.App.Mvvm;
using LocalTranscriber.App.Services;
using LocalTranscriber.Context;
using LocalTranscriber.Engine;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;
using LocalTranscriber.Voice;

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
    private readonly IKnownSpeakerStore _knownSpeakers;
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
    private bool _titleSavedFlash;
    private DispatcherTimer? _titleFlashTimer;
    private bool _transcriptCopiedFlash;
    private DispatcherTimer? _transcriptCopyFlashTimer;

    public MainWindowViewModel(ITranscriptionEngine engine, ConfigService? configService = null,
        IKnownSpeakerStore? knownSpeakers = null)
    {
        _config = (configService ?? new ConfigService()).Load();
        // The engine is always injected by the host, which also owns the single EngineIpcServer.
        _engine = engine;
        // Cross-session roster feeding the rename dialog's name suggestions. Fall back to a direct
        // store (mirrors SpeakerManagementViewModel) so the view-model stays constructable without DI.
        _knownSpeakers = knownSpeakers ?? new SqliteKnownSpeakerStore(new SqliteDatabase(_config.DatabasePath));
        _uiContext = SynchronizationContext.Current;
        _outputFolder = _config.TranscriptFolder;

        StartCommand = new AsyncRelayCommand(StartAsync, () => _state is TranscriptionSessionState.NotStarted or TranscriptionSessionState.Stopped or TranscriptionSessionState.Faulted);
        StopCommand = new AsyncRelayCommand(StopAsync, () => _state is TranscriptionSessionState.Recording or TranscriptionSessionState.Paused);
        PauseCommand = new AsyncRelayCommand(PauseAsync, () => _state == TranscriptionSessionState.Recording);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => _state == TranscriptionSessionState.Paused);
        ToggleTransportCommand = new AsyncRelayCommand(ToggleTransportAsync,
            () => _state is not (TranscriptionSessionState.Starting or TranscriptionSessionState.Stopping));
        CloseReviewCommand = new RelayCommand(CloseReview);
        CancelContinuationCommand = new RelayCommand(CancelContinuation);
        NextMatchCommand = new RelayCommand(() => StepMatch(+1));
        PrevMatchCommand = new RelayCommand(() => StepMatch(-1));
        CitationCommand = new RelayCommand<string>(NavigateToTimestamp);
        RenameSpeakerCommand = new AsyncRelayCommand<TranscriptRowViewModel>(ExecuteRenameSpeakerAsync);
        DeleteLineCommand = new AsyncRelayCommand<TranscriptRowViewModel>(ExecuteDeleteLineAsync);
        UndoCommand = new AsyncRelayCommand(UndoLastAsync, () => _undoStack.Count > 0);
        CopyTranscriptCommand = new RelayCommand(CopyTranscript, () => Transcript.Count > 0);
        GenerateNotesCommand = new AsyncRelayCommand(GenerateNotesAsync, () => Transcript.Count > 0);

        Transcript.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowIdlePanel));
            CopyTranscriptCommand.RaiseCanExecuteChanged();
            GenerateNotesCommand.RaiseCanExecuteChanged();
        };
        RunPreflightChecks();
    }

    // === Speaker rename (click speaker name in transcript) ===

    private readonly List<string> _sessionSpeakerNames = new();

    /// <summary>Shell sets this to show the rename dialog; returns the chosen name+scope or null on cancel.</summary>
    public Func<SpeakerRenameRequest, SpeakerRenameResult?>? ShowSpeakerRenameDialog { get; set; }

    /// <summary>Shell sets this to show the generated-notes preview window (markdown, suggested filename).</summary>
    public Action<string, string>? ShowGenerateNotesPreview { get; set; }

    public AsyncRelayCommand<TranscriptRowViewModel> RenameSpeakerCommand { get; }

    /// <summary>Deletes one transcript line (the ✕ button / context menu). Undoable via <see cref="UndoCommand"/>.</summary>
    public AsyncRelayCommand<TranscriptRowViewModel> DeleteLineCommand { get; }

    // === Undo (rename or delete — button + Ctrl+Z) ===

    /// <summary>In-memory, session-scoped stack of inverse actions (renames and line deletes). Each entry
    /// restores both the persisted store write and the affected rows' visible state. Cleared when the
    /// transcript is swapped.</summary>
    private readonly Stack<(string Label, Func<Task> UndoAsync)> _undoStack = new();

    /// <summary>Event ids that currently carry a per-event override, so a repeated "this one" rename
    /// on the same line undoes to its previous override value rather than clearing it.</summary>
    private readonly HashSet<string> _eventsWithOverride = new(StringComparer.Ordinal);

    /// <summary>Reverts the most recent undoable action — a rename or a line delete (see <see cref="_undoStack"/>).</summary>
    public AsyncRelayCommand UndoCommand { get; }

    public string UndoTooltip => _undoStack.Count > 0
        ? $"Undo {_undoStack.Peek().Label} (Ctrl+Z)"
        : "Nothing to undo";

    private void RaiseUndoChanged()
    {
        UndoCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(UndoTooltip));
    }

    private void PushUndo(string label, Func<Task> undoAsync)
    {
        _undoStack.Push((label, undoAsync));
        RaiseUndoChanged();
    }

    /// <summary>Drops all pending undos. Called whenever the displayed transcript is swapped or reset.</summary>
    private void ClearUndoHistory()
    {
        if (_undoStack.Count == 0 && _eventsWithOverride.Count == 0) return;
        _undoStack.Clear();
        _eventsWithOverride.Clear();
        RaiseUndoChanged();
    }

    private async Task UndoLastAsync()
    {
        if (_undoStack.Count == 0) return;
        var entry = _undoStack.Pop();
        await entry.UndoAsync();
        RaiseUndoChanged();
    }

    // === Copy transcript ===

    /// <summary>Copies the full transcript (all visible rows) to the clipboard as plain text.</summary>
    public RelayCommand CopyTranscriptCommand { get; }

    /// <summary>Pulses true for ~1.5 s after a copy to confirm the action visually.</summary>
    public bool TranscriptCopiedFlash
    {
        get => _transcriptCopiedFlash;
        private set => SetProperty(ref _transcriptCopiedFlash, value);
    }

    private void CopyTranscript()
    {
        if (Transcript.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        foreach (var row in Transcript)
        {
            sb.Append('[').Append(row.Time).Append("] ");
            sb.Append(row.SpeakerName).Append(": ");
            sb.AppendLine(row.Text);
        }
        System.Windows.Clipboard.SetText(sb.ToString());
        FlashTranscriptCopied();
    }

    private void FlashTranscriptCopied()
    {
        TranscriptCopiedFlash = true;
        _transcriptCopyFlashTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _transcriptCopyFlashTimer.Tick -= OnTranscriptCopyFlashTick;
        _transcriptCopyFlashTimer.Tick += OnTranscriptCopyFlashTick;
        _transcriptCopyFlashTimer.Stop();
        _transcriptCopyFlashTimer.Start();
    }

    private void OnTranscriptCopyFlashTick(object? sender, EventArgs e)
    {
        _transcriptCopyFlashTimer!.Stop();
        TranscriptCopiedFlash = false;
    }

    // === Generate Notes (full AI meeting-notes document) ===

    private bool _isGeneratingNotes;

    /// <summary>Builds a full meeting-notes document from the displayed transcript + project context,
    /// via the currently configured agent backend driven as a bounded one-shot. Usable live or in review.</summary>
    public AsyncRelayCommand GenerateNotesCommand { get; }

    /// <summary>True while a document is being generated — drives the button's "Generating…" state.</summary>
    public bool IsGeneratingNotes
    {
        get => _isGeneratingNotes;
        private set => SetProperty(ref _isGeneratingNotes, value);
    }

    private async Task GenerateNotesAsync()
    {
        if (Transcript.Count == 0) return;

        try
        {
            ErrorText = "";
            IsGeneratingNotes = true;

            var config = new ConfigService().Load();

            var pack = await new MarkdownContextPackService().LoadAsync(new ContextPackOptions
            {
                ContextFolder = config.Agent.ContextFolder,
                MaxTotalCharacters = config.Agent.MaxContextCharacters,
                RequiredFiles = config.Agent.RequiredContextFiles
            }).ConfigureAwait(true);

            string fullPrompt = BuildNotesPrompt(ReadEmbeddedNotesPrompt(), pack.CombinedText);

            // Offload the sync factory work (WSL probing etc.) off the UI thread, like StartVoiceAsync.
            string markdown = await Task.Run(() =>
                new MeetingNotesGenerator().GenerateAsync(fullPrompt, config, new SecretsService()))
                .ConfigureAwait(true);

            markdown += BuildNotesFooter(config);

            string sessionKey = ReviewSessionId
                ?? (string.IsNullOrEmpty(SessionId) ? DateTime.Now.ToString("yyyyMMdd") : SessionId);
            ShowGenerateNotesPreview?.Invoke(markdown, $"generated-notes-{sessionKey}.md");
        }
        catch (Exception ex)
        {
            ErrorText = $"Generate notes failed: {ex.Message}";
            AppLog.Warn("app", $"GenerateNotes failed: {ex.Message}");
        }
        finally
        {
            IsGeneratingNotes = false;
        }
    }

    /// <summary>Assembles the one-shot prompt: an override preamble neutralising the backend's baked-in
    /// "be brief" instruction, the embedded notetaker prompt, then the transcript + context source material.</summary>
    private string BuildNotesPrompt(string promptBody, string contextText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Follow the instructions below exactly to produce a complete meeting-notes document. "
            + "Disregard any earlier instruction to be brief or conversational; output only the finished Markdown notes.");
        sb.AppendLine();
        sb.AppendLine(promptBody);
        sb.AppendLine();
        sb.AppendLine("============================================================");
        sb.AppendLine("SOURCE MATERIAL");
        sb.AppendLine("============================================================");
        sb.AppendLine();
        sb.AppendLine("## Meeting transcript");
        foreach (var row in Transcript)
        {
            sb.Append('[').Append(row.Time).Append("] ").Append(row.SpeakerName).Append(": ").AppendLine(row.Text);
        }
        sb.AppendLine();
        sb.AppendLine("## Project context");
        sb.AppendLine(string.IsNullOrWhiteSpace(contextText) ? "(none)" : contextText);
        return sb.ToString();
    }

    /// <summary>Records the actual backend used, so a shared document is self-describing.</summary>
    private static string BuildNotesFooter(AppConfig config)
    {
        string provider = config.Agent.Provider;
        bool claudeBrain = AgentProviders.Is(provider, AgentProvider.ClaudeCli)
            || AgentProviders.Is(provider, AgentProvider.Hybrid);
        string model = claudeBrain
            ? (string.IsNullOrWhiteSpace(config.Agent.ClaudeCli.Model) ? "default" : config.Agent.ClaudeCli.Model)
            : config.Agent.Realtime.Model;
        string voiceMode = config.Agent.Realtime.VoiceMode;
        return $"\n\n---\n\n<sub>Generated by LocalTranscriber · {provider} · model {model} · "
            + $"voice mode {voiceMode} · {DateTime.Now:yyyy-MM-dd HH:mm}</sub>";
    }

    private static string ReadEmbeddedNotesPrompt()
    {
        var asm = Assembly.GetExecutingAssembly();
        string name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("generate-notes-prompt.md", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded prompt 'generate-notes-prompt.md' not found.");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private async Task ExecuteRenameSpeakerAsync(TranscriptRowViewModel row)
    {
        if (!row.CanRename) return;

        // Count occurrences to label the "All" option meaningfully.
        bool wasUnknown = row.IsUnknownSpeaker;
        string matchId = row.SpeakerId;
        string oldName = row.SpeakerName;
        int occurrenceCount = wasUnknown
            ? Transcript.Count(r => r.SpeakerId == matchId)
            : Transcript.Count(r => r.SpeakerName == oldName);

        var suggestions = await BuildRenameSuggestionsAsync(oldName);
        var request = new SpeakerRenameRequest(oldName, suggestions, occurrenceCount, wasUnknown);
        SpeakerRenameResult? result = ShowSpeakerRenameDialog?.Invoke(request);
        if (result is null || string.IsNullOrWhiteSpace(result.NewName) || result.NewName.Trim() == oldName) return;

        string trimmed = result.NewName.Trim();
        string sid = _sessionId;
        var newBrush = Services.SpeakerPalette.GetBrushForName(trimmed);

        // Prior visual state of a row, captured before we mutate it so undo can restore it exactly.
        var snapshots = new List<(TranscriptRowViewModel Row, string Name, bool Unknown, System.Windows.Media.Brush Brush)>();
        void ApplyTo(TranscriptRowViewModel r, bool clearUnknown)
        {
            snapshots.Add((r, r.SpeakerName, r.IsUnknownSpeaker, r.SpeakerBrush));
            r.SpeakerName = trimmed;
            if (clearUnknown) r.IsUnknownSpeaker = false;
            r.SpeakerBrush = newBrush;
        }
        void RestoreSnapshots()
        {
            foreach (var s in snapshots)
            {
                s.Row.SpeakerName = s.Name;
                s.Row.IsUnknownSpeaker = s.Unknown;
                s.Row.SpeakerBrush = s.Brush;
            }
        }

        if (result.Scope == RenameScope.ThisOne)
        {
            bool ok = await _engine.OverrideEventSpeakerAsync(sid, row.EventId, trimmed);
            if (!ok) return;

            bool hadPriorOverride = _eventsWithOverride.Contains(row.EventId);
            string priorName = oldName;
            string eventId = row.EventId;
            ApplyTo(row, clearUnknown: true);
            _eventsWithOverride.Add(eventId);

            PushUndo($"rename → {oldName}", async () =>
            {
                if (hadPriorOverride)
                {
                    await _engine.OverrideEventSpeakerAsync(sid, eventId, priorName);
                }
                else
                {
                    await _engine.ClearEventSpeakerOverrideAsync(sid, eventId);
                    _eventsWithOverride.Remove(eventId);
                }
                RestoreSnapshots();
            });
        }
        else
        {
            // All — enroll/rename globally and update every matching row.
            if (wasUnknown)
            {
                bool ok = await _engine.NameSessionSpeakerAsync(oldName, trimmed);
                if (!ok) return;
            }
            else
            {
                // A known-speaker rename is refused when the target name already belongs to a
                // different saved speaker (see SqliteKnownSpeakerStore.RenameAsync). Leave the
                // transcript untouched rather than showing a name the store rejected.
                if (!await _engine.RenameKnownSpeakerAsync(oldName, trimmed))
                {
                    ErrorText = $"Couldn't rename to \"{trimmed}\": that name is already used by another saved speaker.";
                    return;
                }
            }

            foreach (var r in Transcript)
            {
                bool isTarget = wasUnknown ? r.SpeakerId == matchId : r.SpeakerName == oldName;
                if (isTarget)
                {
                    ApplyTo(r, clearUnknown: true);
                }
                else if (r.SpeakerName == trimmed)
                {
                    // Another row already bearing this name — unify their brush.
                    snapshots.Add((r, r.SpeakerName, r.IsUnknownSpeaker, r.SpeakerBrush));
                    r.SpeakerBrush = newBrush;
                }
            }

            if (wasUnknown)
            {
                // Enroll case: undo removes the session alias (voice sample intentionally left) and restores rows.
                PushUndo($"rename → {oldName}", async () =>
                {
                    await _engine.ClearSessionSpeakerAliasAsync(sid, matchId);
                    RestoreSnapshots();
                });
            }
            else
            {
                // Global known-speaker rename: undo renames back and restores rows.
                PushUndo($"rename → {oldName}", async () =>
                {
                    await _engine.RenameKnownSpeakerAsync(trimmed, oldName);
                    RestoreSnapshots();
                });
            }
        }

        if (!_sessionSpeakerNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            _sessionSpeakerNames.Add(trimmed);
    }

    /// <summary>Names offered in the rename dialog, most-likely first and deduped case-insensitively:
    /// people already named in this session, then "Me", then the persisted cross-session roster
    /// ordered by most-recently-seen. Excludes the name being changed and any placeholder labels of
    /// still-unidentified rows. Surfacing the full roster lets a rename re-link to an existing voice
    /// identity instead of spawning a duplicate from a retyped name.</summary>
    private async Task<IReadOnlyList<string>> BuildRenameSuggestionsAsync(string currentName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            string trimmed = name.Trim();
            if (string.Equals(trimmed, currentName, StringComparison.OrdinalIgnoreCase)) return;
            if (seen.Add(trimmed)) ordered.Add(trimmed);
        }

        // 1. Other people already named in this meeting (the likeliest candidates), skipping the
        //    placeholder labels of rows that are still unidentified ("Speaker 2").
        var placeholders = new HashSet<string>(
            Transcript.Where(r => r.IsUnknownSpeaker).Select(r => r.SpeakerName),
            StringComparer.OrdinalIgnoreCase);
        foreach (string name in _sessionSpeakerNames)
        {
            if (!placeholders.Contains(name)) Add(name);
        }

        // 2. The microphone owner.
        Add(_config.DefaultMicSpeakerName);

        // 3. The persisted roster, most-recently-seen first. A read failure must never block a
        //    rename, so fall back to the session names gathered above.
        try
        {
            var roster = await _knownSpeakers.ListAsync();
            foreach (var s in roster.OrderByDescending(s => s.LastSeenAt ?? DateTimeOffset.MinValue))
            {
                Add(s.DisplayName);
            }
        }
        catch
        {
            // Intentionally swallowed: suggestions are a convenience, not a correctness gate.
        }

        return ordered;
    }

    // === Delete transcript line (✕ button / context menu) ===

    private async Task ExecuteDeleteLineAsync(TranscriptRowViewModel row)
    {
        int index = Transcript.IndexOf(row);
        if (index < 0) return;

        var e = row.Event;
        string sid = _sessionId;
        if (!await _engine.DeleteEventAsync(sid, row.EventId)) return;

        Transcript.RemoveAt(index);
        if (_reviewSearchText.Trim().Length > 0) RecomputeMatches();

        string snippet = row.Text.Length > 24 ? row.Text[..24] + "…" : row.Text;
        PushUndo($"delete → \"{snippet}\"", async () =>
        {
            await _engine.RestoreEventAsync(e);
            int at = Math.Min(index, Transcript.Count);
            Transcript.Insert(at, row);
            if (_reviewSearchText.Trim().Length > 0) RecomputeMatches();
        });
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
                FlashTitleSaved();
            }
        }
    }

    /// <summary>Pulses true for ~1.5 s after each title save to confirm the change was persisted.</summary>
    public bool TitleSavedFlash
    {
        get => _titleSavedFlash;
        private set => SetProperty(ref _titleSavedFlash, value);
    }

    private void FlashTitleSaved()
    {
        TitleSavedFlash = true;
        _titleFlashTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _titleFlashTimer.Tick -= OnTitleFlashTick;
        _titleFlashTimer.Tick += OnTitleFlashTick;
        _titleFlashTimer.Stop();
        _titleFlashTimer.Start();
    }

    private void OnTitleFlashTick(object? sender, EventArgs e)
    {
        _titleFlashTimer!.Stop();
        TitleSavedFlash = false;
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

    // === Continuation mode ===
    private SessionRecord? _continueRecord;
    private string _continueBannerText = "";

    public RelayCommand CloseReviewCommand { get; }
    public RelayCommand CancelContinuationCommand { get; }
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

    /// <summary>True while a session is loaded and ready to be continued (not yet recording).</summary>
    public bool IsContinuing => _continueRecord is not null;

    public string ContinueBannerText
    {
        get => _continueBannerText;
        private set => SetProperty(ref _continueBannerText, value);
    }

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
        ClearUndoHistory();
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

    /// <summary>Loads a stopped session into the Meeting screen ready for continuation.</summary>
    public void LoadForContinuation(SessionRecord record, IReadOnlyList<TranscriptEvent> events)
    {
        if (IsRecording) return;

        // Close any active review first
        if (IsReviewing) CloseReview();

        Transcript.Clear();
        ClearUndoHistory();
        SpeakerPalette.Reset();
        _sessionSpeakerNames.Clear();

        foreach (var e in events)
        {
            var row = new TranscriptRowViewModel(e, _config.SpeakerMatchThreshold);
            Transcript.Add(row);
            if (!string.IsNullOrEmpty(row.SpeakerName) && !_sessionSpeakerNames.Contains(row.SpeakerName, StringComparer.OrdinalIgnoreCase))
                _sessionSpeakerNames.Add(row.SpeakerName);
        }

        SessionId = record.Id;
        SessionTitle = record.Title ?? "";
        CurrentJsonlPath = File.Exists(record.OutputJsonlPath) ? record.OutputJsonlPath : null;
        _continueRecord = record;
        ContinueBannerText = $"Continuing: {(string.IsNullOrWhiteSpace(record.Title) ? $"session {record.Id[..Math.Min(8, record.Id.Length)]}" : record.Title)} — press Start to append";
        OnPropertyChanged(nameof(IsContinuing));
        SelectedScreenIndex = (int)AppScreen.Meeting;
    }

    private void CancelContinuation()
    {
        if (_continueRecord is null) return;
        _continueRecord = null;
        ContinueBannerText = "";
        Transcript.Clear();
        ClearUndoHistory();
        SessionId = "";
        SessionTitle = "";
        CurrentJsonlPath = null;
        OnPropertyChanged(nameof(IsContinuing));
        OnPropertyChanged(nameof(ShowIdlePanel));
        OnPropertyChanged(nameof(GroundingJsonlPath));
    }

    private void CloseReview()
    {
        if (!IsReviewing)
        {
            return;
        }

        Transcript.Clear();
        ClearUndoHistory();
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
    /// <summary>Dispatches to Start/Pause/Resume based on current session state; used by the single live transport button.</summary>
    public AsyncRelayCommand ToggleTransportCommand { get; }

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

    /// <summary>True when idle/stopped/faulted — shows the Start button, hides the live transport button.</summary>
    public bool IsIdleTransport => _state is TranscriptionSessionState.NotStarted or TranscriptionSessionState.Stopped or TranscriptionSessionState.Faulted;

    /// <summary>True while paused — swaps the live transport button label to Resume.</summary>
    public bool IsPausedTransport => _state == TranscriptionSessionState.Paused;

    private Task ToggleTransportAsync() => _state switch
    {
        TranscriptionSessionState.Recording => PauseAsync(),
        TranscriptionSessionState.Paused    => ResumeAsync(),
        _                                    => StartAsync(),
    };

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
        ToggleTransportCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsIdleTransport));
        OnPropertyChanged(nameof(IsPausedTransport));
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
            bool isContinuation = _continueRecord is not null;
            SessionRecord? continueRecord = _continueRecord;

            if (!isContinuation)
            {
                Transcript.Clear();
                ClearUndoHistory();
                SpeakerPalette.Reset();
            }

            string folder = string.IsNullOrWhiteSpace(OutputFolder) ? "output/transcripts" : OutputFolder;
            var options = isContinuation
                ? EngineFactory.CreateContinuationOptions(_config, continueRecord!)
                : EngineFactory.CreateSessionOptions(_config, folder);

            await _engine.StartAsync(options);
            SessionId = options.SessionId;

            if (!isContinuation)
            {
                SessionTitle = "";
                _sessionSpeakerNames.Clear();
            }

            CurrentJsonlPath = options.OutputJsonlPath;
            SetState(TranscriptionSessionState.Recording);

            // Clear continuation state now that recording is live
            if (isContinuation)
            {
                _continueRecord = null;
                ContinueBannerText = "";
                OnPropertyChanged(nameof(IsContinuing));
            }

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
        // The host owns and disposes the EngineIpcServer singleton; nothing to dispose here.
    }
}
