using LocalTranscriber.Audio;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Engine;

/// <summary>
/// A completed window of audio ready for transcription.
/// </summary>
public sealed record AudioWindow(
    AudioSourceType Source,
    byte[] Data,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    bool IsIeeeFloat,
    DateTimeOffset StartsAt);

/// <summary>
/// Accumulates capture chunks into fixed-length windows (chunkSeconds), carrying
/// overlapMs of trailing audio into the next window to soften word cuts at boundaries.
/// </summary>
public sealed class AudioWindowBuffer
{
    private readonly int _windowSeconds;
    private readonly int _overlapMs;
    private readonly MemoryStream _buffer = new();
    private int _sampleRate;
    private int _channels;
    private int _bitsPerSample;
    private bool _isFloat;
    private DateTimeOffset _windowStart;
    private bool _hasFormat;

    public AudioWindowBuffer(int windowSeconds, int overlapMs)
    {
        _windowSeconds = Math.Max(1, windowSeconds);
        _overlapMs = Math.Max(0, overlapMs);
    }

    /// <summary>Adds a chunk; returns a completed window when enough audio accumulated, else null.</summary>
    public AudioWindow? Add(AudioChunk chunk)
    {
        // A mid-session device hot-reconnect (CaptureHost.TryRestartAsync) can hand us a chunk
        // in a different format than the one we latched. Appending it to the stale buffer would
        // tag the window with the wrong format (bad WAV header, garbled Whisper input, wrong
        // Peak()/timestamps). Drop the stale partial buffer and re-latch on the new format.
        if (_hasFormat &&
            (chunk.SampleRate != _sampleRate ||
             chunk.Channels != _channels ||
             chunk.BitsPerSample != _bitsPerSample ||
             chunk.IsIeeeFloat != _isFloat))
        {
            _buffer.SetLength(0);
            _hasFormat = false;
        }

        if (!_hasFormat)
        {
            _sampleRate = chunk.SampleRate;
            _channels = chunk.Channels;
            _bitsPerSample = chunk.BitsPerSample;
            _isFloat = chunk.IsIeeeFloat;
            _windowStart = chunk.StartsAt;
            _hasFormat = true;
        }

        _buffer.Write(chunk.Data, 0, chunk.Data.Length);

        int bytesPerSecond = _sampleRate * _channels * (_bitsPerSample / 8);
        long windowBytes = (long)bytesPerSecond * _windowSeconds;
        if (_buffer.Length < windowBytes)
        {
            return null;
        }

        byte[] windowData = _buffer.ToArray();
        var window = new AudioWindow(chunk.Source, windowData, _sampleRate, _channels, _bitsPerSample, _isFloat, _windowStart);

        // Carry the overlap tail into the next window.
        int blockAlign = _channels * (_bitsPerSample / 8);
        int overlapBytes = (int)Math.Min((long)bytesPerSecond * _overlapMs / 1000, windowData.Length);
        overlapBytes -= overlapBytes % Math.Max(1, blockAlign);

        _buffer.SetLength(0);
        if (overlapBytes > 0)
        {
            _buffer.Write(windowData, windowData.Length - overlapBytes, overlapBytes);
        }

        double consumedSeconds = (double)(windowData.Length - overlapBytes) / bytesPerSecond;
        _windowStart = _windowStart.AddSeconds(consumedSeconds);

        return window;
    }

    /// <summary>Returns whatever is buffered as a final window (used at stop), or null when empty/too short.</summary>
    public AudioWindow? Flush(AudioSourceType source)
    {
        if (!_hasFormat || _buffer.Length == 0)
        {
            return null;
        }

        int bytesPerSecond = _sampleRate * _channels * (_bitsPerSample / 8);
        if (_buffer.Length < bytesPerSecond) // under 1 second: not worth transcribing
        {
            return null;
        }

        var window = new AudioWindow(source, _buffer.ToArray(), _sampleRate, _channels, _bitsPerSample, _isFloat, _windowStart);
        _buffer.SetLength(0);
        return window;
    }

    /// <summary>Peak amplitude 0..1 — used to skip silent windows (whisper hallucinates on silence).</summary>
    public static double Peak(AudioWindow window)
    {
        if (window.IsIeeeFloat && window.BitsPerSample == 32)
        {
            double peak = 0;
            for (int i = 0; i + 3 < window.Data.Length; i += 4)
            {
                float v = BitConverter.ToSingle(window.Data, i);
                double a = Math.Abs(v);
                if (a > peak)
                {
                    peak = a;
                }
            }
            return peak;
        }

        if (window.BitsPerSample == 16)
        {
            int peak = 0;
            for (int i = 0; i + 1 < window.Data.Length; i += 2)
            {
                int v = Math.Abs((int)BitConverter.ToInt16(window.Data, i));
                if (v > peak)
                {
                    peak = v;
                }
            }
            return peak / 32768.0;
        }

        return 1; // unknown format: never skip
    }
}
