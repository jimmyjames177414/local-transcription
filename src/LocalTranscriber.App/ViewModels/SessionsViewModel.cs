using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using LocalTranscriber.App.Mvvm;
using LocalTranscriber.App.Services;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.App.ViewModels;

/// <summary>A participant chip on a session row (name + deterministic color).</summary>
public sealed record ParticipantChip(string Name, Brush Color);

/// <summary>One row in the Sessions list (design 4j).</summary>
public sealed class SessionListItemViewModel
{
    public SessionListItemViewModel(SessionSummary summary, string minutesFolder, bool minutesEnabled, string notesFolder)
    {
        Summary = summary;
        var s = summary.Session;
        var started = s.StartedAt.ToLocalTime();

        Title = string.IsNullOrWhiteSpace(s.Title) ? $"Meeting {started:HH:mm}" : s.Title!;
        DayGroup = started.Date == DateTime.Today ? "TODAY"
            : started.Date == DateTime.Today.AddDays(-1) ? "YESTERDAY"
            : started.ToString("MMMM d, yyyy").ToUpperInvariant();
        TimeAndDuration = $"{started:HH:mm} · {FormatDuration(s, summary.LastEventAt)}";

        Participants = summary.SpeakerNames.Take(4)
            .Select(n => new ParticipantChip(n, SpeakerPalette.GetBrushForName(n)))
            .ToList();
        ExtraParticipants = summary.SpeakerNames.Count > 4 ? $"+{summary.SpeakerNames.Count - 4}" : "";

        HasNotes = File.Exists(Path.Combine(notesFolder, $"notes-{s.Id}.md"));
        IsSynced = MinutesExporter.FindExportedFiles(minutesFolder, s.Id).Length > 0;
        MinutesBadgeVisible = minutesEnabled && s.Status != "recording";

        SizeBytes = ProbeSize(s.OutputTextPath) + ProbeSize(s.OutputJsonlPath);
        FileSizeText = FormatSize(SizeBytes);
    }

    public SessionSummary Summary { get; }
    public SessionRecord Session => Summary.Session;
    public string Title { get; }
    public string DayGroup { get; }
    public string TimeAndDuration { get; }
    public IReadOnlyList<ParticipantChip> Participants { get; }
    public string ExtraParticipants { get; }
    public bool HasNotes { get; }
    public bool IsSynced { get; }
    public bool MinutesBadgeVisible { get; }
    public string MinutesBadgeText => IsSynced ? "min ✓" : "min ⟳ pending";
    public long SizeBytes { get; }
    public string FileSizeText { get; }

    public static string FormatDuration(SessionRecord s, DateTimeOffset? lastEventAt = null)
    {
        // Prefer the real end; fall back to the last event for a session whose ended_at was never
        // written (still recording, or abandoned before a clean stop) so it never collapses to "1m".
        var end = s.EndedAt ?? lastEventAt ?? s.StartedAt;
        var span = end - s.StartedAt;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))}m";
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B"
    };

    private static long ProbeSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>Sessions screen: browse, search, manage and load saved meetings (design 4j).</summary>
public sealed class SessionsViewModel : ObservableObject
{
    private const int PreviewRowCount = 30;

    private readonly ConfigService _configService;
    private readonly ISessionStore _sessions;
    private readonly ITranscriptEventStore _events;
    private readonly ISpeakerNameResolver _nameResolver;
    private readonly Func<bool> _isRecording;
    private readonly DispatcherTimer _searchDebounce;

    private List<SessionListItemViewModel> _all = new();
    private IReadOnlyList<string> _textMatchIds = Array.Empty<string>();
    private SessionListItemViewModel? _selected;
    private string _searchText = "";
    private int _selectedFilterIndex;
    private string _footerText = "";
    private string _statusText = "";
    private string _metaText = "";
    private bool _isRenaming;
    private string _editTitle = "";

