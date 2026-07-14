using System.Runtime.InteropServices;
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

    public virtual bool IsAvailable(AudioCaptureOptions options) => true;

    /// <summary>
    /// True when a usable endpoint exists for this source. When <paramref name="deviceId"/> is
    /// null we require a default endpoint for <paramref name="flow"/>; otherwise the named device.
    /// Returns false instead of throwing when the audio subsystem reports nothing available.
    /// </summary>
    protected static bool HasEndpoint(string? deviceId, DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (deviceId is null)
            {
                using var _ = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                return true;
            }

            using var device = enumerator.GetDevice(deviceId);
            return device is not null;
        }
        catch (COMException)
        {
            // ERROR_NOT_FOUND (0x80070490) and friends: no matching endpoint present.
            return false;
        }
    }

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

    public override bool IsAvailable(AudioCaptureOptions options) =>
        HasEndpoint(options.DeviceId, DataFlow.Capture);

    protected override IWaveIn CreateWaveIn(AudioCaptureOptions options)
    {
        try
        {
            var device = FindDevice(options.DeviceId, DataFlow.Capture);
            return device is null ? new WasapiCapture() : new WasapiCapture(device);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(
                "No microphone found. Connect a microphone (or set it as the default recording device) and try again.", ex);
        }
    }
}

public sealed class SystemLoopbackCaptureService : WasapiCaptureServiceBase
{
    public override AudioSourceType Source => AudioSourceType.SystemAudio;

    public override bool IsAvailable(AudioCaptureOptions options) =>
        HasEndpoint(options.DeviceId, DataFlow.Render);

    protected override IWaveIn CreateWaveIn(AudioCaptureOptions options)
    {
        try
        {
            var device = FindDevice(options.DeviceId, DataFlow.Render);
            // Note: loopback produces no DataAvailable callbacks while the system is silent.
            return device is null ? new WasapiLoopbackCapture() : new WasapiLoopbackCapture(device);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(
                "No playback device found for system audio. Connect speakers/headphones (or set a default playback device) and try again.", ex);
        }
    }
}
