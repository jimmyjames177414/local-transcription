using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using LocalTranscriber.App.Mvvm;
using LocalTranscriber.Audio;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;
using LocalTranscriber.Voice;

namespace LocalTranscriber.App.ViewModels;

/// <summary>
/// Agent tab: real-time voice conversation controls (mode, start/stop, hold-to-talk, live
/// captions) plus the agent's shared config (enable flag, context/output folders).
/// The old suggestion surface has been removed.
/// </summary>
public sealed class AgentPanelViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly Func<string?> _currentTranscriptPath;
    private readonly SynchronizationContext? _uiContext;

    private IRealtimeVoiceConversation? _voice;
    private string _statusText = "Voice conversation not started";
    private string _captions = "";
    private bool _agentEnabled;
    private string _selectedVoiceMode;
    private string _voiceName;
    private string? _selectedInputDeviceId;
    private string? _selectedOutputDeviceId;
    private bool _speakReplies;
    private string _contextFolder;
    private string _outputFolder;

    public AgentPanelViewModel(ConfigService? configService = null, Func<string?>? currentTranscriptPath = null)
    {
        _configService = configService ?? new ConfigService();
        _currentTranscriptPath = currentTranscriptPath ?? (() => null);
        _uiContext = SynchronizationContext.Current;

        var config = _configService.Load();
        _agentEnabled = config.Agent.Enabled;
        _selectedVoiceMode = config.Agent.Realtime.VoiceMode;
        _voiceName = config.Agent.Realtime.Voice;
        _selectedInputDeviceId = config.Agent.Realtime.InputAudioDeviceId;
        _selectedOutputDeviceId = config.Agent.Realtime.OutputAudioDeviceId;
        _speakReplies = config.Agent.Realtime.SpeakReplies;
        _contextFolder = config.Agent.ContextFolder;
        _outputFolder = config.Agent.AgentOutputFolder;

        StartVoiceCommand = new AsyncRelayCommand(StartVoiceAsync, () => _voice is null && SelectedVoiceMode != "off");
        StopVoiceCommand = new AsyncRelayCommand(StopVoiceAsync, () => _voice is not null);
        RefreshInputDevicesCommand = new RelayCommand(RefreshDevices);
        OpenContextFolderCommand = new RelayCommand(() => OpenFolder(ContextFolder));
        OpenOutputFolderCommand = new RelayCommand(() => OpenFolder(OutputFolder));

        RefreshDevices();
    }

    public string[] VoiceModes { get; } = { "off", "hybrid", "pushToTalk", "continuous" };

    // Authoritative list returned by the Realtime server for gpt-realtime-2.1-mini
    // (session.audio.output.voice "Supported values"). Model-specific; re-probe if the model changes.
    public string[] Voices { get; } = { "alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar" };

    /// <summary>Microphone choices for voice input. A separate mic from a Bluetooth headset keeps
    /// that headset in high-quality A2DP playback (opening its mic forces low-quality HFP and, on
    /// some drivers, drops the device — see docs/REALTIME_PROVIDER.md).</summary>
    public ObservableCollection<VoiceInputDevice> InputDevices { get; } = new();

    /// <summary>Playback choices for the AI's spoken reply. Routing replies to a non-Bluetooth
    /// output (e.g. laptop speakers) keeps a Bluetooth headset in A2DP, so opening its mic no longer
    /// triggers the hands-free profile flap that drops the mic.</summary>
    public ObservableCollection<VoiceInputDevice> OutputDevices { get; } = new();

    public AsyncRelayCommand StartVoiceCommand { get; }
    public AsyncRelayCommand StopVoiceCommand { get; }
    public RelayCommand RefreshInputDevicesCommand { get; }
    public RelayCommand OpenContextFolderCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }

    public string? SelectedInputDeviceId
    {
        get => _selectedInputDeviceId;
        set
        {
            SetProperty(ref _selectedInputDeviceId, value);
            PersistConfig(c => c.Agent.Realtime.InputAudioDeviceId = value);
        }
    }

    public string? SelectedOutputDeviceId
    {
        get => _selectedOutputDeviceId;
        set
        {
            SetProperty(ref _selectedOutputDeviceId, value);
            PersistConfig(c => c.Agent.Realtime.OutputAudioDeviceId = value);
        }
    }

    /// <summary>When off, the reply shows as captions only (no playback) — the safest option when a
    /// Bluetooth headset is the mic, since nothing pulls it back to A2DP.</summary>
    public bool SpeakReplies
    {
        get => _speakReplies;
        set
        {
            SetProperty(ref _speakReplies, value);
            PersistConfig(c => c.Agent.Realtime.SpeakReplies = value);
        }
    }

    private void RefreshDevices()
    {
        InputDevices.Clear();
        InputDevices.Add(new VoiceInputDevice(null, "Default microphone"));
        OutputDevices.Clear();
        OutputDevices.Add(new VoiceInputDevice(null, "Default playback"));
        try
        {
            var devices = new AudioDeviceService();
            foreach (var d in devices.ListInputDevices())
            {
                InputDevices.Add(new VoiceInputDevice(d.Id, d.Name + (d.IsDefault ? " (default)" : "")));
            }
            foreach (var d in devices.ListOutputDevices())
            {
                OutputDevices.Add(new VoiceInputDevice(d.Id, d.Name + (d.IsDefault ? " (default)" : "")));
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Could not list audio devices: {ex.Message}";
        }
    }

    public bool AgentEnabled
    {
        get => _agentEnabled;
        set
        {
            SetProperty(ref _agentEnabled, value);
            PersistConfig(c => c.Agent.Enabled = value);
        }
    }

    public string SelectedVoiceMode
    {
        get => _selectedVoiceMode;
        set
        {
            SetProperty(ref _selectedVoiceMode, value);
            PersistConfig(c => c.Agent.Realtime.VoiceMode = value);
            StartVoiceCommand.RaiseCanExecuteChanged();
        }
    }

    public string VoiceName
    {
        get => _voiceName;
        set
        {
            SetProperty(ref _voiceName, value);
            PersistConfig(c => c.Agent.Realtime.Voice = value);
        }
    }

    public string ContextFolder
    {
        get => _contextFolder;
        set
        {
            SetProperty(ref _contextFolder, value);
            PersistConfig(c => c.Agent.ContextFolder = value);
        }
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set
        {
            SetProperty(ref _outputFolder, value);
            PersistConfig(c => c.Agent.AgentOutputFolder = value);
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string Captions
    {
        get => _captions;
        private set => SetProperty(ref _captions, value);
    }

    private async Task StartVoiceAsync()
    {
        try
        {
            Captions = "";
            var config = _configService.Load();
            // Starting from the UI is the explicit action that opens the realtime connection.
            config.Agent.Realtime.Enabled = true;
            _configService.Save(config);

            var resolution = RealtimeVoiceFactory.Create(config, new SecretsService(), _currentTranscriptPath());
            if (resolution.Session is null)
            {
                StatusText = resolution.Notice ?? "Voice unavailable.";
                return;
            }

            _voice = resolution.Session;
            _voice.AssistantTextAvailable += OnAssistantText;
            _voice.StateChanged += OnVoiceStateChanged;
            _voice.ErrorOccurred += OnVoiceError;

            await _voice.StartAsync();
            StatusText = $"Voice started ({SelectedVoiceMode}). Hold the Talk button to speak.";
        }
        catch (Exception ex)
        {
            StatusText = $"Start voice failed: {ex.Message}";
            _voice = null;
        }
        finally
        {
            StartVoiceCommand.RaiseCanExecuteChanged();
            StopVoiceCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task StopVoiceAsync()
    {
        try
        {
            if (_voice is not null)
            {
                _voice.AssistantTextAvailable -= OnAssistantText;
                _voice.StateChanged -= OnVoiceStateChanged;
                _voice.ErrorOccurred -= OnVoiceError;
                await _voice.DisposeAsync();
                StatusText = "Voice conversation stopped.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Stop voice failed: {ex.Message}";
        }
        finally
        {
            _voice = null;
            StartVoiceCommand.RaiseCanExecuteChanged();
            StopVoiceCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Called from the Talk button's PreviewMouseLeftButtonDown.</summary>
    public void VoicePushToTalkDown() => _voice?.PushToTalkDown();

    /// <summary>Called from the Talk button's PreviewMouseLeftButtonUp.</summary>
    public void VoicePushToTalkUp() => _voice?.PushToTalkUp();

    private void OnAssistantText(object? sender, string text)
        => PostToUi(() => Captions += text);

    private void OnVoiceStateChanged(object? sender, RealtimeVoiceState state)
        => PostToUi(() => StatusText = $"Voice: {state}");

    private void OnVoiceError(object? sender, string message)
        => PostToUi(() => StatusText = message);

    private void PersistConfig(Action<AppConfig> mutate)
    {
        try
        {
            var config = _configService.Load();
            mutate(config);
            _configService.Save(config);
        }
        catch (Exception ex)
        {
            StatusText = $"Config save failed: {ex.Message}";
        }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", Path.GetFullPath(folder)) { UseShellExecute = true });
        }
        catch
        {
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

    public async Task ShutdownAsync()
    {
        if (_voice is not null)
        {
            await _voice.DisposeAsync();
            _voice = null;
        }
    }
}

/// <summary>A microphone option for the voice input dropdown (Id null = Windows default).</summary>
public sealed record VoiceInputDevice(string? Id, string Name);