    public SessionsViewModel(ConfigService? configService = null, Func<bool>? isRecording = null,
        ISessionStore? sessions = null, ITranscriptEventStore? events = null,
        ISpeakerNameResolver? nameResolver = null)
    {
        _configService = configService ?? new ConfigService();
        _isRecording = isRecording ?? (() => false);
        var config = _configService.Load();
        var db = new SqliteDatabase(config.DatabasePath);
        _sessions = sessions ?? new SqliteSessionStore(db);
        _events = events ?? new SqliteTranscriptEventStore(db);
        // Resolves speakers renamed mid-meeting to their final name at read time, so a saved review
        // shows the rename retroactively on every line (early "Speaker 1" lines included).
        // The override store also handles "just this line" per-event corrections.
        _nameResolver = nameResolver
            ?? new SqliteSpeakerNameResolver(new SqliteEventSpeakerOverrideStore(db), new SqliteSpeakerAliasStore(db), new SqliteKnownSpeakerStore(db));

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounce.Tick += async (_, _) =>
        {
            _searchDebounce.Stop();
            await RunTextSearchAsync();
        };

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        LoadCommand = new AsyncRelayCommand(LoadSelectedAsync, () => Selected is not null && !_isRecording());
        OpenFolderCommand = new RelayCommand(OpenTranscriptFolder);
        ExportCommand = new AsyncRelayCommand(ExportSelectedAsync, () => Selected is not null);
        DeleteCommand = new RelayCommand(() =>
        {
            if (Selected is not null)
            {
                DeleteRequested?.Invoke(Selected);
            }
        });
        BeginRenameCommand = new RelayCommand(() =>
        {
            if (Selected is not null)
            {
                EditTitle = Selected.Title;
                IsRenaming = true;
            }
        });
        CommitRenameCommand = new AsyncRelayCommand(CommitRenameAsync, () => !string.IsNullOrWhiteSpace(EditTitle));
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        ContinueCommand = new AsyncRelayCommand(ContinueSelectedAsync,
            () => Selected is not null && !_isRecording() && Selected.Session.Status != "recording");
    }

    /// <summary>Raised when the user loads a session for review (full events included).</summary>
    public event Action<SessionRecord, IReadOnlyList<TranscriptEvent>>? LoadRequested;

    /// <summary>Raised when the user wants to continue a session (append new recording to it).</summary>
    public event Action<SessionRecord, IReadOnlyList<TranscriptEvent>>? ContinueRequested;

    /// <summary>Raised when the user asks to delete a session; the shell shows the confirm dialog.</summary>
    public event Action<SessionListItemViewModel>? DeleteRequested;

    public ObservableCollection<SessionListItemViewModel> Items { get; } = new();
    public ObservableCollection<TranscriptRowViewModel> PreviewRows { get; } = new();
    public ObservableCollection<string> NotesDecisions { get; } = new();
    public ObservableCollection<string> NotesActionItems { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand ContinueCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand BeginRenameCommand { get; }
    public AsyncRelayCommand CommitRenameCommand { get; }
    public RelayCommand CancelRenameCommand { get; }

    public string[] Filters { get; } = { "All", "With notes", "Synced", "Not synced" };

    public int SelectedFilterIndex
    {
        get => _selectedFilterIndex;
        set
        {
            SetProperty(ref _selectedFilterIndex, value);
            ApplyFilter();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _searchDebounce.Stop();
                if (string.IsNullOrWhiteSpace(value))
                {
                    _textMatchIds = Array.Empty<string>();
                    ApplyFilter();
                }
                else
                {
                    ApplyFilter(); // instant title/participant filtering
                    _searchDebounce.Start(); // transcript text search follows
                }
            }
        }
    }

