using System.CommandLine;
using LocalTranscriber.Engine;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Cli;

public static class CliApp
{
    public static RootCommand BuildRootCommand(ConfigService? configService = null)
    {
        configService ??= new ConfigService();

        var root = new RootCommand("LocalTranscriber — local-only transcription. No cloud, no API keys.");

        root.AddCommand(BuildStatusCommand());
        root.AddCommand(BuildFakeSessionCommand(configService));
        root.AddCommand(BuildStartFakeCommand(configService));
        root.AddCommand(BuildTailCommand());
        root.AddCommand(BuildReadCommand());
        root.AddCommand(BuildSessionsCommand(configService));
        root.AddCommand(BuildSpeakersCommand(configService));
        root.AddCommand(BuildRenameSpeakerCommand(configService));
        root.AddCommand(BuildForgetSpeakerCommand(configService));
        root.AddCommand(BuildConfigCommand(configService));
        root.AddCommand(AudioCommands.Build());

        return root;
    }

    private static SqliteDatabase OpenDatabase(ConfigService configService)
        => new(configService.Load().DatabasePath);

    private static Command BuildStatusCommand()
    {
        var cmd = new Command("status", "Show transcription status.");
        cmd.SetHandler(() =>
        {
            // Cross-process status arrives with IPC in Phase 12. For now: no in-process session.
            Console.WriteLine("LocalTranscriber status: idle (no session in this process).");
            Console.WriteLine("Note: cross-process session status arrives in a later phase.");
        });
        return cmd;
    }

    private static Command BuildFakeSessionCommand(ConfigService configService)
    {
        var outputOpt = new Option<string>("--output", () => "./output/transcripts/test.txt", "Output .txt path (.jsonl written next to it).");
        var linesOpt = new Option<int>("--lines", () => 10, "Number of fake lines to write.");
        var cmd = new Command("fake-session", "Write a batch of fake transcript lines and exit.");
        cmd.AddOption(outputOpt);
        cmd.AddOption(linesOpt);
        cmd.SetHandler(async (string output, int lines) =>
        {
            string jsonlPath = Path.ChangeExtension(output, ".jsonl");
            var options = new TranscriptionSessionOptions
            {
                OutputTextPath = output,
                OutputJsonlPath = jsonlPath,
                FakeEventIntervalMs = 10
            };

            var db = OpenDatabase(configService);
            await using var engine = new FakeTranscriptionEngine(new SqliteSessionStore(db), new SqliteTranscriptEventStore(db));
            await engine.StartAsync(options);

            long target = lines;
            while (true)
            {
                var status = await engine.GetStatusAsync();
                if (status.EventCount >= target)
                {
                    break;
                }
                await Task.Delay(20);
            }

            await engine.StopAsync();
            Console.WriteLine($"Wrote {lines} fake lines to:");
            Console.WriteLine($"  {output}");
            Console.WriteLine($"  {jsonlPath}");
        }, outputOpt, linesOpt);
        return cmd;
    }

    private static Command BuildStartFakeCommand(ConfigService configService)
    {
        var outputOpt = new Option<string>("--output", () => "./output/transcripts/fake.txt", "Output .txt path (.jsonl written next to it).");
        var cmd = new Command("start-fake", "Run a fake live session in this process until Ctrl+C.");
        cmd.AddOption(outputOpt);
        cmd.SetHandler(async (string output) =>
        {
            string jsonlPath = Path.ChangeExtension(output, ".jsonl");
            var options = new TranscriptionSessionOptions
            {
                OutputTextPath = output,
                OutputJsonlPath = jsonlPath
            };

            var db = OpenDatabase(configService);
            await using var engine = new FakeTranscriptionEngine(new SqliteSessionStore(db), new SqliteTranscriptEventStore(db));
            await engine.StartAsync(options);
            Console.WriteLine($"Fake session started (session {options.SessionId}).");
            Console.WriteLine($"Writing to {output} — press Ctrl+C to stop.");
            Console.WriteLine("Note: this fake session runs in-process. Cross-process control arrives in a later phase.");

            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            try
            {
                await foreach (var e in engine.StreamEventsAsync(stop.Token))
                {
                    Console.WriteLine(TranscriptFormatting.FormatLine(e));
                }
            }
            catch (OperationCanceledException)
            {
            }

            await engine.StopAsync();
            Console.WriteLine("Stopped.");
        }, outputOpt);
        return cmd;
    }

