using System.CommandLine;
using LocalTranscriber.Speakers;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Cli;

public static class SpeakerCommands
{
    public static Command Build(ConfigService configService)
    {
        var speakers = new Command("speakers", "Speaker management, diarization, and voice matching.");
        speakers.AddCommand(BuildList(configService));
        speakers.AddCommand(BuildRename(configService));
        speakers.AddCommand(BuildForget(configService));
        speakers.AddCommand(BuildEnroll(configService));
        speakers.AddCommand(BuildMatch(configService));
        speakers.AddCommand(BuildDiarize(configService));
        speakers.AddCommand(BuildEmbedding(configService));

        // bare `speakers` behaves like `speakers list`
        speakers.SetHandler(async () => await ListAsync(configService));
        return speakers;
    }

    private static SqliteDatabase Db(ConfigService configService) => new(configService.Load().DatabasePath);

    private static SpeakerModelConfig Models(ConfigService configService, string? modelDir)
        => new() { ModelDir = modelDir ?? configService.Load().SpeakerModelPath };

    private static SpeakerRecognitionService Recognition(ConfigService configService)
    {
        var config = configService.Load();
        var db = Db(configService);
        return new SpeakerRecognitionService(
            new SqliteKnownSpeakerStore(db),
            new SqliteSpeakerEmbeddingStore(db),
            new SpeakerMemoryOptions
            {
                MatchThreshold = config.SpeakerMatchThreshold,
                UncertainThreshold = config.SpeakerUncertainThreshold
            });
    }

    private static async Task ListAsync(ConfigService configService)
    {
        var store = new SqliteKnownSpeakerStore(Db(configService));
        var speakers = await store.ListAsync();
        if (speakers.Count == 0)
        {
            Console.WriteLine("No known speakers yet.");
            return;
        }

        foreach (var s in speakers)
        {
            string lastSeen = s.LastSeenAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "never";
            Console.WriteLine($"{s.DisplayName}  (samples: {s.SampleCount}, last seen: {lastSeen}, id: {s.Id})");
        }
    }

    private static Command BuildList(ConfigService configService)
    {
        var cmd = new Command("list", "List known speakers.");
        cmd.SetHandler(async () => await ListAsync(configService));
        return cmd;
    }

    private static Command BuildRename(ConfigService configService)
    {
        var fromOpt = new Option<string>("--from", "Current speaker name.") { IsRequired = true };
        var toOpt = new Option<string>("--to", "New speaker name.") { IsRequired = true };
        var cmd = new Command("rename", "Rename a speaker, keeping its voice embeddings.");
        cmd.AddOption(fromOpt);
        cmd.AddOption(toOpt);
        cmd.SetHandler(async (string from, string to) =>
        {
            await new SqliteKnownSpeakerStore(Db(configService)).RenameAsync(from, to);
            Console.WriteLine($"Renamed '{from}' to '{to}'.");
        }, fromOpt, toOpt);
        return cmd;
    }

    private static Command BuildForget(ConfigService configService)
    {
        var nameOpt = new Option<string>("--name", "Speaker name to forget.") { IsRequired = true };
        var cmd = new Command("forget", "Forget a speaker and delete its embeddings.");
        cmd.AddOption(nameOpt);
        cmd.SetHandler(async (string name) =>
        {
            if (await new SqliteKnownSpeakerStore(Db(configService)).ForgetAsync(name))
            {
                Console.WriteLine($"Forgot speaker '{name}'.");
            }
            else
            {
                Console.Error.WriteLine($"Speaker not found: {name}");
                Environment.ExitCode = 1;
            }
        }, nameOpt);
        return cmd;
    }

    private static Command BuildEnroll(ConfigService configService)
    {
        var nameOpt = new Option<string>("--name", "Speaker name to enroll.") { IsRequired = true };
        var audioOpt = new Option<string>("--audio", "WAV sample of this speaker's voice.") { IsRequired = true };
        var modelOpt = new Option<string?>("--model", () => null, "Speaker model folder (default from config).");
        var cmd = new Command("enroll", "Enroll a speaker from a voice sample so later sessions recognize them.");
        cmd.AddOption(nameOpt);
        cmd.AddOption(audioOpt);
        cmd.AddOption(modelOpt);
        cmd.SetHandler(async (string name, string audio, string? model) =>
        {
            try
            {
                using var embeddings = new SherpaOnnxEmbeddingService();
                var embedding = await embeddings.ExtractEmbeddingAsync(new SpeakerEmbeddingRequest
                {
                    AudioPath = audio,
                    Models = Models(configService, model)
                });
                await Recognition(configService).EnrollAsync(name, embedding, sessionId: null);
                Console.WriteLine($"Enrolled '{name}' ({embedding.Dimensions}-dim embedding, model {embedding.ModelName}).");
            }
            catch (Exception ex) when (ex is SpeakerModelNotFoundException or FileNotFoundException or InvalidOperationException)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
        }, nameOpt, audioOpt, modelOpt);
        return cmd;
    }

