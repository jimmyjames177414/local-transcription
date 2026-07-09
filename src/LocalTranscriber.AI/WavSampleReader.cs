using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LocalTranscriber.AI;

/// <summary>
/// Loads a WAV file and converts it to the 16kHz mono float samples whisper expects.
/// </summary>
public static class WavSampleReader
{
    public const int WhisperSampleRate = 16000;

    public static float[] ReadMono16k(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        ISampleProvider samples = reader.ToSampleProvider();

        if (samples.WaveFormat.Channels > 1)
        {
            samples = samples.WaveFormat.Channels == 2
                ? new StereoToMonoSampleProvider(samples)
                : new MultiplexingSampleProvider(new[] { samples }, 1);
        }

        if (samples.WaveFormat.SampleRate != WhisperSampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, WhisperSampleRate);
        }

        var result = new List<float>(capacity: 1 << 20);
        var buffer = new float[WhisperSampleRate];
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                result.Add(buffer[i]);
            }
        }

        return result.ToArray();
    }
}
