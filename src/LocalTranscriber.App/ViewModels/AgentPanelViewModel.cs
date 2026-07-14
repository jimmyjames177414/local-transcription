using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using LocalTranscriber.App.Mvvm;
using LocalTranscriber.Audio;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;
using LocalTranscriber.Voice;

namespace LocalTranscriber.App.ViewModels;

/// <summary>Assistant activity shown by the status pill in the chat header.</summary>
public enum AgentPillState
{
    Off,
    Connecting,
    Listening,
    Thinking,
    Speaking,
    Error
}

/// <summary>Visual phases of the hold-to-talk button.</summary>
public enum HoldToTalkPhase
{
    Idle,
    Held,
    Transcribing
}

/// <summary>
/// Assistant panel: real-time voice/text conversation (chat bubbles, typed input, hold-to-talk,
/// status pill) plus the agent's shared config (enable flag, devices, context/output folders).
/// </summary>
public sealed class AgentPanelViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly Func<string?> _currentTranscriptPath;
    private readonly SynchronizationContext? _uiContext;

    private IRealtimeVoiceConversation? _voice;
    private string _statusText = "Assistant off";
    private string _chatInput = "";
    private bool _agentEnabled;
    private string _selectedVoiceMode;
    private string _voiceName;
    private string? _selectedInputDeviceId;
    private string? _selectedOutputDeviceId;
    private bool _speakReplies;
    private string _contextFolder;
    private string _outputFolder;
    private AgentPillState _pillState = AgentPillState.Off;
    private HoldToTalkPhase _holdPhase = HoldToTalkPhase.Idle;
    private bool _holdPending;

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
        SendTextCommand = new AsyncRelayCommand(SendTextAsync, () => AgentEnabled && SelectedVoiceMode != "off");
        RefreshInputDevicesCommand = new RelayCommand(RefreshDevices);
        OpenContextFolderCommand = new RelayCommand(() => OpenFolder(ContextFolder));
        OpenOutputFolderCommand = new RelayCommand(() => OpenFolder(OutputFolder));
        EnableAssistantCommand = new RelayCommand(() =>
        {
            AgentEnabled = true;
            if (SelectedVoiceMode == "off")
            {
                SelectedVoiceMode = "hybrid";
            }
        });

        RefreshDevices();
        ProbeApiKey(config);
    }

    private bool _hasApiKey = true; // optimistic until the probe completes
    private string _apiKeyNotice = "";

    /// <summary>False when no OpenAI key resolves (env var or secrets.json).</summary>
    public bool HasApiKey
    {
        get => _hasApiKey;
        private set => SetProperty(ref _hasApiKey, value);
    }

    public string ApiKeyNotice
    {
        get => _apiKeyNotice;
        private set => SetProperty(ref _apiKeyNotice, value);
    }

    public RelayCommand EnableAssistantCommand { get; }

    private void ProbeApiKey(AppConfig config)
        => _ = Task.Run(() =>
        {
            try
            {
                var (key, reason) = new SecretsService().ResolveOpenAIKey(config.Agent.Realtime.ApiKeyEnvironmentVariable);
                PostToUi(() =>
                {
                    HasApiKey = key is not null;
                    ApiKeyNotice = key is null
                        ? $"API key missing — assistant is unavailable. Transcription is unaffected. ({reason})"
                        : "";
                });
            }
            catch
            {
                // Probe is advisory only.
            }
        });

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

    /// <summary>The conversation, newest last. Assistant replies stream into the trailing bubble.</summary>
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    /// <summary>
    /// Set by the shell: writes the full notes markdown file. Used by the AI's write_notes tool.
    /// </summary>
    public Func<string, Task>? SaveNote { get; set; }

    public AsyncRelayCommand StartVoiceCommand { get; }
    public AsyncRelayCommand StopVoiceCommand { get; }
    public AsyncRelayCommand SendTextCommand { get; }
    public RelayCommand RefreshInputDevicesCommand { get; }
    public RelayCommand OpenContextFolderCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }

    public string ChatInput
    {
        get => _chatInput;
        set => SetProperty(ref _chatInput, value);
    }

    public AgentPillState PillState
    {
        get => _pillState;
        private set
        {
            if (SetProperty(ref _pillState, value))
            {
                OnPropertyChanged(nameof(PillText));
                OnPropertyChanged(nameof(IsPillActive));
            }
        }
    }

    /// <summary>True while the assistant is doing something (drives the pill's pulse animation).</summary>
    public bool IsPillActive => _pillState is AgentPillState.Connecting or AgentPillState.Listening
        or AgentPillState.Thinking or AgentPillState.Speaking;

    public string PillText => _pillState switch
    {
        AgentPillState.Off => "Off",
        AgentPillState.Connecting => "Connecting…",
        AgentPillState.Listening => "Listening",
        AgentPillState.Thinking => "Thinking",
        AgentPillState.Speaking => "Speaking",
        AgentPillState.Error => "Error",
        _ => _pillState.ToString()
    };

    public HoldToTalkPhase HoldPhase
    {
        get => _holdPhase;
        private set => SetProperty(ref _holdPhase, value);
    }

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
            OnPropertyChanged(nameof(AgentCloudActive));
            OnPropertyChanged(nameof(PrivacyLocalText));
            OnPropertyChanged(nameof(PrivacyAgentText));
            SendTextCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Raised when switching to a streaming mode without stored consent (sendAudio=false).
    /// The shell shows the consent dialog and calls <see cref="ApplyVoiceMode"/> with the outcome.
    /// </summary>
    public event EventHandler<string>? ConsentRequested;

    public string SelectedVoiceMode
    {
        get => _selectedVoiceMode;
        set
        {
            if (value == _selectedVoiceMode)
            {
                return;
            }

            if (value is "pushToTalk" or "continuous" && !_configService.Load().Agent.Realtime.SendAudio)
            {
                // Snap the ComboBox back to the current mode, then ask for consent (4c dialog).
                string requested = value;
                PostToUi(() =>
                {
                    OnPropertyChanged(nameof(SelectedVoiceMode));
                    ConsentRequested?.Invoke(this, requested);
                });
                return;
            }

            ApplyVoiceModeCore(value);
        }
    }

    /// <summary>Applies a mode after the consent dialog; grantsAudioConsent persists sendAudio=true.</summary>
    public void ApplyVoiceMode(string mode, bool grantAudioConsent)
    {
        if (grantAudioConsent)
        {
            PersistConfig(c => c.Agent.Realtime.SendAudio = true);
        }

        ApplyVoiceModeCore(mode);
    }

    private void ApplyVoiceModeCore(string value)
    {
        SetProperty(ref _selectedVoiceMode, value, nameof(SelectedVoiceMode));
        PersistConfig(c => c.Agent.Realtime.VoiceMode = value);
        OnPropertyChanged(nameof(ModeBadgeText));
        OnPropertyChanged(nameof(ModeSendsAudio));
        OnPropertyChanged(nameof(AgentCloudActive));
        OnPropertyChanged(nameof(PrivacyLocalText));
        OnPropertyChanged(nameof(PrivacyAgentText));
        StartVoiceCommand.RaiseCanExecuteChanged();
        SendTextCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Chat-header badge, e.g. "hybrid · text-only" / "pushToTalk · mic streams".</summary>
    public string ModeBadgeText => _selectedVoiceMode switch
    {
        "hybrid" => "hybrid · text-only",
        "pushToTalk" => "pushToTalk · mic while held",
        "continuous" => "continuous · mic streaming",
        _ => "off"
    };

    /// <summary>True for the modes that stream microphone audio to the cloud.</summary>
    public bool ModeSendsAudio => _selectedVoiceMode is "pushToTalk" or "continuous";

    /// <summary>True when the assistant can reach the cloud at all (enabled + a mode picked).</summary>
    public bool AgentCloudActive => AgentEnabled && _selectedVoiceMode != "off";

    /// <summary>Header readout, green segment: what stays local.</summary>
    public string PrivacyLocalText => AgentCloudActive ? "transcribe → this PC" : "everything local";

    /// <summary>Header readout, amber segment: what the agent sends (visible when AgentCloudActive).</summary>
    public string PrivacyAgentText => ModeSendsAudio ? "agent → mic audio" : "agent → text only";

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

    private async Task StartVoiceAsync()
    {
        try
        {
            PillState = AgentPillState.Connecting;
            var config = _configService.Load();
            // Starting from the UI is the explicit action that opens the realtime connection.
            config.Agent.Realtime.Enabled = true;
            _configService.Save(config);

            var resolution = RealtimeVoiceFactory.Create(
                config, new SecretsService(), _currentTranscriptPath(),
                tools: BuildTools(), toolHandler: SaveNote is null ? null : HandleToolCallAsync);
            if (resolution.Session is null)
            {
                StatusText = resolution.Notice ?? "Voice unavailable.";
                PillState = AgentPillState.Off;
                return;
            }

            _voice = resolution.Session;
            _voice.AssistantTextAvailable += OnAssistantText;
            _voice.StateChanged += OnVoiceStateChanged;
            _voice.ErrorOccurred += OnVoiceError;
            _voice.UserTextCommitted += OnUserTextCommitted;
            _voice.ResponseCompleted += OnResponseCompleted;

            await _voice.StartAsync();
            StatusText = $"Voice started ({SelectedVoiceMode}).";
        }
        catch (Exception ex)
        {
            StatusText = $"Start voice failed: {ex.Message}";
            _voice = null;
            PillState = AgentPillState.Error;
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
                _voice.UserTextCommitted -= OnUserTextCommitted;
                _voice.ResponseCompleted -= OnResponseCompleted;
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
            PillState = AgentPillState.Off;
            HoldPhase = HoldToTalkPhase.Idle;
            StartVoiceCommand.RaiseCanExecuteChanged();
            StopVoiceCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task SendTextAsync()
    {
        string text = ChatInput.Trim();
        if (text.Length == 0)
        {
            return;
        }

        ChatInput = "";

        // Typing is an explicit user action: lazily open the connection like "Start voice" would.
        if (_voice is null)
        {
            await StartVoiceAsync();
            if (_voice is null)
            {
                return; // StatusText already explains (no key, mode off, connect failed...)
            }
        }

        Messages.Add(new ChatMessageViewModel(ChatRole.User, text));
        try
        {
            await _voice.SendUserTextAsync(text);
        }
        catch (Exception ex)
        {
            StatusText = $"Send failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Stops the realtime session if one is connected. Used when switching between live and
    /// archive grounding — the transcript path is captured at connect, so the session must
    /// reconnect (lazily, on next send) to ground on the right file.
    /// </summary>
    public async Task StopVoiceIfRunningAsync()
    {
        if (_voice is not null)
        {
            await StopVoiceAsync();
        }
    }

    /// <summary>Called from the Talk button / Space key down.</summary>
    public void VoicePushToTalkDown()
    {
        _holdPending = true;
        if (_voice is null)
        {
            // Lazily start voice on first hold, then begin the hold once connected.
            _ = StartVoiceThenTalkAsync();
            return;
        }
        HoldPhase = HoldToTalkPhase.Held;
        _voice.PushToTalkDown();
    }

    private async Task StartVoiceThenTalkAsync()
    {
        await StartVoiceAsync();
        // Only begin recording if the user is still holding (didn't release while connecting).
        if (_holdPending && _voice is not null)
        {
            HoldPhase = HoldToTalkPhase.Held;
            _voice.PushToTalkDown();
        }
        else
        {
            HoldPhase = HoldToTalkPhase.Idle;
        }
    }

    /// <summary>Called from the Talk button / Space key up.</summary>
    public void VoicePushToTalkUp()
    {
        _holdPending = false;
        if (_voice is null)
        {
            HoldPhase = HoldToTalkPhase.Idle;
            return;
        }
        HoldPhase = SelectedVoiceMode == "hybrid" ? HoldToTalkPhase.Transcribing : HoldToTalkPhase.Idle;
        _voice.PushToTalkUp();
    }

    private void OnAssistantText(object? sender, string text)
        => PostToUi(() =>
        {
            var last = Messages.LastOrDefault();
            if (last is not { Role: ChatRole.Assistant, IsStreaming: true })
            {
                last = new ChatMessageViewModel(ChatRole.Assistant, "", isStreaming: true);
                Messages.Add(last);
            }
            last.Append(text);
        });

    private void OnResponseCompleted(object? sender, EventArgs e)
        => PostToUi(() =>
        {
            var streaming = Messages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.IsStreaming);
            if (streaming is not null)
            {
                streaming.IsStreaming = false;
                string time = DateTimeOffset.Now.ToLocalTime().ToString("HH:mm:ss");
                streaming.MetaText = SpeakReplies ? $"🔊 spoken privately · {time}" : time;
            }
        });

    private IReadOnlyList<RealtimeToolDefinition> BuildTools()
        => SaveNote is null
            ? Array.Empty<RealtimeToolDefinition>()
            : new[]
            {
                new RealtimeToolDefinition(
                    "write_notes",
                    "Rewrite the user's private meeting notes with the full updated markdown. Always include " +
                    "ALL previous notes plus any new content — the file is completely replaced on each call. " +
                    "Use plain markdown: headings, bullet lists, bold for emphasis. Keep it concise and useful.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            markdown = new
                            {
                                type = "string",
                                description = "The complete notes document in markdown. Must include all existing content plus any additions."
                            }
                        },
                        required = new[] { "markdown" }
                    })
            };

    private async Task<string> HandleToolCallAsync(RealtimeToolCall call)
    {
        if (call.Name != "write_notes" || SaveNote is null)
        {
            return "{\"ok\":false,\"error\":\"unknown tool\"}";
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
            string? markdown = doc.RootElement.TryGetProperty("markdown", out var m) ? m.GetString() : null;
            if (markdown is null)
            {
                return "{\"ok\":false,\"error\":\"missing markdown\"}";
            }

            await SaveNote(markdown);
            return "{\"ok\":true}";
        }
        catch (Exception ex)
        {
            AppLog.Warn("app", $"write_notes failed: {ex.Message}");
            return "{\"ok\":false,\"error\":\"write failed\"}";
        }
    }

    private void OnUserTextCommitted(object? sender, string text)
        => PostToUi(() => Messages.Add(new ChatMessageViewModel(ChatRole.User, text)));

    private void OnVoiceStateChanged(object? sender, RealtimeVoiceState state)
        => PostToUi(() =>
        {
            PillState = state switch
            {
                RealtimeVoiceState.Idle or RealtimeVoiceState.Stopped => AgentPillState.Off,
                RealtimeVoiceState.Connecting => AgentPillState.Connecting,
                RealtimeVoiceState.Ready or RealtimeVoiceState.Capturing => AgentPillState.Listening,
                RealtimeVoiceState.Thinking => AgentPillState.Thinking,
                RealtimeVoiceState.Speaking => AgentPillState.Speaking,
                RealtimeVoiceState.Faulted => AgentPillState.Error,
                _ => PillState
            };
            if (state is RealtimeVoiceState.Ready or RealtimeVoiceState.Speaking)
            {
                HoldPhase = HoldToTalkPhase.Idle;
            }
        });

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
