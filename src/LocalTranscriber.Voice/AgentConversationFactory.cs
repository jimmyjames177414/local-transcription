using System.Diagnostics;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Voice;

/// <summary>
/// Dispatches to the configured assistant backend, returning the same
/// <see cref="RealtimeVoiceFactory.Resolution"/> shape either way: a session or a human-readable
/// notice explaining why it is unavailable. Provider "claude-cli" shells out to the local Claude
/// Code CLI; anything else uses the unchanged OpenAI realtime path.
/// </summary>
public static class AgentConversationFactory
{
    public static RealtimeVoiceFactory.Resolution Create(
        AppConfig config,
        SecretsService? secrets = null,
        string? transcriptJsonlPath = null,
        IReadOnlyList<RealtimeToolDefinition>? tools = null,
        Func<RealtimeToolCall, Task<string>>? toolHandler = null,
        string? notesFilePath = null)
    {
        string provider = (config.Agent.Provider ?? "openai").Trim().ToLowerInvariant();

        return provider switch
        {
            "claude-cli" => CreateClaudeCli(config, transcriptJsonlPath, notesFilePath),
            "hybrid" => CreateHybrid(config, secrets, transcriptJsonlPath, notesFilePath),
            _ => RealtimeVoiceFactory.Create(config, secrets, transcriptJsonlPath, tools, toolHandler)
        };
    }

    /// <summary>Claude CLI as the brain + OpenAI realtime as the voice (speaks Claude's replies).</summary>
    private static RealtimeVoiceFactory.Resolution CreateHybrid(
        AppConfig config, SecretsService? secrets, string? transcriptJsonlPath, string? notesFilePath)
    {
        var brainResolution = CreateClaudeCli(config, transcriptJsonlPath, notesFilePath);
        if (brainResolution.Session is not ClaudeCliConversation brain)
        {
            return brainResolution; // surfaces the workspace/executable notice
        }

        IReplySpeaker? speaker = null;
        // The voice (mouth) is optional: only when the user wants spoken replies and a key resolves.
        // Without it the hybrid still works as captions-only, so a missing key is not a hard failure.
        if (config.Agent.Realtime.SpeakReplies)
        {
            var (key, _) = (secrets ?? new SecretsService()).ResolveOpenAIKey(config.Agent.Realtime.ApiKeyEnvironmentVariable);
            if (key is not null)
            {
                speaker = new RealtimeSpeaker(new RealtimeSpeakerOptions
                {
                    ApiKey = key,
                    Model = config.Agent.Realtime.Model,
                    Voice = config.Agent.Realtime.Voice,
                    OutputAudioDeviceId = config.Agent.Realtime.OutputAudioDeviceId
                });
            }
        }

        return new RealtimeVoiceFactory.Resolution(new HybridBrainConversation(brain, speaker), null);
    }

    private static RealtimeVoiceFactory.Resolution CreateClaudeCli(AppConfig config, string? transcriptJsonlPath, string? notesFilePath)
    {
        var cli = config.Agent.ClaudeCli;

        if (!cli.Enabled)
        {
            return new RealtimeVoiceFactory.Resolution(null, "Claude CLI backend is not enabled (agent.claudeCli.enabled=false).");
        }

        if (string.IsNullOrWhiteSpace(cli.WorkspaceFolder))
        {
            return new RealtimeVoiceFactory.Resolution(null, "Choose a workspace folder for the Claude CLI backend (Settings → Assistant & privacy).");
        }

        string executable;
        string workspace;
        if (cli.UseWsl)
        {
            // WSL: workspace is a Linux path inside the distro; validate it there rather than on Windows.
            // The executable name (e.g. "claude") is resolved on the WSL PATH at launch, not here.
            if (!WslDirectoryExists(cli.WslDistro, cli.WorkspaceFolder, out string wslError))
            {
                return new RealtimeVoiceFactory.Resolution(null, wslError);
            }
            executable = string.IsNullOrWhiteSpace(cli.ExecutablePath) ? "claude" : cli.ExecutablePath;
            workspace = cli.WorkspaceFolder; // leave the Linux path untouched
        }
        else
        {
            if (!Directory.Exists(cli.WorkspaceFolder))
            {
                return new RealtimeVoiceFactory.Resolution(null, $"Workspace folder does not exist: {cli.WorkspaceFolder}");
            }
            string? resolved = ResolveExecutable(cli.ExecutablePath);
            if (resolved is null)
            {
                return new RealtimeVoiceFactory.Resolution(null, $"Claude CLI executable not found: '{cli.ExecutablePath}' (not an existing path or on PATH).");
            }
            executable = resolved;
            workspace = Path.GetFullPath(cli.WorkspaceFolder);
        }

        var options = new ClaudeCliConversationOptions
        {
            ExecutablePath = executable,
            WorkspaceFolder = workspace,
            UseWsl = cli.UseWsl,
            WslDistro = cli.WslDistro,
            Model = cli.Model,
            AllowEditsAndCommands = cli.AllowEditsAndCommands,
            TimeoutSeconds = cli.TimeoutSeconds,
            MaxTranscriptEvents = cli.MaxTranscriptEvents,
            Mode = RealtimeVoiceFactory.ParseMode(config.Agent.Realtime.VoiceMode),
            TranscriptJsonlPath = transcriptJsonlPath,
            InputAudioDeviceId = config.Agent.Realtime.InputAudioDeviceId,
            WhisperModelPath = config.WhisperModelPath,
            AgentOutputFolder = config.Agent.AgentOutputFolder,
            NotesFilePath = notesFilePath
        };

        return new RealtimeVoiceFactory.Resolution(new ClaudeCliConversation(options), null);
    }

    /// <summary>Checks a Linux directory exists inside WSL via <c>wsl.exe [-d distro] -- test -d path</c>.</summary>
    internal static bool WslDirectoryExists(string distro, string linuxPath, out string error)
    {
        error = "";
        try
        {
            var psi = new ProcessStartInfo("wsl.exe")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            if (!string.IsNullOrWhiteSpace(distro))
            {
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(distro);
            }
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("test");
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(linuxPath);

            using var p = Process.Start(psi);
            if (p is null)
            {
                error = "Could not launch wsl.exe. Is WSL installed?";
                return false;
            }
            // Drain both pipes so a chatty wsl.exe (e.g. a first-run/update notice) can't fill the
            // buffer and deadlock the WaitForExit below.
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                error = "WSL check timed out (distro not responding).";
                return false;
            }
            if (p.ExitCode != 0)
            {
                string where = string.IsNullOrWhiteSpace(distro) ? "the default WSL distro" : $"WSL distro '{distro}'";
                error = $"WSL workspace not found: '{linuxPath}' in {where}.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"WSL not available: {ex.Message}";
            return false;
        }
    }

    /// <summary>Returns the resolved executable path (an existing file, or the first PATH hit), else null.</summary>
    internal static string? ResolveExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        if (File.Exists(executable))
        {
            return Path.GetFullPath(executable);
        }

        // A rooted/relative path was given but the file is missing — don't fall through to a PATH search.
        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            foreach (var ext in extensions)
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), executable + ext);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch
                {
                    // Malformed PATH entry — skip it.
                }
            }
        }

        return null;
    }
}
