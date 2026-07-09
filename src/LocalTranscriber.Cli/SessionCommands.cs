using System.CommandLine;
using LocalTranscriber.Engine;
using LocalTranscriber.Engine.Ipc;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Cli;

public static class SessionCommands
{
    public static Command BuildStart(ConfigService configService)
    {
        var outputOpt = new Option<string?>("--output", () => null, "Output .txt path (defaults to transcript folder with timestamped name).");
        var micOpt = new Option<bool?>("--mic", () => null, "Capture microphone (default from config).");
        var systemOpt = new Option<bool?>("--system", () => null, "Capture system audio (default from config).");
        var cmd = new Command("start", "Start a real local transcription session. Runs until Ctrl+C; control it from other terminals via stop/pause/resume/status.");
        cmd.AddOption(outputOpt);
        cmd.AddOption(micOpt);
        cmd.AddOption(systemOpt);
        cmd.SetHandler(async (string? output, bool? mic, bool? system) =>
        {
            var existing = await EngineIpcClient.TrySendAsync("status");
            if (existing?.Status?.State is TranscriptionSessionState.Recording or TranscriptionSessionState.Paused)
            {
                Console.Error.WriteLine($"A session is already running (session {existing.Status.SessionId}). Stop it first.");
                Environment.ExitCode = 1;
                return;
            }

            var config = configService.Load();
            var options = EngineFactory.CreateSessionOptions(config, mic: mic, system: system);
            if (output is not null)
            {
                options = options with
                {
                    OutputTextPath = output,
                    OutputJsonlPath = Path.ChangeExtension(output, ".jsonl")
                };
            }

            await using var engine = EngineFactory.CreateReal(config);
            try
            {
                await engine.StartAsync(options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
                return;
            }

            await using var ipc = new EngineIpcServer(engine);
            ipc.Start();

            Console.WriteLine($"Recording (session {options.SessionId}).");
            Console.WriteLine($"  mic: {options.EnableMicrophone}  system audio: {options.EnableSystemAudio}");
            Console.WriteLine($"  transcript: {options.OutputTextPath}");
            Console.WriteLine("Press Ctrl+C to stop. Other terminals: localtranscriber stop|pause|resume|status");

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

            // Also exit when another process stopped the session via IPC.
            Console.WriteLine("Stopping (waiting for buffered audio to finish transcribing)...");
            await engine.StopAsync();
            var status = await engine.GetStatusAsync();
            Console.WriteLine($"Stopped. {status.EventCount} transcript events written.");
        }, outputOpt, micOpt, systemOpt);
        return cmd;
    }

    public static Command BuildIpcCommand(string name, string description, string ipcCommand)
    {
        var cmd = new Command(name, description);
        cmd.SetHandler(async () =>
        {
            var response = await EngineIpcClient.TrySendAsync(ipcCommand);
            if (response is null)
            {
                Console.WriteLine("No active session found (is a 'start' session or the app running?).");
                Environment.ExitCode = 1;
                return;
            }

            if (!response.Ok)
            {
                Console.Error.WriteLine(response.Message ?? "Command failed.");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine(response.Message ?? "OK");
        });
        return cmd;
    }

    public static Command BuildStatus()
    {
        var cmd = new Command("status", "Show status of the running transcription session.");
        cmd.SetHandler(async () =>
        {
            var response = await EngineIpcClient.TrySendAsync("status");
            if (response?.Status is null)
            {
                Console.WriteLine("LocalTranscriber status: idle (no active session).");
                return;
            }

            var s = response.Status;
            Console.WriteLine($"State:      {s.State}");
            Console.WriteLine($"Session:    {s.SessionId}");
            Console.WriteLine($"Transcript: {s.OutputTextPath}");
            Console.WriteLine($"Started:    {s.StartedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Last event: {s.LastEventAt?.ToLocalTime():HH:mm:ss}");
            Console.WriteLine($"Events:     {s.EventCount}");
            if (s.Error is not null)
            {
                Console.WriteLine($"Warnings:   {s.Error}");
            }
        });
        return cmd;
    }
}