    private static Command BuildMatch(ConfigService configService)
    {
        var audioOpt = new Option<string>("--audio", "WAV sample to identify.") { IsRequired = true };
        var modelOpt = new Option<string?>("--model", () => null, "Speaker model folder (default from config).");
        var cmd = new Command("match", "Match a voice sample against known speakers.");
        cmd.AddOption(audioOpt);
        cmd.AddOption(modelOpt);
        cmd.SetHandler(async (string audio, string? model) =>
        {
            try
            {
                using var embeddings = new SherpaOnnxEmbeddingService();
                var embedding = await embeddings.ExtractEmbeddingAsync(new SpeakerEmbeddingRequest
                {
                    AudioPath = audio,
                    Models = Models(configService, model)
                });
                var match = await Recognition(configService).MatchAsync(embedding);
                if (match is null)
                {
                    Console.WriteLine("No match among known speakers.");
                }
                else
                {
                    string label = match.Certainty == SpeakerMatchCertainty.Confident ? match.DisplayName : $"possibly {match.DisplayName}";
                    Console.WriteLine($"{label}  (similarity: {match.Similarity:F3})");
                }
            }
            catch (Exception ex) when (ex is SpeakerModelNotFoundException or FileNotFoundException or InvalidOperationException)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
        }, audioOpt, modelOpt);
        return cmd;
    }

    private static Command BuildDiarize(ConfigService configService)
    {
        var audioOpt = new Option<string>("--audio", "WAV file to diarize.") { IsRequired = true };
        var modelOpt = new Option<string?>("--model", () => null, "Speaker model folder (default from config).");
        var numOpt = new Option<int?>("--num-speakers", () => null, "Exact speaker count when known.");
        var cmd = new Command("diarize", "Split audio into speaker-labeled segments (Speaker 1, Speaker 2, ...).");
        cmd.AddOption(audioOpt);
        cmd.AddOption(modelOpt);
        cmd.AddOption(numOpt);
        cmd.SetHandler(async (string audio, string? model, int? numSpeakers) =>
        {
            try
            {
                var service = new SherpaOnnxDiarizationService();
                var segments = await service.DiarizeAsync(new SpeakerDiarizationRequest
                {
                    AudioPath = audio,
                    Models = Models(configService, model),
                    NumSpeakers = numSpeakers
                });

                if (segments.Count == 0)
                {
                    Console.WriteLine("No speaker segments detected.");
                    return;
                }

                foreach (var s in segments)
                {
                    Console.WriteLine($"[{TimeSpan.FromMilliseconds(s.StartMs):hh\\:mm\\:ss\\.f} -> {TimeSpan.FromMilliseconds(s.EndMs):hh\\:mm\\:ss\\.f}] {s.TemporarySpeakerId}");
                }
                Console.WriteLine();
                Console.WriteLine("Note: speaker labels are best-effort clusters, not guaranteed identities.");
            }
            catch (Exception ex) when (ex is SpeakerModelNotFoundException or FileNotFoundException)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
        }, audioOpt, modelOpt, numOpt);
        return cmd;
    }

    private static Command BuildEmbedding(ConfigService configService)
    {
        var audioOpt = new Option<string>("--audio", "WAV file to embed.") { IsRequired = true };
        var modelOpt = new Option<string?>("--model", () => null, "Speaker model folder (default from config).");
        var cmd = new Command("embedding", "Extract a voice embedding vector from a WAV sample.");
        cmd.AddOption(audioOpt);
        cmd.AddOption(modelOpt);
        cmd.SetHandler(async (string audio, string? model) =>
        {
            try
            {
                using var embeddings = new SherpaOnnxEmbeddingService();
                var embedding = await embeddings.ExtractEmbeddingAsync(new SpeakerEmbeddingRequest
                {
                    AudioPath = audio,
                    Models = Models(configService, model)
                });
                Console.WriteLine($"Model: {embedding.ModelName}");
                Console.WriteLine($"Dimensions: {embedding.Dimensions}");
                Console.WriteLine($"First 8 values: {string.Join(", ", embedding.Vector.Take(8).Select(v => v.ToString("F4")))}");
            }
            catch (Exception ex) when (ex is SpeakerModelNotFoundException or FileNotFoundException or InvalidOperationException)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
        }, audioOpt, modelOpt);
        return cmd;
    }
}
