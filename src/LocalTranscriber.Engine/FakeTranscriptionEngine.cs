using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Engine;

/// <summary>
/// Emits fake transcript events on a timer so the UI, CLI, and MCP server can
/// integrate against a stable engine API before real audio/AI exists.
/// </summary>
public sealed class FakeTranscriptionEngine : ITranscriptionEngine, IAsyncDisposable
{
    private static readonly string[] FakeLines =
    {
        "This is a fake microphone transcript line.",
        "This is a fake system audio transcript line.",
        "Another fake remote speaker line.",
        "Can everyone hear me?",
        "Yes, I can hear you.",
        "Let's move deployment to Friday.",
        "I need to check the test results."
    };

    private readonly SemaphoreSlim _control = new(1, 1);
    private readonly Channel<TranscriptEvent> _events = Channel.CreateUnbounded<TranscriptEvent>();
    private readonly ISessionStore? _sessionStore;
    private readonly ITranscriptEventStore? _eventStore;

    public FakeTranscriptionEngine(ISessionStore? sessionStore = null, ITranscriptEventStore? eventStore = null)
    {
        _sessionStore = sessionStore;
        _eventStore = eventStore;
    }

    private TranscriptionSessionOptions? _options;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ITranscriptWriter? _writer;
    private volatile bool _paused;

    private TranscriptionSessionState _state = TranscriptionSessionState.NotStarted;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastEventAt;
    private long _eventCount;
    private string? _error;

    public async Task StartAsync(TranscriptionSessionOptions options, CancellationToken cancellationToken = default)
    {
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is TranscriptionSessionState.Recording or TranscriptionSessionState.Paused or TranscriptionSessionState.Starting)
            {
                throw new InvalidOperationException($"A session is already active (state: {_state}).");
            }

            _state = TranscriptionSessionState.Starting;
            _options = options;
            _error = null;
            _eventCount = 0;
            _paused = false;

            _writer = new CompositeTranscriptWriter(
                new PlainTextTranscriptWriter(options.OutputTextPath),
                new JsonlTranscriptWriter(options.OutputJsonlPath));

            _cts = new CancellationTokenSource();
            _startedAt = DateTimeOffset.Now;

            if (_sessionStore is not null)
            {
                await _sessionStore.CreateAsync(new SessionRecord(
                    options.SessionId, _startedAt.Value, null,
                    options.OutputTextPath, options.OutputJsonlPath, "recording"), cancellationToken).ConfigureAwait(false);
            }

            _loop = Task.Run(() => RunLoopAsync(options, _cts.Token), CancellationToken.None);
            _state = TranscriptionSessionState.Recording;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _state = TranscriptionSessionState.Faulted;
            _error = ex.Message;
            throw;
        }
        finally
        {
            _control.Release();
        }
    }

    private async Task RunLoopAsync(TranscriptionSessionOptions options, CancellationToken cancellationToken)
    {
        int lineIndex = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(options.FakeEventIntervalMs, cancellationToken).ConfigureAwait(false);
                if (_paused)
                {
                    continue;
                }

                var e = CreateFakeEvent(options, lineIndex);
                lineIndex++;

                var writer = _writer;
                if (writer is not null)
                {
                    await writer.WriteAsync(e, cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (_eventStore is not null)
                {
                    await _eventStore.InsertAsync(e, cancellationToken).ConfigureAwait(false);
                }

                _lastEventAt = e.Timestamp;
                Interlocked.Increment(ref _eventCount);
                _events.Writer.TryWrite(e);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _state = TranscriptionSessionState.Faulted;
            _error = ex.Message;
        }
    }

    private static TranscriptEvent CreateFakeEvent(TranscriptionSessionOptions options, int index)
    {
        // Alternate mic ("Me") and two fake system-audio speakers.
        int rotation = index % 3;
        SpeakerLabel speaker;
        AudioSourceType source;
        if (rotation == 0 && options.EnableMicrophone)
        {
            speaker = new SpeakerLabel("mic", options.MicrophoneSpeakerName, IsKnown: true);
            source = AudioSourceType.Microphone;
        }
        else
        {
            int n = rotation == 0 ? 1 : rotation;
            speaker = new SpeakerLabel($"speaker_{n}", $"Speaker {n}", IsKnown: false);
            source = AudioSourceType.SystemAudio;
        }

        return new TranscriptEvent(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: options.SessionId,
            Timestamp: DateTimeOffset.Now,
            Speaker: speaker,
            Source: source,
            Text: FakeLines[index % FakeLines.Length],
            Confidence: 0.9,
            StartMs: index * (long)options.FakeEventIntervalMs,
            EndMs: (index + 1) * (long)options.FakeEventIntervalMs);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is TranscriptionSessionState.NotStarted or TranscriptionSessionState.Stopped)
            {
                _state = TranscriptionSessionState.Stopped;
                return; // idempotent
            }

            _state = TranscriptionSessionState.Stopping;
            _cts?.Cancel();
            if (_loop is not null)
            {
                try
                {
                    await _loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (_writer is not null)
            {
                await _writer.DisposeAsync().ConfigureAwait(false);
                _writer = null;
            }

            _cts?.Dispose();
            _cts = null;
            _loop = null;
            if (_state != TranscriptionSessionState.Faulted)
            {
                _state = TranscriptionSessionState.Stopped;
            }

            if (_sessionStore is not null && _options is not null)
            {
                await _sessionStore.EndAsync(_options.SessionId, DateTimeOffset.Now,
                    _state == TranscriptionSessionState.Faulted ? "faulted" : "stopped", cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _control.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == TranscriptionSessionState.Recording)
            {
                _paused = true;
                _state = TranscriptionSessionState.Paused;
            }
        }
        finally
        {
            _control.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == TranscriptionSessionState.Paused)
            {
                _paused = false;
                _state = TranscriptionSessionState.Recording;
            }
        }
        finally
        {
            _control.Release();
        }
    }

    public Task<TranscriptionSessionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TranscriptionSessionStatus
        {
            State = _state,
            SessionId = _options?.SessionId,
            OutputTextPath = _options?.OutputTextPath,
            OutputJsonlPath = _options?.OutputJsonlPath,
            StartedAt = _startedAt,
            LastEventAt = _lastEventAt,
            EventCount = Interlocked.Read(ref _eventCount),
            Error = _error
        });
    }

    public async IAsyncEnumerable<TranscriptEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_events.Reader.TryRead(out var e))
            {
                yield return e;
            }
        }
    }

    public Task<IReadOnlyList<SessionSpeakerInfo>> ListSessionSpeakersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SessionSpeakerInfo>>(Array.Empty<SessionSpeakerInfo>());

    public Task<bool> NameSessionSpeakerAsync(string sessionLabel, string newName, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task UpdateSessionTitleAsync(string sessionId, string? title, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
        _control.Dispose();
    }
}
