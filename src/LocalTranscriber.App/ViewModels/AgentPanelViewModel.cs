using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using LocalTranscriber.Agent;
using LocalTranscriber.App.Mvvm;
using LocalTranscriber.Storage;

namespace LocalTranscriber.App.ViewModels;

public sealed class AgentPanelViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly Func<string?> _currentTranscriptPath;
    private readonly SynchronizationContext? _uiContext;

    private MeetingAgent? _agent;
    private CancellationTokenSource? _streamCts;
    private string _statusText = "Agent not running";
    private bool _agentEnabled;
    private string _selectedMode;
    private string _selectedProvider;
    private string _contextFolder;
    private string _outputFolder;
    private AgentSuggestionItem? _selectedSuggestion;

    public AgentPanelViewModel(ConfigService? configService = null, Func<string?>? currentTranscriptPath = null)
    {
        _configService = configService ?? new ConfigService();
        _currentTranscriptPath = currentTranscriptPath ?? (() => null);
        _uiContext = SynchronizationContext.Current;

        var config = _configService.Load();
        _agentEnabled = config.Agent.Enabled;
        _selectedMode = config.Agent.Mode;
        _selectedProvider = config.Agent.Provider;
        _contextFolder = config.Agent.ContextFolder;
        _outputFolder = config.Agent.AgentOutputFolder;

        StartCommand = new AsyncRelayCommand(StartAsync, () => _agent is null && AgentEnabled && SelectedMode != "Off");
        StopCommand = new AsyncRelayCommand(StopAsync, () => _agent is not null);
        DismissCommand = new AsyncRelayCommand(DismissAsync, () => SelectedSuggestion is not null);
        CopyCommand = new RelayCommand(Copy, () => SelectedSuggestion is not null);
        OpenContextFolderCommand = new RelayCommand(() => OpenFolder(ContextFolder));
        OpenOutputFolderCommand = new RelayCommand(() => OpenFolder(OutputFolder));
    }

    public ObservableCollection<AgentSuggestionItem> Suggestions { get; } = new();

    public string[] Modes { get; } = { "Off", "SilentObserver", "PrivateCoach" };
    public string[] Providers { get; } = { "fake" };

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand DismissCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand OpenContextFolderCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }

    public bool AgentEnabled
    {
        get => _agentEnabled;
        set
        {
            SetProperty(ref _agentEnabled, value);
            PersistConfig(c => c.Agent.Enabled = value);
            StartCommand.RaiseCanExecuteChanged();
        }
    }

    public string SelectedMode
    {
        get => _selectedMode;
        set
        {
            SetProperty(ref _selectedMode, value);
            PersistConfig(c => c.Agent.Mode = value);
            StartCommand.RaiseCanExecuteChanged();
        }
    }

    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            SetProperty(ref _selectedProvider, value);
            PersistConfig(c => c.Agent.Provider = value);
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

    public AgentSuggestionItem? SelectedSuggestion
    {
        get => _selectedSuggestion;
        set
        {
            SetProperty(ref _selectedSuggestion, value);
            DismissCommand.RaiseCanExecuteChanged();
            CopyCommand.RaiseCanExecuteChanged();
        }
    }

    private void PersistConfig(Action<Shared.AppConfig> mutate)
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

    private async Task StartAsync()
    {
        try
        {
            string? transcript = _currentTranscriptPath();
            if (transcript is null)
            {
                // No live session: fall back to the newest jsonl in the transcript folder.
                var config = _configService.Load();
                transcript = Directory.Exists(config.TranscriptFolder)
                    ? Directory.EnumerateFiles(config.TranscriptFolder, "*.jsonl")
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault()
                    : null;
            }

            if (transcript is null)
            {
                StatusText = "No transcript found. Start a recording session first.";
                return;
            }

            var appConfig = _configService.Load();
            var db = new SqliteDatabase(appConfig.DatabasePath);
            var sink = new CompositeAgentSuggestionSink(OutputFolder, new SqliteAgentSuggestionStore(db));
            _agent = new MeetingAgent(new FakeMeetingAgentProvider(), sink: sink);

            await _agent.StartAsync(new MeetingAgentOptions
            {
                TranscriptJsonlPath = transcript,
                ContextFolder = ContextFolder,
                AgentOutputFolder = OutputFolder,
                Mode = Enum.TryParse<AgentMode>(SelectedMode, out var mode) ? mode : AgentMode.SilentObserver,
                RollingWindowMinutes = appConfig.Agent.RollingWindowMinutes,
                SuggestionIntervalSeconds = Math.Max(2, appConfig.Agent.SuggestionIntervalSeconds),
                MaxTranscriptEventsPerPrompt = appConfig.Agent.MaxTranscriptEventsPerPrompt,
                MaxContextCharacters = appConfig.Agent.MaxContextCharacters,
                RequiredContextFiles = appConfig.Agent.RequiredContextFiles
            });

            StatusText = $"Agent running ({SelectedProvider}, {SelectedMode}) on {Path.GetFileName(transcript)}";
            _streamCts = new CancellationTokenSource();
            _ = ConsumeSuggestionsAsync(_streamCts.Token);
        }
        catch (Exception ex)
        {
            StatusText = $"Start failed: {ex.Message}";
            _agent = null;
        }

        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
    }

    private async Task ConsumeSuggestionsAsync(CancellationToken cancellationToken)
    {
        var agent = _agent;
        if (agent is null)
        {
            return;
        }

        try
        {
            await foreach (var s in agent.StreamSuggestionsAsync(cancellationToken))
            {
                PostToUi(() => Suggestions.Insert(0, new AgentSuggestionItem(s)));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task StopAsync()
    {
        try
        {
            _streamCts?.Cancel();
            _streamCts = null;
            if (_agent is not null)
            {
                var status = await _agent.GetStatusAsync();
                await _agent.DisposeAsync();
                StatusText = $"Agent stopped. Events: {status.EventsSeen}, suggestions: {status.SuggestionsEmitted}.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Stop failed: {ex.Message}";
        }
        finally
        {
            _agent = null;
            StartCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task DismissAsync()
    {
        if (SelectedSuggestion is null)
        {
            return;
        }

        try
        {
            var config = _configService.Load();
            var store = new SqliteAgentSuggestionStore(new SqliteDatabase(config.DatabasePath));
            await store.DismissAsync(SelectedSuggestion.Id);
            Suggestions.Remove(SelectedSuggestion);
            SelectedSuggestion = null;
        }
        catch (Exception ex)
        {
            StatusText = $"Dismiss failed: {ex.Message}";
        }
    }

    private void Copy()
    {
        if (SelectedSuggestion is not null)
        {
            Clipboard.SetText($"[{SelectedSuggestion.Priority}] {SelectedSuggestion.Title}: {SelectedSuggestion.Message}");
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
        _streamCts?.Cancel();
        if (_agent is not null)
        {
            await _agent.DisposeAsync();
            _agent = null;
        }
    }
}

public sealed class AgentSuggestionItem
{
    public AgentSuggestionItem(AgentSuggestion s)
    {
        Id = s.Id;
        Time = s.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
        Type = s.Type.ToString();
        Priority = s.Priority.ToString();
        Title = s.Title;
        Message = s.Message;
    }

    public string Id { get; }
    public string Time { get; }
    public string Type { get; }
    public string Priority { get; }
    public string Title { get; }
    public string Message { get; }
}
