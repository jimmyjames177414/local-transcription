using LocalTranscriber.Audio;

namespace LocalTranscriber.Audio.Tests;

public class PcmResamplerTests
{
    [Fact]
    public void FloatToPcm16_ProducesTwoLittleEndianBytesPerSample()
    {
        byte[] bytes = PcmResampler.FloatToPcm16(new[] { 0f });

        Assert.Equal(2, bytes.Length);
        Assert.Equal(0, bytes[0]);
        Assert.Equal(0, bytes[1]);
    }

    [Fact]
    public void FloatToPcm16_MapsFullScaleToInt16Extents()
    {
        byte[] bytes = PcmResampler.FloatToPcm16(new[] { 1f, -1f });

        // +1.0 -> 32767 (0x7FFF), little-endian
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0x7F, bytes[1]);
        // -1.0 -> -32767 (0x8001), little-endian
        short negative = (short)(bytes[2] | (bytes[3] << 8));
        Assert.Equal(-32767, negative);
    }

    [Fact]
    public void FloatToPcm16_ClampsOutOfRangeValues()
    {
        byte[] bytes = PcmResampler.FloatToPcm16(new[] { 2f, -2f });

        short high = (short)(bytes[0] | (bytes[1] << 8));
        short low = (short)(bytes[2] | (bytes[3] << 8));
        Assert.Equal(32767, high);
        Assert.Equal(-32767, low);
    }

    [Fact]
    public void FloatToPcm16_IsDeterministic()
    {
        var samples = new[] { 0.25f, -0.5f, 0.75f };
        Assert.Equal(PcmResampler.FloatToPcm16(samples), PcmResampler.FloatToPcm16(samples));
    }

    [Fact]
    public void FloatToPcm16_EmptyInputProducesEmptyOutput()
    {
        Assert.Empty(PcmResampler.FloatToPcm16(ReadOnlySpan<float>.Empty));
    }
}