    public SessionListItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                IsRenaming = false;
                LoadCommand.RaiseCanExecuteChanged();
                ContinueCommand.RaiseCanExecuteChanged();
                ExportCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedTitle));
                _ = LoadDetailAsync(value);
            }
        }
    }

    public bool HasSelection => Selected is not null;
    public string SelectedTitle => Selected?.Title ?? "";

    public string MetaText
    {
        get => _metaText;
        private set => SetProperty(ref _metaText, value);
    }

    public string FooterText
    {
        get => _footerText;
        private set => SetProperty(ref _footerText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        private set => SetProperty(ref _isRenaming, value);
    }

    public string EditTitle
    {
        get => _editTitle;
        set
        {
            SetProperty(ref _editTitle, value);
            CommitRenameCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            var config = _configService.Load();
            string notesFolder = config.Agent.AgentOutputFolder;
            string minutesFolder = config.MinutesExport.Folder;
            bool minutesEnabled = config.MinutesExport.Enabled;

            var summaries = await _sessions.ListSummariesAsync();
            string? selectedId = Selected?.Session.Id;

            _all = summaries
                .Select(s => new SessionListItemViewModel(s, minutesFolder, minutesEnabled, notesFolder))
                .ToList();

            long totalBytes = _all.Sum(i => i.SizeBytes);
            FooterText = $"{_all.Count} session{(_all.Count == 1 ? "" : "s")} · {SessionListItemViewModel.FormatSize(totalBytes)} in {config.TranscriptFolder}";

            ApplyFilter();
            Selected = _all.FirstOrDefault(i => i.Session.Id == selectedId) ?? Items.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load sessions: {ex.Message}";
        }
    }

    private async Task RunTextSearchAsync()
    {
        try
        {
            string query = SearchText.Trim();
            _textMatchIds = query.Length == 0
                ? Array.Empty<string>()
                : await _events.SearchSessionIdsAsync(query);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText = $"Search failed: {ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        string query = SearchText.Trim();
        IEnumerable<SessionListItemViewModel> filtered = _all;

        filtered = SelectedFilterIndex switch
        {
            1 => filtered.Where(i => i.HasNotes),
            2 => filtered.Where(i => i.IsSynced),
            3 => filtered.Where(i => !i.IsSynced),
            _ => filtered
        };

        if (query.Length > 0)
        {
            filtered = filtered.Where(i =>
                i.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || i.Summary.SpeakerNames.Any(n => n.Contains(query, StringComparison.OrdinalIgnoreCase))
                || _textMatchIds.Contains(i.Session.Id));
        }

        Items.Clear();
        foreach (var item in filtered)
        {
            Items.Add(item);
        }
    }

    private async Task LoadDetailAsync(SessionListItemViewModel? item)
    {
        PreviewRows.Clear();
        NotesDecisions.Clear();
        NotesActionItems.Clear();
        MetaText = "";
        if (item is null)
        {
            return;
        }

        try
        {
            var config = _configService.Load();
            var s = item.Session;
            var started = s.StartedAt.ToLocalTime();
            string when = started.Date == DateTime.Today ? "Today" : started.ToString("yyyy-MM-dd");
            string range = s.EndedAt is { } ended ? $"{started:HH:mm}–{ended.ToLocalTime():HH:mm}" : $"{started:HH:mm}";
            string files = ".txt · .jsonl" + (item.HasNotes ? " · notes.md" : "");
            MetaText = $"{when} {range} · {SessionListItemViewModel.FormatDuration(s, item.Summary.LastEventAt)} · #{s.Id[..Math.Min(8, s.Id.Length)]} · {files}";

            var events = await ResolveNamesAsync(s.Id, await _events.ListBySessionAsync(s.Id));
            foreach (var e in events.Take(PreviewRowCount))
            {
                PreviewRows.Add(new TranscriptRowViewModel(e, config.SpeakerMatchThreshold));
            }

            string notesPath = Path.Combine(config.Agent.AgentOutputFolder, $"notes-{s.Id}.md");
            if (File.Exists(notesPath))
            {
                var notes = NotesDocument.Parse(File.ReadAllText(notesPath), s.Id);
                foreach (var d in notes[NoteSection.Decisions])
                {
                    NotesDecisions.Add(d.Text);
                }
                foreach (var a in notes[NoteSection.ActionItems])
                {
                    NotesActionItems.Add(a.Text);
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load session detail: {ex.Message}";
        }
    }

    private async Task LoadSelectedAsync()
    {
        if (Selected is null)
        {
            return;
        }

        try
        {
            var events = await ResolveNamesAsync(Selected.Session.Id, await _events.ListBySessionAsync(Selected.Session.Id));
            LoadRequested?.Invoke(Selected.Session, events);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load session: {ex.Message}";
        }
    }

    /// <summary>
    /// Overlays the current display name onto each event via the alias/known-speaker resolver, so a
    /// voice renamed mid-meeting shows its final name on every line — including the early lines that
    /// were captured before the rename. Events the resolver doesn't know are returned unchanged.
    /// </summary>
    private async Task<IReadOnlyList<TranscriptEvent>> ResolveNamesAsync(string sessionId, IReadOnlyList<TranscriptEvent> events)
    {
        var resolved = new List<TranscriptEvent>(events.Count);
        foreach (var e in events)
        {
            string? name = await _nameResolver.ResolveDisplayNameAsync(sessionId, e.Speaker.SpeakerId, e.Id);
            resolved.Add(name is null || name == e.Speaker.DisplayName
                ? e
                : e with { Speaker = e.Speaker with { DisplayName = name } });
        }
        return resolved;
    }

    private async Task ExportSelectedAsync()
    {
        if (Selected is null)
        {
            return;
        }

        try
        {
            var service = new MinutesExportService(_configService.Load(), _sessions, _events);
            string path = await service.ExportAsync(Selected.Session.Id);
            StatusText = $"Exported: {path}";
            await RefreshAsync(); // sync badge flips to "min ✓"
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    private async Task CommitRenameAsync()
    {
        if (Selected is null)
        {
            return;
        }

        try
        {
            await _sessions.UpdateTitleAsync(Selected.Session.Id, EditTitle.Trim());
            IsRenaming = false;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Rename failed: {ex.Message}";
        }
    }

    private void OpenTranscriptFolder()
    {
        try
        {
            string folder = Path.GetFullPath(_configService.Load().TranscriptFolder);
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    /// <summary>Called by the shell when recording state changes (Load canExecute + list freshness).</summary>
    public void OnRecordingStateChanged()
    {
        LoadCommand.RaiseCanExecuteChanged();
        ContinueCommand.RaiseCanExecuteChanged();
    }

    private async Task ContinueSelectedAsync()
    {
        if (Selected is null) return;
        try
        {
            var events = await ResolveNamesAsync(Selected.Session.Id, await _events.ListBySessionAsync(Selected.Session.Id));
            ContinueRequested?.Invoke(Selected.Session, events);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load session: {ex.Message}";
        }
    }
}
