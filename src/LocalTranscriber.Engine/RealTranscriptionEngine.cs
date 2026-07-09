using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LocalTranscriber.AI;
using LocalTranscriber.Audio;
using LocalTranscriber.Shared;
using LocalTranscriber.Speakers;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Engine;

/// <summary>
/// The real pipeline:
///   mic window    -> whisper -> "Me" events
///   system window -> diarize -> whisper -> align -> speaker memory -> labeled events
/// Mic and system audio stay separate end to end; they are never mixed.
/// </summary>
public sealed class RealTranscriptionEngine : ITranscriptionEngine, IAsyncDisposable
{
    private readonly Func<IAudioCaptureService> _micFactory;
    private readonly Func<IAudioCaptureService> _systemFactory;
    private readonly ILocalTranscriptionService _transcription;
    private readonly ISpeakerDiarizationService? _diarization;
    private readonly ISpeakerEmbeddingService? _embedding;
    private readonly ISpeakerRecognitionService? _recognition;
    private readonly ISessionStore? _sessionStore;
    private readonly ITranscriptEventStore? _eventStore;

    private readonly SemaphoreSlim _control = new(1, 1);
    private readonly Channel<TranscriptEvent> _events = Channel.CreateUnbounded<TranscriptEvent>();
    private readonly List<string> _warnings = new();

    private TranscriptionSessionOptions? _options;
    private ITranscriptWriter? _writer;
    private IAudioCaptureService? _mic;
    private IAudioCaptureService? _system;
    private Channel<AudioWindow>? _windowQueue;
    private Task? _processor;
    private CancellationTokenSource? _cts;
    private AudioWindowBuffer? _micBuffer;
    private AudioWindowBuffer? _systemBuffer;
    private SessionSpeakerRegistry? _registry;
    private string? _tempDir;
    private int _windowCounter;
    private string _lastEmittedText = "";

    private volatile bool _paused;
    private TranscriptionSessionState _state = TranscriptionSessionState.NotStarted;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastEventAt;
    private long _eventCount;
    private string? _error;

    public RealTranscriptionEngine(
        ILocalTranscriptionService transcription,
        Func<IAudioCaptureService>? micFactory = null,
        Func<IAudioCaptureService>? systemFactory = null,
        ISpeakerDiarizationService? diarization = null,
        ISpeakerEmbeddingService? embedding = null,
        ISpeakerRecognitionService? recognition = null,
        ISessionStore? sessionStore = null,
        ITranscriptEventStore? eventStore = null)
    {
        _transcription = transcription;
        _micFactory = micFactory ?? (() => new MicrophoneCaptureService());
        _systemFactory = systemFactory ?? (() => new SystemLoopbackCaptureService());
        _diarization = diarization;
        _embedding = embedding;
        _recognition = recognition;
        _sessionStore = sessionStore;
        _eventStore = eventStore;
    }

    public async Task StartAsync(TranscriptionSessionOptions options, CancellationToken cancellationToken = default)
    {
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is TranscriptionSessionState.Recording or TranscriptionSessionState.Paused or TranscriptionSessionState.Starting)
            {
                throw new InvalidOperationException($"A session is already active (state: {_state}).");
            }

            if (!options.EnableMicrophone && !options.EnableSystemAudio)
            {
                throw new InvalidOperationException("Enable at least one audio source (microphone or system audio).");
            }

            if (options.WhisperModelPath is null || !File.Exists(options.WhisperModelPath))
            {
                throw new WhisperModelNotFoundException(options.WhisperModelPath ?? "(not configured)");
            }

            _state = TranscriptionSessionState.Starting;
            _options = options;
            _error = null;
            _eventCount = 0;
            _paused = false;
            _warnings.Clear();
            _lastEmittedText = "";
            _windowCounter = 0;

            _writer = new CompositeTranscriptWriter(
                new PlainTextTranscriptWriter(options.OutputTextPath),
                new JsonlTranscriptWriter(options.OutputJsonlPath));

