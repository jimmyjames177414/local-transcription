using System.CommandLine;
using LocalTranscriber.Audio;

namespace LocalTranscriber.Cli;

public static class AudioCommands
{
    public static Command Build()
    {
        var audio = new Command("audio", "Audio device and capture utilities.");
        audio.AddCommand(BuildDevicesCommand());
        audio.AddCommand(BuildRecordCommand("record-mic", "Record microphone to a WAV file.",
            () => new MicrophoneCaptureService(), "./output/audio/mic-test.wav"));
        audio.AddCommand(BuildRecordCommand("record-system", "Record system/loopback audio to a WAV file. Silent if nothing is playing.",
            () => new SystemLoopbackCaptureService(), "./output/audio/system-test.wav"));
        audio.AddCommand(BuildRecordBothCommand());
        return audio;
    }

    private static Command BuildDevicesCommand()
    {
        var cmd = new Command("devices", "List input (microphone) and output (loopback) devices.");
        cmd.SetHandler(() =>
        {
            try
            {
                var service = new AudioDeviceService();
                Console.WriteLine("Input devices (microphones):");
                var inputs = service.ListInputDevices();
                if (inputs.Count == 0)
                {
                    Console.WriteLine("  (none found)");
                }
                foreach (var d in inputs)
                {
                    Console.WriteLine($"  {(d.IsDefault ? "*" : " ")} {d.Name}");
                    Console.WriteLine($"      id: {d.Id}");
                }

                Console.WriteLine();
                Console.WriteLine("Output devices (system audio via loopback):");
                var outputs = service.ListOutputDevices();
                if (outputs.Count == 0)
                {
                    Console.WriteLine("  (none found)");
                }
                foreach (var d in outputs)
                {
                    Console.WriteLine($"  {(d.IsDefault ? "*" : " ")} {d.Name}");
                    Console.WriteLine($"      id: {d.Id}");
                }
                Console.WriteLine();
                Console.WriteLine("* = default device");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to enumerate audio devices: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });
        return cmd;
    }

    private static Command BuildRecordCommand(string name, string description, Func<IAudioCaptureService> factory, string defaultOutput)
    {
        var secondsOpt = new Option<int>("--seconds", () => 10, "Recording duration in seconds.");
        var outputOpt = new Option<string>("--output", () => defaultOutput, "Output .wav path.");
        var deviceOpt = new Option<string?>("--device", () => null, "Device id (from 'audio devices'). Default device when omitted.");
        var cmd = new Command(name, description);
        cmd.AddOption(secondsOpt);
        cmd.AddOption(outputOpt);
        cmd.AddOption(deviceOpt);
        cmd.SetHandler(async (int seconds, string output, string? device) =>
        {
            Environment.ExitCode = await RecordAsync(factory(), seconds, output, device);
        }, secondsOpt, outputOpt, deviceOpt);
        return cmd;
    }

    private static Command BuildRecordBothCommand()
    {
        var secondsOpt = new Option<int>("--seconds", () => 10, "Recording duration in seconds.");
        var folderOpt = new Option<string>("--output-folder", () => "./output/audio", "Folder for mic.wav and system.wav.");
        var cmd = new Command("record-both", "Record microphone and system audio simultaneously to separate WAV files.");
        cmd.AddOption(secondsOpt);
        cmd.AddOption(folderOpt);
        cmd.SetHandler(async (int seconds, string folder) =>
        {
            var micTask = RecordAsync(new MicrophoneCaptureService(), seconds, Path.Combine(folder, "mic.wav"), null);
            var systemTask = RecordAsync(new SystemLoopbackCaptureService(), seconds, Path.Combine(folder, "system.wav"), null);
            int[] results = await Task.WhenAll(micTask, systemTask);
            Environment.ExitCode = results.Max();
        }, secondsOpt, folderOpt);
        return cmd;
    }

    public static async Task<int> RecordAsync(IAudioCaptureService service, int seconds, string output, string? deviceId)
    {
        if (seconds <= 0)
        {
            Console.Error.WriteLine("--seconds must be greater than zero.");
            return 1;
        }

        try
        {
            await using (service)
            {
                using var writer = new WavDebugWriter(output);
                service.ChunkAvailable += (_, chunk) => writer.Write(chunk);

                await service.StartAsync(new AudioCaptureOptions { DeviceId = deviceId });
                Console.WriteLine($"[{service.Source}] recording {seconds}s -> {output}");
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                await service.StopAsync();

                if (writer.BytesWritten == 0)
                {
                    Console.WriteLine($"[{service.Source}] captured no audio data. " +
                        (service.Source == LocalTranscriber.Shared.AudioSourceType.SystemAudio
                            ? "System loopback is silent when nothing is playing — play some audio and retry."
                            : "Check that the microphone is connected and not in exclusive use."));
                    return 0;
                }

                Console.WriteLine($"[{service.Source}] wrote {writer.BytesWritten / 1024} KB to {output}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Recording failed: {ex.Message}");
            return 1;
        }
    }
}
