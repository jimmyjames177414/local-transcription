using LocalTranscriber.Shared;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LocalTranscriber.Audio;

/// <summary>
/// Plays the realtime agent's PCM16 audio (24 kHz mono). Audio arrives incrementally as
/// deltas over the websocket and is buffered for gapless playback. <see cref="Stop"/> is the
/// hard barge-in stop (clear everything); <see cref="Flush"/> marks a turn boundary so
/// <see cref="PlayedMilliseconds"/> reports position within the current turn.
/// </summary>
public interface IAgentAudioOutput : IDisposable
{
    /// <summary>Queues little-endian PCM16 mono @24kHz for playback.</summary>
    void EnqueuePcm16(byte[] pcm);

    /// <summary>Decodes a base64 PCM16 delta and queues it.</summary>
    void EnqueueBase64(string base64Pcm);

    /// <summary>Barge-in hard stop: discards buffered audio and stops playback immediately.</summary>
    void Stop();

    /// <summary>Turn boundary: resets the played-position baseline (does not discard audio).</summary>
    void Flush();

    /// <summary>Milliseconds of audio actually played since the last <see cref="Flush"/>/<see cref="Stop"/>.</summary>
    long PlayedMilliseconds { get; }

    /// <summary>True while there is buffered audio still to play.</summary>
    bool IsPlaying { get; }
}

/// <summary>Discards all audio. Used when playback is unavailable or not wanted.</summary>
public sealed class NoOpAgentAudioOutput : IAgentAudioOutput
{
    public void EnqueuePcm16(byte[] pcm) { }
    public void EnqueueBase64(string base64Pcm) { }
    public void Stop() { }
    public void Flush() { }
    public long PlayedMilliseconds => 0;
    public bool IsPlaying => false;
    public void Dispose() { }
}

/// <summary>
/// <see cref="WaveOutEvent"/> + <see cref="BufferedWaveProvider"/> playback at 24 kHz / 16-bit / mono.
/// <see cref="PlayedMilliseconds"/> is derived from the device playback position so barge-in
/// truncation reports what was actually heard, not what was enqueued.
/// </summary>
public sealed class NAudioAgentAudioOutput : IAgentAudioOutput
{
    public const int SampleRate = 24000;

    private static readonly WaveFormat Format = new(SampleRate, 16, 1);

    private readonly object _lock = new();
    private readonly BufferedWaveProvider _buffer;
    private readonly IWavePlayer _output;
    private readonly IWavePosition _position;
    private readonly double _bytesPerMs;
    private long _baselineBytes;
    private bool _started;
    private bool _disposed;

    /// <param name="outputDeviceId">
    /// MMDevice endpoint id to play through (from <see cref="AudioDeviceService.ListOutputDevices"/>).
    /// Null plays through the default playback device. Routing playback to a non-Bluetooth device
    /// keeps a Bluetooth headset in A2DP, avoiding the profile flap that drops its mic.
    /// </param>
    public NAudioAgentAudioOutput(string? outputDeviceId = null)
    {
        _buffer = new BufferedWaveProvider(Format)
        {
            BufferDuration = TimeSpan.FromSeconds(60),
            DiscardOnBufferOverflow = true
        };
        _output = CreateOutput(outputDeviceId);
        _position = (IWavePosition)_output;
        _output.Init(_buffer);
        // Derive bytes/ms from the ACTUAL output format: WasapiOut in shared mode may run at the
        // device mix rate, so the 24 kHz source rate would give a wrong played-position (breaking
        // barge-in truncation). IWavePosition.OutputWaveFormat is valid after Init.
        _bytesPerMs = _position.OutputWaveFormat.AverageBytesPerSecond / 1000.0;
    }

    /// <summary>
    /// Default (null id) uses <see cref="WaveOutEvent"/> — unchanged behaviour. An explicit endpoint
    /// id needs <see cref="WasapiOut"/>, which addresses devices by MMDevice. Falls back to the
    /// default device (with a warning) if the id no longer resolves.
    /// </summary>
    private static IWavePlayer CreateOutput(string? outputDeviceId)
    {
        if (outputDeviceId is null)
        {
            return new WaveOutEvent();
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(outputDeviceId);
            if (device is not null)
            {
                return new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("voice", $"Voice output device '{outputDeviceId}' unavailable ({ex.Message}); using default playback.");
        }

        return new WaveOutEvent();
    }

    public void EnqueuePcm16(byte[] pcm)
    {
        if (pcm.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _buffer.AddSamples(pcm, 0, pcm.Length);
            if (!_started)
            {
                _output.Play();
                _started = true;
            }
        }
    }

    public void EnqueueBase64(string base64Pcm)
    {
        if (string.IsNullOrEmpty(base64Pcm))
        {
            return;
        }

        EnqueuePcm16(Convert.FromBase64String(base64Pcm));
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _output.Stop();
            _buffer.ClearBuffer();
            _started = false;
            _baselineBytes = 0;
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _baselineBytes = _position.GetPosition();
        }
    }

    public long PlayedMilliseconds
    {
        get
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return 0;
                }

                long played = _position.GetPosition() - _baselineBytes;
                return played <= 0 ? 0 : (long)(played / _bytesPerMs);
            }
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_lock)
            {
                return !_disposed && _buffer.BufferedBytes > 0;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _output.Dispose();
        }
    }
}
