using LocalTranscriber.Audio;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Audio.Tests;

public class AudioModelTests
{
    private static AudioChunk MakeChunk(int bytes, int sampleRate = 16000, int channels = 1, int bits = 16)
        => new(AudioSourceType.Microphone, new byte[bytes], sampleRate, channels, bits,
            IsIeeeFloat: false, CapturedAt: new DateTimeOffset(2026, 7, 9, 10, 0, 10, TimeSpan.Zero));

    [Fact]
    public void Chunk_ComputesBytesPerSecond()
    {
        Assert.Equal(32000, MakeChunk(100).BytesPerSecond); // 16000 Hz * 1 ch * 2 bytes
        Assert.Equal(384000, MakeChunk(100, 48000, 2, 32).BytesPerSecond);
    }

    [Fact]
    public void Chunk_ComputesDurationFromLength()
    {
        // 32000 bytes/sec -> 16000 bytes = 0.5 s
        Assert.Equal(TimeSpan.FromMilliseconds(500), MakeChunk(16000).Duration);
    }

    [Fact]
    public void Chunk_StartsAt_IsCapturedAtMinusDuration()
    {
        var chunk = MakeChunk(32000); // 1 second
        Assert.Equal(chunk.CapturedAt - TimeSpan.FromSeconds(1), chunk.StartsAt);
    }

    [Fact]
    public void WavDebugWriter_RejectsNonWavPath()
    {
        Assert.Throws<ArgumentException>(() => new WavDebugWriter(Path.Combine(Path.GetTempPath(), "out.mp3")));
    }

    [Fact]
    public void WavDebugWriter_WritesPlayableWavFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lt-audio-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "test.wav");
        try
        {
            using (var writer = new WavDebugWriter(path))
            {
                writer.Write(MakeChunk(32000));
                Assert.Equal(32000, writer.BytesWritten);
            }

            Assert.True(File.Exists(path));
            // RIFF header + 1 second of 16kHz mono 16-bit
            byte[] header = File.ReadAllBytes(path);
            Assert.True(header.Length > 44);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(header, 8, 4));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DeviceInfo_MapsFields()
    {
        var d = new AudioDeviceInfo("id-1", "Fancy Mic", IsInput: true, IsDefault: true);
        Assert.Equal("id-1", d.Id);
        Assert.True(d.IsInput);
        Assert.True(d.IsDefault);
    }
}
