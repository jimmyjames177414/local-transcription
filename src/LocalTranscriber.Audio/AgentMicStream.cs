using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LocalTranscriber.Audio;

/// <summary>
/// A microphone-only PCM source for the realtime voice pipeline. Emits 24 kHz / 16-bit / mono
/// little-endian frames. Wired to the microphone only — meeting/system audio is never streamed.
/// </summary>
public interface IAgentMicStream : IAsyncDisposable
{
    /// <summary>Raised (on the capture thread) with a 24 kHz PCM16 mono frame.</summary>
    event EventHandler<byte[]>? FrameAvailable;

    bool IsCapturing { get; }

    /// <summary>True when a usable microphone endpoint exists.</summary>
    bool IsAvailable { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Wraps a dedicated <see cref="MicrophoneCaptureService"/> (separate from the transcription mic)
/// and converts each captured chunk to 24 kHz PCM16 mono using the resampling chain proven in
/// <c>WavSampleReader</c>. Never captures system/loopback audio.
/// </summary>
public sealed class ResamplingAgentMicStream : IAgentMicStream
{
    public const int RealtimeSampleRate = 24000;

    private readonly MicrophoneCaptureService _capture = new();
    private readonly AudioCaptureOptions _options;

    public ResamplingAgentMicStream(string? inputDeviceId = null)
    {
        _options = new AudioCaptureOptions { DeviceId = inputDeviceId };
        _capture.ChunkAvailable += OnChunkAvailable;
    }

    public event EventHandler<byte[]>? FrameAvailable;

    public bool IsCapturing => _capture.IsCapturing;

    public bool IsAvailable => _capture.IsAvailable(_options);

    public Task StartAsync(CancellationToken cancellationToken = default)
        => _capture.StartAsync(_options, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => _capture.StopAsync(cancellationToken);

    private void OnChunkAvailable(object? sender, AudioChunk chunk)
    {
        byte[] pcm = ConvertTo24kMonoPcm16(chunk);
        if (pcm.Length > 0)
        {
            FrameAvailable?.Invoke(this, pcm);
        }
    }

    /// <summary>
    /// Converts a captured chunk (any rate/channel/format) to 24 kHz PCM16 mono bytes via the
    /// StereoToMono + WDL resampler chain, then <see cref="PcmResampler.FloatToPcm16"/>.
    /// </summary>
    public static byte[] ConvertTo24kMonoPcm16(AudioChunk chunk)
    {
        if (chunk.Data.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var format = chunk.IsIeeeFloat
            ? WaveFormat.CreateIeeeFloatWaveFormat(chunk.SampleRate, chunk.Channels)
            : new WaveFormat(chunk.SampleRate, chunk.BitsPerSample, chunk.Channels);

        using var raw = new RawSourceWaveStream(chunk.Data, 0, chunk.Data.Length, format);
        ISampleProvider samples = raw.ToSampleProvider();

        if (samples.WaveFormat.Channels > 1)
        {
            samples = samples.WaveFormat.Channels == 2
                ? new StereoToMonoSampleProvider(samples)
                : new MultiplexingSampleProvider(new[] { samples }, 1);
        }

        if (samples.WaveFormat.SampleRate != RealtimeSampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, RealtimeSampleRate);
        }

        var floats = new List<float>(capacity: RealtimeSampleRate / 4);
        var buffer = new float[RealtimeSampleRate];
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                floats.Add(buffer[i]);
            }
        }

        return PcmResampler.FloatToPcm16(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floats));
    }

    public async ValueTask DisposeAsync()
    {
        _capture.ChunkAvailable -= OnChunkAvailable;
        await _capture.DisposeAsync().ConfigureAwait(false);
    }
}