            _registry = new SessionSpeakerRegistry();
            _tempDir = Path.Combine(Path.GetTempPath(), "localtranscriber", options.SessionId);
            Directory.CreateDirectory(_tempDir);

            _micBuffer = new AudioWindowBuffer(options.ChunkSeconds, options.OverlapMs);
            _systemBuffer = new AudioWindowBuffer(options.ChunkSeconds, options.OverlapMs);
            _windowQueue = Channel.CreateUnbounded<AudioWindow>();
            _cts = new CancellationTokenSource();
            _startedAt = DateTimeOffset.Now;

            if (_sessionStore is not null)
            {
                await _sessionStore.CreateAsync(new SessionRecord(
                    options.SessionId, _startedAt.Value, null,
                    options.OutputTextPath, options.OutputJsonlPath, "recording"), cancellationToken).ConfigureAwait(false);
            }

            try
            {
                if (options.EnableMicrophone)
                {
                    _mic = _micFactory();
                    _mic.ChunkAvailable += OnMicChunk;
                    await _mic.StartAsync(new AudioCaptureOptions(), cancellationToken).ConfigureAwait(false);
                }

                if (options.EnableSystemAudio)
                {
                    _system = _systemFactory();
                    _system.ChunkAvailable += OnSystemChunk;
                    await _system.StartAsync(new AudioCaptureOptions(), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                await CleanupCaptureAsync().ConfigureAwait(false);
                _state = TranscriptionSessionState.Faulted;
                _error = $"Audio capture failed to start: {ex.Message}";
                throw new InvalidOperationException(_error, ex);
            }

            _processor = Task.Run(() => ProcessWindowsAsync(_cts.Token), CancellationToken.None);
            _state = TranscriptionSessionState.Recording;
        }
        finally
        {
            _control.Release();
        }
    }

    private void OnMicChunk(object? sender, AudioChunk chunk) => EnqueueWindow(_micBuffer?.Add(chunk));

    private void OnSystemChunk(object? sender, AudioChunk chunk) => EnqueueWindow(_systemBuffer?.Add(chunk));

    private void EnqueueWindow(AudioWindow? window)
    {
        if (window is null || _paused)
        {
            return;
        }

        _windowQueue?.Writer.TryWrite(window);
    }

