using System.CommandLine;
using LocalTranscriber.AI;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Cli;

public static class TranscribeCommand
{
    public static Command Build(ConfigService configService)
    {
        var audioOpt = new Option<string>("--audio", "WAV file to transcribe.") { IsRequired = true };
        var modelOpt = new Option<string?>("--model", () => null, "Whisper model path (defaults to config whisperModelPath).");
        var outputOpt = new Option<string?>("--output", () => null, "Optional path to also write the transcript text.");
        var languageOpt = new Option<string?>("--language", () => null, "Language code (e.g. en). Auto-detect when omitted.");

        var cmd = new Command("transcribe", "Transcribe a WAV file locally with whisper.cpp. No cloud, no keys.");
        cmd.AddOption(audioOpt);
        cmd.AddOption(modelOpt);
        cmd.AddOption(outputOpt);
        cmd.AddOption(languageOpt);
        cmd.SetHandler(async (string audio, string? model, string? output, string? language) =>
        {
            model ??= configService.Load().WhisperModelPath;
            try
            {
                using var service = new WhisperCppTranscriptionService();
                var result = await service.TranscribeAsync(new TranscriptionRequest
                {
                    AudioPath = audio,
                    ModelPath = model,
                    Language = language
                });

                if (result.Segments.Count == 0)
                {
                    Console.WriteLine("(no speech detected)");
                }
                foreach (var s in result.Segments)
                {
                    Console.WriteLine($"[{TimeSpan.FromMilliseconds(s.StartMs):hh\\:mm\\:ss} -> {TimeSpan.FromMilliseconds(s.EndMs):hh\\:mm\\:ss}] {s.Text}");
                }
                Console.WriteLine();
                Console.WriteLine($"Took {result.Duration.TotalSeconds:F1}s. Avg segment confidence: {result.Confidence:F2}");

                if (output is not null)
                {
                    string? dir = Path.GetDirectoryName(Path.GetFullPath(output));
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    await File.WriteAllTextAsync(output, result.Text + Environment.NewLine);
                    Console.WriteLine($"Wrote transcript to {output}");
                }
            }
            catch (Exception ex) when (ex is WhisperModelNotFoundException or FileNotFoundException or TimeoutException)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
        }, audioOpt, modelOpt, outputOpt, languageOpt);
        return cmd;
    }
}
