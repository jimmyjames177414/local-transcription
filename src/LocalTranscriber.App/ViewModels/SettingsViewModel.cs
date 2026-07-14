using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using LocalTranscriber.App.Mvvm;
using LocalTranscriber.Audio;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private AppConfig _config;
    private string _saveStatus = "";
    private int _selectedSectionIndex;
    private DispatcherTimer? _saveStatusTimer;

    public SettingsViewModel(ConfigService? configService = null)
    {
        _configService = configService ?? new ConfigService();
        _config = _configService.Load();
        SaveCommand = new RelayCommand(Save);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }

    public string[] Sections { get; } = { "General", "Audio", "Models", "Assistant & privacy", "Integrations", "Advanced" };

    public int SelectedSectionIndex
    {
        get => _selectedSectionIndex;
        set
        {
            if (SetProperty(ref _selectedSectionIndex, value) && value == 4)
            {
                _ = RefreshMinutesStatusAsync();
            }
        }
    }

    public ObservableCollection<string> AudioDevices { get; } = new();

    private void RefreshDevices()
    {
        AudioDevices.Clear();
        try
        {
            var service = new AudioDeviceService();
            foreach (var d in service.ListInputDevices())
            {
                AudioDevices.Add($"[mic]{(d.IsDefault ? " (default)" : "")} {d.Name}");
            }
            foreach (var d in service.ListOutputDevices())
            {
                AudioDevices.Add($"[system]{(d.IsDefault ? " (default)" : "")} {d.Name}");
            }
            if (AudioDevices.Count == 0)
            {
                AudioDevices.Add("No audio devices found.");
            }
        }
        catch (Exception ex)
        {
            AudioDevices.Add($"Device enumeration failed: {ex.Message}");
        }
    }

    public string TranscriptFolder
    {
        get => _config.TranscriptFolder;
        set { _config.TranscriptFolder = value; OnPropertyChanged(); }
    }

    public bool EnableMicCapture
    {
        get => _config.EnableMicCapture;
        set { _config.EnableMicCapture = value; OnPropertyChanged(); }
    }

    public bool EnableSystemCapture
    {
        get => _config.EnableSystemCapture;
        set { _config.EnableSystemCapture = value; OnPropertyChanged(); }
    }

    public bool FilterNonSpeech
    {
        get => _config.FilterNonSpeech;
        set { _config.FilterNonSpeech = value; OnPropertyChanged(); }
    }

    public string DefaultMicSpeakerName
    {
        get => _config.DefaultMicSpeakerName;
        set { _config.DefaultMicSpeakerName = value; OnPropertyChanged(); }
    }

    public string WhisperModelPath
    {
        get => _config.WhisperModelPath;
        set { _config.WhisperModelPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(WhisperModelStatus)); }
    }

    public string SpeakerModelPath
    {
        get => _config.SpeakerModelPath;
        set { _config.SpeakerModelPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpeakerModelStatus)); }
    }

    public string WhisperModelStatus => DescribeModelPath(_config.WhisperModelPath);
    public string SpeakerModelStatus => DescribeModelPath(_config.SpeakerModelPath);

    private static string DescribeModelPath(string path)
        => string.IsNullOrWhiteSpace(path) ? "✗ no path set"
            : File.Exists(path) || Directory.Exists(path) ? "✓ found"
            : "✗ not found";

    /// <summary>
    /// The stored streaming consent (agent.realtime.sendAudio). Persisted immediately — turning
    /// it off means the next switch to pushToTalk/continuous shows the consent dialog again.
    /// </summary>
    public bool AllowMicStreaming
    {
        get => _config.Agent.Realtime.SendAudio;
        set
        {
            _config.Agent.Realtime.SendAudio = value;
            try
            {
                var fresh = _configService.Load();
                fresh.Agent.Realtime.SendAudio = value;
                _configService.Save(fresh);
            }
            catch (Exception ex)
            {
                SaveStatus = $"Save failed: {ex.Message}";
            }
            OnPropertyChanged();
        }
    }

    public double SpeakerMatchThreshold
    {
        get => _config.SpeakerMatchThreshold;
        set { _config.SpeakerMatchThreshold = value; OnPropertyChanged(); }
    }

    public int ChunkSeconds
    {
        get => _config.ChunkSeconds;
        set { _config.ChunkSeconds = value; OnPropertyChanged(); }
    }

    public bool ExportMinutesOnStop
    {
        get => _config.MinutesExport.Enabled;
        set { _config.MinutesExport.Enabled = value; OnPropertyChanged(); }
    }

    public string MinutesFolder
    {
        get => _config.MinutesExport.Folder;
        set { _config.MinutesExport.Folder = value; OnPropertyChanged(); }
    }

    // === Integrations (design 4m) ===

    private string _minutesStatusText = "";
    private bool _isSyncing;
    private AsyncRelayCommand? _syncAllCommand;

    public AsyncRelayCommand SyncAllNowCommand => _syncAllCommand ??= new AsyncRelayCommand(SyncAllAsync, () => !_isSyncing);

    public string MinutesStatusText
    {
        get => _minutesStatusText;
        private set => SetProperty(ref _minutesStatusText, value);
    }

    /// <summary>Path agents can tail mid-meeting; the live session's .jsonl lands in this folder.</summary>
    public string LiveFeedPathText => Path.Combine(Path.GetFullPath(_config.TranscriptFolder), "session-<id>.jsonl");

    private async Task RefreshMinutesStatusAsync()
    {
        try
        {
            var config = _configService.Load();
            var db = new SqliteDatabase(config.DatabasePath);
            var sessions = await new SqliteSessionStore(db).ListAsync();
            var finished = sessions.Where(s => s.Status != "recording").ToList();

            int synced = finished.Count(s => MinutesExporter.FindExportedFiles(config.MinutesExport.Folder, s.Id).Length > 0);
            var lastSynced = finished.FirstOrDefault(s => MinutesExporter.FindExportedFiles(config.MinutesExport.Folder, s.Id).Length > 0);
            string last = lastSynced is null
                ? "nothing published yet"
                : $"last publish: {(string.IsNullOrWhiteSpace(lastSynced.Title) ? $"Meeting {lastSynced.StartedAt.ToLocalTime():HH:mm}" : lastSynced.Title)} · {lastSynced.StartedAt.ToLocalTime():HH:mm}";

            MinutesStatusText = $"✓ connected · {last} · {synced}/{finished.Count} sessions synced";
        }
        catch (Exception ex)
        {
            MinutesStatusText = $"status unavailable: {ex.Message}";
        }
    }

    private async Task SyncAllAsync()
    {
        _isSyncing = true;
        SyncAllNowCommand.RaiseCanExecuteChanged();
        try
        {
            var service = new MinutesExportService(_configService.Load());
            var written = await service.ExportMissingAsync();
            MinutesStatusText = written.Count == 0
                ? "✓ everything already synced"
                : $"✓ published {written.Count} session{(written.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            MinutesStatusText = $"sync failed: {ex.Message}";
        }
        finally
        {
            _isSyncing = false;
            SyncAllNowCommand.RaiseCanExecuteChanged();
        }
    }

    public string SaveStatus
    {
        get => _saveStatus;
        private set => SetProperty(ref _saveStatus, value);
    }

    private void Save()
    {
        try
        {
            _configService.Save(_config);
            SaveStatus = "✓ Saved just now";
            _saveStatusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _saveStatusTimer.Tick -= OnSaveStatusExpired;
            _saveStatusTimer.Tick += OnSaveStatusExpired;
            _saveStatusTimer.Stop();
            _saveStatusTimer.Start();
        }
        catch (Exception ex)
        {
            SaveStatus = $"Save failed: {ex.Message}";
        }
    }

    private void OnSaveStatusExpired(object? sender, EventArgs e)
    {
        _saveStatusTimer?.Stop();
        SaveStatus = "";
    }

    public void Reload()
    {
        _config = _configService.Load();
        OnPropertyChanged(string.Empty);
    }
}