    private async Task ProcessWindowsAsync(CancellationToken cancellationToken)
    {
        if (_windowQueue is null)
        {
            return;
        }

        try
        {
            await foreach (var window in _windowQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await ProcessWindowAsync(window, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AddWarning($"Window processing failed ({window.Source}): {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _state = TranscriptionSessionState.Faulted;
            _error = ex.Message;
        }
    }

    private async Task ProcessWindowAsync(AudioWindow window, CancellationToken cancellationToken)
    {
        if (_options is null)
        {
            return;
        }

        if (AudioWindowBuffer.Peak(window) < 0.015)
        {
            return; // silence — transcribing it only invites whisper hallucinations
        }

        string wavPath = WriteTempWav(window);
        try
        {
            if (window.Source == AudioSourceType.Microphone)
            {
                await ProcessMicWindowAsync(window, wavPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ProcessSystemWindowAsync(window, wavPath, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try { File.Delete(wavPath); } catch { }
        }
    }

    private async Task ProcessMicWindowAsync(AudioWindow window, string wavPath, CancellationToken cancellationToken)
    {
        var result = await TranscribeAsync(wavPath, cancellationToken).ConfigureAwait(false);
        var speaker = new SpeakerLabel("mic", _options!.MicrophoneSpeakerName, IsKnown: true);
        foreach (var segment in result.Segments)
        {
            await EmitAsync(window, segment, speaker, AudioSourceType.Microphone, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessSystemWindowAsync(AudioWindow window, string wavPath, CancellationToken cancellationToken)
    {
        IReadOnlyList<SpeakerSegment> diarized = Array.Empty<SpeakerSegment>();
        if (_diarization is not null && _options!.SpeakerModelDir is not null)
        {
            try
            {
                diarized = await _diarization.DiarizeAsync(new SpeakerDiarizationRequest
                {
                    AudioPath = wavPath,
                    Models = new SpeakerModelConfig { ModelDir = _options.SpeakerModelDir }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddWarning($"Diarization failed, falling back to single speaker: {ex.Message}");
            }
        }

        var result = await TranscribeAsync(wavPath, cancellationToken).ConfigureAwait(false);
        if (result.Segments.Count == 0)
        {
            return;
        }

        // Resolve each diarized cluster to a speaker label once per window.
        var clusterLabels = new Dictionary<string, SpeakerLabel>();
        foreach (var cluster in diarized.GroupBy(s => s.TemporarySpeakerId))
        {
            var longest = cluster.OrderByDescending(s => s.EndMs - s.StartMs).First();
            clusterLabels[cluster.Key] = await ResolveSpeakerAsync(wavPath, longest, cancellationToken).ConfigureAwait(false);
        }

        var defaultLabel = clusterLabels.Count == 1
            ? clusterLabels.Values.First()
            : new SpeakerLabel("speaker_unknown", _registry?.FallbackLabel() ?? "Speaker 1", IsKnown: false);

        foreach (var segment in result.Segments)
        {
            var speaker = AssignSpeaker(segment, diarized, clusterLabels) ?? defaultLabel;
            await EmitAsync(window, segment, speaker, AudioSourceType.SystemAudio, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Assigns the diarized cluster with the largest time overlap with the whisper segment.</summary>
    private static SpeakerLabel? AssignSpeaker(
        TranscribedSegment segment,
        IReadOnlyList<SpeakerSegment> diarized,
        IReadOnlyDictionary<string, SpeakerLabel> clusterLabels)
    {
        string? bestCluster = null;
        long bestOverlap = 0;
        foreach (var d in diarized)
        {
            long overlap = Math.Min(segment.EndMs, d.EndMs) - Math.Max(segment.StartMs, d.StartMs);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestCluster = d.TemporarySpeakerId;
            }
        }

        return bestCluster is not null && clusterLabels.TryGetValue(bestCluster, out var label) ? label : null;
    }

    private async Task<SpeakerLabel> ResolveSpeakerAsync(string wavPath, SpeakerSegment segment, CancellationToken cancellationToken)
    {
        if (_embedding is null || _options?.SpeakerModelDir is null || segment.EndMs - segment.StartMs < 700)
        {
            return new SpeakerLabel("speaker_unknown", _registry?.FallbackLabel() ?? "Speaker 1", IsKnown: false);
        }

        try
        {
            var embedding = await _embedding.ExtractEmbeddingAsync(new SpeakerEmbeddingRequest
            {
                AudioPath = wavPath,
                Models = new SpeakerModelConfig { ModelDir = _options.SpeakerModelDir },
                StartMs = segment.StartMs,
                EndMs = segment.EndMs
            }, cancellationToken).ConfigureAwait(false);

            if (_recognition is not null)
            {
                var match = await _recognition.MatchAsync(embedding, cancellationToken).ConfigureAwait(false);
                if (match is not null)
                {
                    // Confidence below the writer's threshold renders as "possibly Name".
                    return new SpeakerLabel(match.SpeakerId, match.DisplayName, IsKnown: true, Confidence: match.Similarity);
                }
            }

            string label = _registry?.ResolveLabel(embedding) ?? "Speaker 1";
            return new SpeakerLabel($"session_{label.Replace(' ', '_').ToLowerInvariant()}", label, IsKnown: false);
        }
        catch (Exception ex)
        {
            AddWarning($"Speaker embedding failed: {ex.Message}");
            return new SpeakerLabel("speaker_unknown", _registry?.FallbackLabel() ?? "Speaker 1", IsKnown: false);
        }
    }

    private async Task<TranscriptionResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
    {
        return await _transcription.TranscribeAsync(new TranscriptionRequest
        {
            AudioPath = wavPath,
            ModelPath = _options!.WhisperModelPath!,
            Language = _options.Language,
            Timeout = TimeSpan.FromSeconds(Math.Max(60, _options.ChunkSeconds * 6))
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task EmitAsync(AudioWindow window, TranscribedSegment segment, SpeakerLabel speaker, AudioSourceType source, CancellationToken cancellationToken)
    {
        string text = segment.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        // Overlap dedup: drop a segment identical to the last emitted text.
        if (string.Equals(text, _lastEmittedText, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _lastEmittedText = text;

        var e = new TranscriptEvent(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: _options!.SessionId,
            Timestamp: window.StartsAt.AddMilliseconds(segment.StartMs),
            Speaker: speaker,
            Source: source,
            Text: text,
            Confidence: segment.Confidence,
            StartMs: segment.StartMs,
            EndMs: segment.EndMs);

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

    private string WriteTempWav(AudioWindow window)
    {
        string path = Path.Combine(_tempDir!, $"{window.Source}-{Interlocked.Increment(ref _windowCounter)}.wav");
        using var writer = new WavDebugWriter(path);
        writer.Write(new AudioChunk(window.Source, window.Data, window.SampleRate, window.Channels, window.BitsPerSample, window.IsIeeeFloat, window.StartsAt));
        return path;
    }

    private void AddWarning(string warning)
    {
        lock (_warnings)
        {
            _warnings.Add($"{DateTime.Now:HH:mm:ss} {warning}");
            if (_warnings.Count > 20)
            {
                _warnings.RemoveAt(0);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is TranscriptionSessionState.NotStarted or TranscriptionSessionState.Stopped)
            {
                _state = TranscriptionSessionState.Stopped;
                return;
            }

            _state = TranscriptionSessionState.Stopping;

            await CleanupCaptureAsync().ConfigureAwait(false);

            // Process any remaining buffered audio, then drain the queue.
            EnqueueFinalWindows();
            _windowQueue?.Writer.TryComplete();
            if (_processor is not null)
            {
                try
                {
                    await _processor.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _cts?.Cancel();
                    AddWarning("Stopped before all buffered audio finished transcribing.");
                }
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _processor = null;

            if (_writer is not null)
            {
                await _writer.DisposeAsync().ConfigureAwait(false);
                _writer = null;
            }

            if (_tempDir is not null)
            {
                try { Directory.Delete(_tempDir, recursive: true); } catch { }
                _tempDir = null;
            }

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

    private void EnqueueFinalWindows()
    {
        var micTail = _micBuffer?.Flush(AudioSourceType.Microphone);
        if (micTail is not null)
        {
            _windowQueue?.Writer.TryWrite(micTail);
        }

        var systemTail = _systemBuffer?.Flush(AudioSourceType.SystemAudio);
        if (systemTail is not null)
        {
            _windowQueue?.Writer.TryWrite(systemTail);
        }
    }

    private async Task CleanupCaptureAsync()
    {
        if (_mic is not null)
        {
            _mic.ChunkAvailable -= OnMicChunk;
            await _mic.DisposeAsync().ConfigureAwait(false);
            _mic = null;
        }

        if (_system is not null)
        {
            _system.ChunkAvailable -= OnSystemChunk;
            await _system.DisposeAsync().ConfigureAwait(false);
            _system = null;
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
        string? warnings;
        lock (_warnings)
        {
            warnings = _warnings.Count > 0 ? string.Join(" | ", _warnings) : null;
        }

        return Task.FromResult(new TranscriptionSessionStatus
        {
            State = _state,
            SessionId = _options?.SessionId,
            OutputTextPath = _options?.OutputTextPath,
            OutputJsonlPath = _options?.OutputJsonlPath,
            StartedAt = _startedAt,
            LastEventAt = _lastEventAt,
            EventCount = Interlocked.Read(ref _eventCount),
            Error = _error ?? warnings
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
        _control.Dispose();
    }
}