    private static Command BuildTailCommand()
    {
        var fileOpt = new Option<string>("--file", "Transcript file to tail.") { IsRequired = true };
        var linesOpt = new Option<int>("--lines", () => 20, "Number of lines to print.");
        var cmd = new Command("tail", "Print the last N lines of a transcript file.");
        cmd.AddOption(fileOpt);
        cmd.AddOption(linesOpt);
        cmd.SetHandler((string file, int lines) =>
        {
            if (!File.Exists(file))
            {
                Console.Error.WriteLine($"File not found: {file}");
                Environment.ExitCode = 1;
                return;
            }

            foreach (var line in TailFile(file, lines))
            {
                Console.WriteLine(line);
            }
        }, fileOpt, linesOpt);
        return cmd;
    }

    public static IReadOnlyList<string> TailFile(string path, int lines)
    {
        var all = File.ReadAllLines(path);
        return all.Skip(Math.Max(0, all.Length - lines)).ToArray();
    }

    private static Command BuildReadCommand()
    {
        var fileOpt = new Option<string>("--file", "Transcript file to print.") { IsRequired = true };
        var cmd = new Command("read", "Print a whole transcript file.");
        cmd.AddOption(fileOpt);
        cmd.SetHandler((string file) =>
        {
            if (!File.Exists(file))
            {
                Console.Error.WriteLine($"File not found: {file}");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine(File.ReadAllText(file));
        }, fileOpt);
        return cmd;
    }

    private static Command BuildSessionsCommand(ConfigService configService)
    {
        var cmd = new Command("sessions", "List recorded sessions.");
        cmd.SetHandler(async () =>
        {
            var store = new SqliteSessionStore(OpenDatabase(configService));
            var sessions = await store.ListAsync();
            if (sessions.Count == 0)
            {
                Console.WriteLine("No sessions yet.");
                return;
            }

            foreach (var s in sessions)
            {
                string ended = s.EndedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                Console.WriteLine($"{s.Id}  {s.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ended: {ended}  [{s.Status}]  {s.OutputTextPath}");
            }
        });
        return cmd;
    }

    private static Command BuildSpeakersCommand(ConfigService configService)
    {
        var cmd = new Command("speakers", "List known speakers.");
        cmd.SetHandler(async () =>
        {
            var store = new SqliteKnownSpeakerStore(OpenDatabase(configService));
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
        });
        return cmd;
    }

    private static Command BuildRenameSpeakerCommand(ConfigService configService)
    {
        var fromOpt = new Option<string>("--from", "Current speaker name.") { IsRequired = true };
        var toOpt = new Option<string>("--to", "New speaker name.") { IsRequired = true };
        var cmd = new Command("rename-speaker", "Rename a speaker (creates the name if unknown).");
        cmd.AddOption(fromOpt);
        cmd.AddOption(toOpt);
        cmd.SetHandler(async (string from, string to) =>
        {
            var store = new SqliteKnownSpeakerStore(OpenDatabase(configService));
            await store.RenameAsync(from, to);
            Console.WriteLine($"Renamed '{from}' to '{to}'.");
        }, fromOpt, toOpt);
        return cmd;
    }

    private static Command BuildForgetSpeakerCommand(ConfigService configService)
    {
        var nameOpt = new Option<string>("--name", "Speaker name to forget.") { IsRequired = true };
        var cmd = new Command("forget-speaker", "Forget a speaker and its embeddings.");
        cmd.AddOption(nameOpt);
        cmd.SetHandler(async (string name) =>
        {
            var store = new SqliteKnownSpeakerStore(OpenDatabase(configService));
            if (await store.ForgetAsync(name))
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

    private static Command BuildConfigCommand(ConfigService configService)
    {
        var cmd = new Command("config", "Show or update local config.");

        var show = new Command("show", "Print current config JSON.");
        show.SetHandler(() =>
        {
            var config = configService.Load();
            configService.Save(config); // materialize defaults on first run
            Console.WriteLine(File.ReadAllText(configService.ConfigPath));
        });

        var keyArg = new Argument<string>("key", "Config key, e.g. transcriptFolder.");
        var valueArg = new Argument<string>("value", "New value.");
        var set = new Command("set", "Set a config value.");
        set.AddArgument(keyArg);
        set.AddArgument(valueArg);
        set.SetHandler((string key, string value) =>
        {
            var config = configService.Load();
            if (!configService.TrySet(config, key, value))
            {
                Console.Error.WriteLine($"Unknown config key or invalid value: {key} = {value}");
                Environment.ExitCode = 1;
                return;
            }

            configService.Save(config);
            Console.WriteLine($"Set {key} = {value}");
        }, keyArg, valueArg);

        cmd.AddCommand(show);
        cmd.AddCommand(set);
        return cmd;
    }
}
