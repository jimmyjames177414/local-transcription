using LocalTranscriber.Shared;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LocalTranscriber.Audio;

/// <summary>
/// Shared WASAPI capture plumbing. Microphone and system-loopback services differ
/// only in how they create the underlying <see cref="IWaveIn"/>.
/// </summary>
public abstract class WasapiCaptureServiceBase : IAudioCaptureService
{
    private IWaveIn? _waveIn;
    private TaskCompletionSource? _stopped;

    public abstract AudioSourceType Source { get; }

    public event EventHandler<AudioChunk>? ChunkAvailable;

    public bool IsCapturing { get; private set; }

    public WaveFormat? CurrentFormat => _waveIn?.WaveFormat;

    protected abstract IWaveIn CreateWaveIn(AudioCaptureOptions options);

    protected static MMDevice? FindDevice(string? deviceId, DataFlow flow)
    {
        if (deviceId is null)
        {
            return null;
        }

        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDevice(deviceId);
    }

    public Task StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default)
    {
        if (IsCapturing)
        {
            throw new InvalidOperationException("Capture is already running.");
        }

        _waveIn = CreateWaveIn(options.Validate());
        _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
        IsCapturing = true;
        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }

        var format = _waveIn?.WaveFormat;
        if (format is null)
        {
            return;
        }

        byte[] data = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, data, 0, e.BytesRecorded);

        ChunkAvailable?.Invoke(this, new AudioChunk(
            Source,
            data,
            format.SampleRate,
            format.Channels,
            format.BitsPerSample,
            IsIeeeFloat: format.Encoding == WaveFormatEncoding.IeeeFloat,
            CapturedAt: DateTimeOffset.Now));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsCapturing = false;
        _stopped?.TrySetResult();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_waveIn is null)
        {
            return;
        }

        _waveIn.StopRecording();
        if (_stopped is not null)
        {
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;
        _waveIn.Dispose();
        _waveIn = null;
        IsCapturing = false;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        GC.SuppressFinalize(this);
    }
}

public sealed class MicrophoneCaptureService : WasapiCaptureServiceBase
{
    public override AudioSourceType Source => AudioSourceType.Microphone;

    protected override IWaveIn CreateWaveIn(AudioCaptureOptions options)
    {
        var device = FindDevice(options.DeviceId, DataFlow.Capture);
        return device is null ? new WasapiCapture() : new WasapiCapture(device);
    }
}

public sealed class SystemLoopbackCaptureService : WasapiCaptureServiceBase
{
    public override AudioSourceType Source => AudioSourceType.SystemAudio;

    protected override IWaveIn CreateWaveIn(AudioCaptureOptions options)
    {
        var device = FindDevice(options.DeviceId, DataFlow.Render);
        // Note: loopback produces no DataAvailable callbacks while the system is silent.
        return device is null ? new WasapiLoopbackCapture() : new WasapiLoopbackCapture(device);
    }
}
