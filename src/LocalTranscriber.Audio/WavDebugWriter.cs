using NAudio.Wave;

namespace LocalTranscriber.Audio;

/// <summary>
/// Writes captured chunks to a .wav file for manual verification.
/// </summary>
public sealed class WavDebugWriter : IDisposable
{
    private readonly string _path;
    private WaveFileWriter? _writer;
    private readonly object _lock = new();

    public long BytesWritten { get; private set; }

    public WavDebugWriter(string path)
    {
        string full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(full), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"WAV output path must end in .wav: {path}");
        }

        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _path = full;
    }

    public void Write(AudioChunk chunk)
    {
        lock (_lock)
        {
            _writer ??= new WaveFileWriter(_path, chunk.IsIeeeFloat
                ? WaveFormat.CreateIeeeFloatWaveFormat(chunk.SampleRate, chunk.Channels)
                : new WaveFormat(chunk.SampleRate, chunk.BitsPerSample, chunk.Channels));

            _writer.Write(chunk.Data, 0, chunk.Data.Length);
            BytesWritten += chunk.Data.Length;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
