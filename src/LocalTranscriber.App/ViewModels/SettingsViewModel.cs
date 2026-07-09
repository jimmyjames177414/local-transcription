using System.Collections.ObjectModel;
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

    public SettingsViewModel(ConfigService? configService = null)
    {
        _configService = configService ?? new ConfigService();
        _config = _configService.Load();
        SaveCommand = new RelayCommand(Save);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }

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
        set { _config.WhisperModelPath = value; OnPropertyChanged(); }
    }

    public string SpeakerModelPath
    {
        get => _config.SpeakerModelPath;
        set { _config.SpeakerModelPath = value; OnPropertyChanged(); }
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
            SaveStatus = $"Saved at {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            SaveStatus = $"Save failed: {ex.Message}";
        }
    }

    public void Reload()
    {
        _config = _configService.Load();
        OnPropertyChanged(string.Empty);
    }
}
