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

        root.AddCommand(SessionCommands.BuildStatus());
        root.AddCommand(SessionCommands.BuildStart(configService));
        root.AddCommand(SessionCommands.BuildIpcCommand("stop", "Stop the running transcription session.", "stop"));
        root.AddCommand(SessionCommands.BuildIpcCommand("pause", "Pause the running transcription session.", "pause"));
        root.AddCommand(SessionCommands.BuildIpcCommand("resume", "Resume the paused transcription session.", "resume"));
        root.AddCommand(BuildFakeSessionCommand(configService));
        root.AddCommand(BuildStartFakeCommand(configService));
        root.AddCommand(BuildTailCommand());
        root.AddCommand(BuildReadCommand());
        root.AddCommand(BuildSessionsCommand(configService));
        root.AddCommand(BuildExportMinutesCommand(configService));
        root.AddCommand(BuildSyncMinutesCommand(configService));
        root.AddCommand(BuildDeleteSessionCommand(configService));
        root.AddCommand(SpeakerCommands.Build(configService));
        root.AddCommand(BuildRenameSpeakerCommand(configService));
        root.AddCommand(BuildForgetSpeakerCommand(configService));
        root.AddCommand(BuildConfigCommand(configService));
        root.AddCommand(AudioCommands.Build());
        root.AddCommand(TranscribeCommand.Build(configService));
        root.AddCommand(AgentCommands.Build(configService));
        root.AddCommand(ContextCommands.Build(configService));

        return root;
    }

    private static SqliteDatabase OpenDatabase(ConfigService configService)
        => new(configService.Load().DatabasePath);

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

    private static Command BuildExportMinutesCommand(ConfigService configService)
    {
        var sessionOpt = new Option<string?>("--session", () => null, "Session id (or unique prefix); default = most recent session.");
        var outOpt = new Option<string?>("--out", () => null, "Destination folder; default = config minutesExport.folder (~/meetings).");
        var titleOpt = new Option<string?>("--title", () => null, "Frontmatter title; default = \"Meeting <start time>\".");
        var cmd = new Command("export-minutes", "Export a session as minutes-format markdown (transcript + notes, local file only).");
        cmd.AddOption(sessionOpt);
        cmd.AddOption(outOpt);
        cmd.AddOption(titleOpt);
        cmd.SetHandler(async (string? session, string? outFolder, string? title) =>
        {
            try
            {
                var service = new MinutesExportService(configService.Load());
                string path = await service.ExportAsync(session, outFolder, title);
                Console.WriteLine($"Exported: {path}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
        }, sessionOpt, outOpt, titleOpt);
        return cmd;
    }

    private static Command BuildSyncMinutesCommand(ConfigService configService)
    {
        var cmd = new Command("sync-minutes", "Export every finished session that has no minutes file yet.");
        cmd.SetHandler(async () =>
        {
            try
            {
                var service = new MinutesExportService(configService.Load());
                var written = await service.ExportMissingAsync();
                if (written.Count == 0)
                {
                    Console.WriteLine("Everything already synced.");
                    return;
                }
                foreach (string path in written)
                {
                    Console.WriteLine($"Exported: {path}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
        });
        return cmd;
    }

    private static Command BuildDeleteSessionCommand(ConfigService configService)
    {
        var sessionOpt = new Option<string>("--session", "Session id to delete.") { IsRequired = true };
        var yesOpt = new Option<bool>("--yes", "Confirm the deletion (required; there is no interactive prompt).");
        var keepMinutesOpt = new Option<bool>("--keep-minutes", "Keep the exported minutes file(s) in the meetings folder.");
        var cmd = new Command("delete-session", "Delete a session: transcript files, notes, and database rows. Voice memory is kept.");
        cmd.AddOption(sessionOpt);
        cmd.AddOption(yesOpt);
        cmd.AddOption(keepMinutesOpt);
        cmd.SetHandler(async (string session, bool yes, bool keepMinutes) =>
        {
            try
            {
                var service = new SessionDeletionService(configService.Load());
                var files = await service.ListFilesAsync(session);
                if (!yes)
                {
                    Console.WriteLine("This would delete:");
                    foreach (var f in files)
                    {
                        Console.WriteLine($"  {f.Path}");
                    }
                    Console.WriteLine("Re-run with --yes to confirm. Voice memory is unaffected.");
                    Environment.ExitCode = 1;
                    return;
                }

                await service.DeleteAsync(session, alsoRemoveMinutes: !keepMinutes);
                Console.WriteLine($"Deleted session {session} ({files.Count} file{(files.Count == 1 ? "" : "s")} removed).");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
        }, sessionOpt, yesOpt, keepMinutesOpt);
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
                string title = string.IsNullOrWhiteSpace(s.Title) ? "" : $"  \"{s.Title}\"";
                Console.WriteLine($"{s.Id}  {s.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ended: {ended}  [{s.Status}]{title}  {s.OutputTextPath}");
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
