using LocalTranscriber.App.Mvvm;
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
    }

    public RelayCommand SaveCommand { get; }

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
