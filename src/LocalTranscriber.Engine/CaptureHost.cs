using LocalTranscriber.Audio;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Engine;

/// <summary>
/// Owns the mic + system capture devices for a session: starts them, raises each audio chunk, and
/// runs the stall watchdog that hot-reconnects a device that stops delivering audio. Extracted from
/// <see cref="RealTranscriptionEngine"/> so the engine keeps only orchestration and the session
/// state machine.
///
/// Behavior is preserved from the inline version: a device is reconnected only when the engine still
/// reports the session active (the <c>isActive</c> predicate, formerly a state check under the
/// engine's control lock) — here serialized by this host's own device lock so a reconnect never
/// races <see cref="StopAsync"/>.
/// </summary>
internal sealed class CaptureHost : IAsyncDisposable
{
    private readonly Func<IAudioCaptureService> _micFactory;
    private readonly Func<IAudioCaptureService> _systemFactory;
    private readonly Action<string> _addWarning;
    private readonly Func<bool> _isActive;
    private readonly TimeSpan _staleThreshold;
    private readonly TimeSpan _pollInterval;

    private readonly SemaphoreSlim _deviceLock = new(1, 1);
    private IAudioCaptureService? _mic;
    private IAudioCaptureService? _system;
    private long _lastMicChunkMs;
    private long _lastSystemChunkMs;
    private Task? _watchdog;
    private CancellationTokenSource? _cts;
    private volatile bool _running;

    /// <summary>Raised on the capture thread for each chunk; handlers must not block.</summary>
    public event Action<AudioChunk>? MicChunk;
    public event Action<AudioChunk>? SystemChunk;

    public CaptureHost(
        Func<IAudioCaptureService> micFactory,
        Func<IAudioCaptureService> systemFactory,
        Action<string> addWarning,
        Func<bool> isActive,
        TimeSpan staleThreshold)
    {
        _micFactory = micFactory;
        _systemFactory = systemFactory;
        _addWarning = addWarning;
        _isActive = isActive;
        _staleThreshold = staleThreshold;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, staleThreshold.TotalSeconds / 3));
    }

    /// <summary>
    /// Starts the requested devices and the watchdog. Individual unavailable devices are warned and
    /// skipped; throws when neither device is available (the caller cleans up on throw).
    /// </summary>
    public async Task StartAsync(bool enableMic, bool enableSystem, CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _lastMicChunkMs, Environment.TickCount64);
        Interlocked.Exchange(ref _lastSystemChunkMs, Environment.TickCount64);

        var captureOptions = new AudioCaptureOptions();

        if (enableMic)
        {
            var mic = _micFactory();
            if (mic.IsAvailable(captureOptions))
            {
                _mic = mic;
                _mic.ChunkAvailable += OnMicChunk;
                await _mic.StartAsync(captureOptions, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await mic.DisposeAsync().ConfigureAwait(false);
                _addWarning("Microphone unavailable (no default recording device); skipping mic capture.");
            }
        }

        if (enableSystem)
        {
            var system = _systemFactory();
            if (system.IsAvailable(captureOptions))
            {
                _system = system;
                _system.ChunkAvailable += OnSystemChunk;
                await _system.StartAsync(captureOptions, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await system.DisposeAsync().ConfigureAwait(false);
                _addWarning("System audio unavailable (no default playback device); skipping system capture.");
            }
        }

        if (_mic is null && _system is null)
        {
            throw new InvalidOperationException(
                "No audio devices available. Connect a microphone or a playback device and try again.");
        }

        _running = true;
        _watchdog = Task.Run(() => WatchdogAsync(_cts.Token), CancellationToken.None);
    }

    private void OnMicChunk(object? sender, AudioChunk chunk)
    {
        Interlocked.Exchange(ref _lastMicChunkMs, Environment.TickCount64);
        MicChunk?.Invoke(chunk);
    }

    private void OnSystemChunk(object? sender, AudioChunk chunk)
    {
        Interlocked.Exchange(ref _lastSystemChunkMs, Environment.TickCount64);
        SystemChunk?.Invoke(chunk);
    }

    private async Task WatchdogAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_staleThreshold, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                long now = Environment.TickCount64;
                long staleMs = (long)_staleThreshold.TotalMilliseconds;
                if (_mic is not null && now - Interlocked.Read(ref _lastMicChunkMs) > staleMs)
                    await TryRestartAsync(AudioSourceType.Microphone, cancellationToken).ConfigureAwait(false);
                if (_system is not null && now - Interlocked.Read(ref _lastSystemChunkMs) > staleMs)
                    await TryRestartAsync(AudioSourceType.SystemAudio, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TryRestartAsync(AudioSourceType source, CancellationToken cancellationToken)
    {
        _addWarning($"{source} capture stalled — no audio for {(int)_staleThreshold.TotalSeconds}s; reconnecting.");

        // WaitAsync throws OCE if cancelled before acquired (lock NOT held in that case).
        bool acquired = await _deviceLock.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (!acquired)
            return;
        try
        {
            // Don't reconnect once stopped or while the engine reports the session inactive
            // (e.g. stopping/paused) — mirrors the original state check.
            if (!_running || !_isActive())
                return;

            var captureOptions = new AudioCaptureOptions();

            if (source == AudioSourceType.Microphone)
            {
                if (_mic is not null)
                {
                    _mic.ChunkAvailable -= OnMicChunk;
                    await _mic.DisposeAsync().ConfigureAwait(false);
                    _mic = null;
                }
                Interlocked.Exchange(ref _lastMicChunkMs, Environment.TickCount64);
                try
                {
                    var capture = _micFactory();
                    if (capture.IsAvailable(captureOptions))
                    {
                        await capture.StartAsync(captureOptions, cancellationToken).ConfigureAwait(false);
                        capture.ChunkAvailable += OnMicChunk;
                        _mic = capture;
                        AppLog.Info("engine", "Microphone capture reconnected.");
                    }
                    else
                    {
                        await capture.DisposeAsync().ConfigureAwait(false);
                        _addWarning("Microphone unavailable after reconnect; mic capture suspended.");
                    }
                }
                catch (Exception ex)
                {
                    _addWarning($"Microphone reconnect failed: {ex.Message}");
                }
            }
            else
            {
                if (_system is not null)
                {
                    _system.ChunkAvailable -= OnSystemChunk;
                    await _system.DisposeAsync().ConfigureAwait(false);
                    _system = null;
                }
                Interlocked.Exchange(ref _lastSystemChunkMs, Environment.TickCount64);
                try
                {
                    var capture = _systemFactory();
                    if (capture.IsAvailable(captureOptions))
                    {
                        await capture.StartAsync(captureOptions, cancellationToken).ConfigureAwait(false);
                        capture.ChunkAvailable += OnSystemChunk;
                        _system = capture;
                        AppLog.Info("engine", "System audio capture reconnected.");
                    }
                    else
                    {
                        await capture.DisposeAsync().ConfigureAwait(false);
                        _addWarning("System audio unavailable after reconnect; system capture suspended.");
                    }
                }
                catch (Exception ex)
                {
                    _addWarning($"System audio reconnect failed: {ex.Message}");
                }
            }
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    /// <summary>Stops the watchdog and disposes both devices. Idempotent.</summary>
    public async Task StopAsync()
    {
        _running = false;
        _cts?.Cancel();
        if (_watchdog is not null)
        {
            try { await _watchdog.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            _watchdog = null;
        }

        await _deviceLock.WaitAsync().ConfigureAwait(false);
        try
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
        finally
        {
            _deviceLock.Release();
        }

        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _deviceLock.Dispose();
    }
}
