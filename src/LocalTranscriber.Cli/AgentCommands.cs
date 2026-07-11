using System.CommandLine;
using LocalTranscriber.Agent;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;
using LocalTranscriber.Voice;

namespace LocalTranscriber.Cli;

public static class AgentCommands
{
    public static Command Build(ConfigService configService)
    {
        var agent = new Command("agent", "Live meeting AI sidecar (optional; off by default).");
        agent.AddCommand(BuildTailEvents());
        agent.AddCommand(BuildTalk(configService));
        return agent;
    }

    private static Command BuildTalk(ConfigService configService)
    {
        var transcriptOpt = new Option<string?>("--transcript", () => null, "Transcript .jsonl to ground the conversation in (optional).");
        var modeOpt = new Option<string?>("--mode", () => null, "Voice mode: hybrid | pushToTalk | continuous (default from config).");
        var cmd = new Command("talk", "Start a real-time voice conversation with the AI. Assistant captions stream to stdout; Ctrl+C stops.");
        cmd.AddOption(transcriptOpt);
        cmd.AddOption(modeOpt);
        cmd.SetHandler(async (string? transcript, string? mode) =>
        {
            var config = configService.Load();
            if (!string.IsNullOrWhiteSpace(mode))
            {
                config.Agent.Realtime.VoiceMode = mode;
            }
            // Running `agent talk` is the explicit user action that opens the realtime connection.
            config.Agent.Realtime.Enabled = true;

            var resolution = RealtimeVoiceFactory.Create(config, new SecretsService(), transcript);
            if (resolution.Session is null)
            {
                Console.Error.WriteLine(resolution.Notice);
                Environment.ExitCode = 1;
                return;
            }

            var session = resolution.Session;
            session.AssistantTextAvailable += (_, text) => Console.Write(text);
            session.StateChanged += (_, state) => Console.Error.WriteLine($"[voice] {state}");
            session.ErrorOccurred += (_, message) => Console.Error.WriteLine($"[voice error] {message}");

            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            try
            {
                await session.StartAsync(stop.Token);
                var voiceMode = RealtimeVoiceFactory.ParseMode(config.Agent.Realtime.VoiceMode);
                Console.Error.WriteLine($"Voice conversation started (mode: {voiceMode}). Ctrl+C to stop.");

                if (voiceMode == RealtimeVoiceMode.Hybrid)
                {
                    // Console hold-to-talk: Enter to begin a turn, Enter again to send.
                    while (!stop.IsCancellationRequested)
                    {
                        Console.Error.WriteLine("Press Enter to talk...");
                        if (Console.ReadLine() is null) break;
                        session.PushToTalkDown();
                        Console.Error.WriteLine("Recording — press Enter to send.");
                        if (Console.ReadLine() is null) break;
                        session.PushToTalkUp();
                    }
                }
                else
                {
                    await Task.Delay(Timeout.Infinite, stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await session.DisposeAsync();
                Console.Error.WriteLine("\nVoice conversation ended.");
            }
        }, transcriptOpt, modeOpt);
        return cmd;
    }

    private static Command BuildTailEvents()
    {
        var fileOpt = new Option<string>("--file", "Transcript .jsonl file to tail.") { IsRequired = true };
        var fromStartOpt = new Option<bool>("--from-start", () => false, "Read from the beginning instead of the checkpoint.");
        var cmd = new Command("tail-events", "Tail transcript events from a live .jsonl file (Ctrl+C to stop).");
        cmd.AddOption(fileOpt);
        cmd.AddOption(fromStartOpt);
        cmd.SetHandler(async (string file, bool fromStart) =>
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            await using var tailer = new TranscriptEventTailer();
            var options = new TranscriptTailOptions
            {
                JsonlPath = file,
                FromStart = fromStart,
                CheckpointPath = Path.Combine("output", "agent", "tailer-checkpoint.json")
            };

            Console.WriteLine($"Tailing {file} (Ctrl+C to stop)...");
            try
            {
                await foreach (var e in tailer.TailAsync(options, stop.Token))
                {
                    Console.WriteLine($"[{e.Timestamp.ToLocalTime():HH:mm:ss}] {e.Speaker.DisplayName} ({e.Source}): {e.Text}");
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, fileOpt, fromStartOpt);
        return cmd;
    }
}
