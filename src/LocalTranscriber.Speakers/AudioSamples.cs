using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LocalTranscriber.Speakers;

/// <summary>
/// WAV -> 16kHz mono float samples for the sherpa-onnx models.
/// </summary>
internal static class AudioSamples
{
    public const int SampleRate = 16000;

    public static float[] ReadMono16k(string wavPath, long? startMs = null, long? endMs = null)
    {
        using var reader = new WaveFileReader(wavPath);
        ISampleProvider samples = reader.ToSampleProvider();

        if (samples.WaveFormat.Channels == 2)
        {
            samples = new StereoToMonoSampleProvider(samples);
        }
        else if (samples.WaveFormat.Channels > 2)
        {
            samples = new MultiplexingSampleProvider(new[] { samples }, 1);
        }

        if (samples.WaveFormat.SampleRate != SampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, SampleRate);
        }

        var result = new List<float>(capacity: 1 << 20);
        var buffer = new float[SampleRate];
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                result.Add(buffer[i]);
            }
        }

        if (startMs is null && endMs is null)
        {
            return result.ToArray();
        }

        int start = (int)Math.Clamp((startMs ?? 0) * SampleRate / 1000, 0, result.Count);
        int end = (int)Math.Clamp((endMs ?? long.MaxValue / (SampleRate + 1)) * SampleRate / 1000, start, result.Count);
        return result.GetRange(start, end - start).ToArray();
    }
}
