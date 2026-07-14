using LocalTranscriber.Audio;

namespace LocalTranscriber.Voice;

/// <summary>
/// Captures a single held microphone turn to a WAV file for local (hybrid) transcription.
/// Microphone only — meeting/system audio is never captured.
/// </summary>
public interface IPushToTalkRecorder : IAsyncDisposable
{
    bool IsAvailable { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops capture and returns the recorded WAV path, or null if nothing was captured.</summary>
    Task<string?> StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default recorder: a dedicated <see cref="MicrophoneCaptureService"/> writing to a temp WAV,
/// which <c>WhisperCppTranscriptionService</c> resamples to 16 kHz on read.
/// </summary>
public sealed class MicrophonePushToTalkRecorder : IPushToTalkRecorder
{
    private readonly string? _inputDeviceId;
    private readonly string _outputFolder;

    private MicrophoneCaptureService? _capture;
    private WavDebugWriter? _writer;
    private string? _path;

    public MicrophonePushToTalkRecorder(string? inputDeviceId, string outputFolder)
    {
        _inputDeviceId = inputDeviceId;
        _outputFolder = outputFolder;
    }

    public bool IsAvailable =>
        new MicrophoneCaptureService().IsAvailable(new AudioCaptureOptions { DeviceId = _inputDeviceId });

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputFolder);
        _path = Path.Combine(_outputFolder, $"ptt-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.wav");
        _writer = new WavDebugWriter(_path);
        _capture = new MicrophoneCaptureService();
        _capture.ChunkAvailable += OnChunk;
        await _capture.StartAsync(new AudioCaptureOptions { DeviceId = _inputDeviceId }, cancellationToken).ConfigureAwait(false);
    }

    private void OnChunk(object? sender, AudioChunk chunk) => _writer?.Write(chunk);

    public async Task<string?> StopAsync(CancellationToken cancellationToken = default)
    {
        if (_capture is not null)
        {
            _capture.ChunkAvailable -= OnChunk;
            await _capture.StopAsync(cancellationToken).ConfigureAwait(false);
            await _capture.DisposeAsync().ConfigureAwait(false);
            _capture = null;
        }

        long bytes = _writer?.BytesWritten ?? 0;
        _writer?.Dispose();
        _writer = null;

        return bytes > 0 ? _path : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_capture is not null)
        {
            _capture.ChunkAvailable -= OnChunk;
            await _capture.DisposeAsync().ConfigureAwait(false);
            _capture = null;
        }
        _writer?.Dispose();
        _writer = null;
    }
}
