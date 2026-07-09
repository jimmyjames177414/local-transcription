using System.IO;
using System.Text;
using LocalTranscriber.App.Mvvm;
using LocalTranscriber.Engine;
using LocalTranscriber.Engine.Ipc;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly ITranscriptionEngine _engine;
    private readonly AppConfig _config;
    private readonly EngineIpcServer? _ipcServer;
    private readonly SynchronizationContext? _uiContext;
    private readonly StringBuilder _preview = new();
    private CancellationTokenSource? _streamCts;

    private string _statusText = "Not recording";
    private string _sessionId = "";
    private string _outputFolder;
    private string _previewText = "";
    private string _errorText = "";
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
        ClearPreviewCommand = new RelayCommand(ClearPreview);
    }

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand PauseCommand { get; }
    public AsyncRelayCommand ResumeCommand { get; }
    public RelayCommand ClearPreviewCommand { get; }

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

    public string OutputFolder
    {
        get => _outputFolder;
        set => SetProperty(ref _outputFolder, value);
    }

    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    private void SetState(TranscriptionSessionState state)
    {
        _state = state;
        StatusText = state switch
        {
            TranscriptionSessionState.NotStarted => "Not recording",
            TranscriptionSessionState.Starting => "Starting...",
            TranscriptionSessionState.Recording => "Recording (fake)",
            TranscriptionSessionState.Paused => "Paused",
            TranscriptionSessionState.Stopping => "Stopping...",
            TranscriptionSessionState.Stopped => "Stopped",
            TranscriptionSessionState.Faulted => "Faulted",
            _ => state.ToString()
        };
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
    }

    private async Task StartAsync()
    {
        try
        {
            ErrorText = "";
            string folder = string.IsNullOrWhiteSpace(OutputFolder) ? "output/transcripts" : OutputFolder;
            var options = EngineFactory.CreateSessionOptions(_config, folder);

            await _engine.StartAsync(options);
            SessionId = options.SessionId;
            SetState(TranscriptionSessionState.Recording);

            _streamCts = new CancellationTokenSource();
            _ = ConsumeEventsAsync(_streamCts.Token);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            SetState(TranscriptionSessionState.Faulted);
        }
    }

    private async Task ConsumeEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var e in _engine.StreamEventsAsync(cancellationToken))
            {
                string line = TranscriptFormatting.FormatLine(e);
                PostToUi(() =>
                {
                    _preview.AppendLine(line);
                    PreviewText = _preview.ToString();
                });
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

    private void ClearPreview()
    {
        _preview.Clear();
        PreviewText = "";
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
