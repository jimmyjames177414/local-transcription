using LocalTranscriber.Audio;
using LocalTranscriber.Engine;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Engine.Tests;

public class AudioWindowBufferTests
{
    private static AudioChunk Chunk(int lengthBytes, int sampleRate, int channels, int bits, bool isFloat)
        => new(AudioSourceType.SystemAudio, new byte[lengthBytes], sampleRate, channels, bits, isFloat, DateTimeOffset.Now);

    [Fact]
    public void Add_FormatChangeMidBuffer_DropsStalePartial_AndRelatchesToNewFormat()
    {
        var buffer = new AudioWindowBuffer(windowSeconds: 1, overlapMs: 0);

        // Format A: 16 kHz mono 16-bit PCM. A 1s window is 16000 * 1 * 2 = 32000 bytes,
        // so 16000 bytes (0.5s) leaves a partial buffer that has NOT completed a window.
        Assert.Null(buffer.Add(Chunk(16000, sampleRate: 16000, channels: 1, bits: 16, isFloat: false)));

        // Device hot-reconnect hands us Format B: 48 kHz stereo 32-bit float. A 1s window is
        // 48000 * 2 * 4 = 384000 bytes, delivered in one chunk. The stale Format-A partial must be
        // dropped and the buffer re-latched to B, so the completed window carries B — not A, and
        // not A's leftover bytes prepended.
        var window = buffer.Add(Chunk(384000, sampleRate: 48000, channels: 2, bits: 32, isFloat: true));

        Assert.NotNull(window);
        Assert.Equal(48000, window!.SampleRate);
        Assert.Equal(2, window.Channels);
        Assert.Equal(32, window.BitsPerSample);
        Assert.True(window.IsIeeeFloat);
        // 384000 (B alone), not 400000 (A's stale 16000 + B) — proves the partial was dropped.
        Assert.Equal(384000, window.Data.Length);
    }

    [Fact]
    public void Add_SameFormat_AccumulatesUntilWindowCompletes()
    {
        var buffer = new AudioWindowBuffer(windowSeconds: 1, overlapMs: 0);

        // 16 kHz mono 16-bit: 1s = 32000 bytes. Two 16000-byte chunks complete one window.
        Assert.Null(buffer.Add(Chunk(16000, 16000, 1, 16, false)));
        var window = buffer.Add(Chunk(16000, 16000, 1, 16, false));

        Assert.NotNull(window);
        Assert.Equal(16000, window!.SampleRate);
        Assert.Equal(32000, window.Data.Length);
    }
}
