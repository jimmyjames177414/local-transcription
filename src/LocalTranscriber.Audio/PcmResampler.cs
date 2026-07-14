namespace LocalTranscriber.Audio;

/// <summary>
/// Deterministic float→PCM16 conversion for the realtime voice pipeline. Kept separate
/// from the NAudio resampling chain so the numeric conversion is unit-testable in isolation.
/// </summary>
public static class PcmResampler
{
    /// <summary>
    /// Converts normalized float samples in [-1, 1] to little-endian signed 16-bit PCM bytes.
    /// Values outside the range are clamped. Output length is <c>samples.Length * 2</c>.
    /// </summary>
    public static byte[] FloatToPcm16(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = Math.Clamp(samples[i], -1f, 1f);
            short value = (short)Math.Round(clamped * short.MaxValue);
            bytes[i * 2] = (byte)(value & 0xFF);
            bytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return bytes;
    }
}
