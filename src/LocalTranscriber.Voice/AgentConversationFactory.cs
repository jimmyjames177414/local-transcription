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

        if (!Directory.Exists(cli.WorkspaceFolder))
        {
            return new RealtimeVoiceFactory.Resolution(null, $"Workspace folder does not exist: {cli.WorkspaceFolder}");
        }

        string? executable = ResolveExecutable(cli.ExecutablePath);
        if (executable is null)
        {
            return new RealtimeVoiceFactory.Resolution(null, $"Claude CLI executable not found: '{cli.ExecutablePath}' (not an existing path or on PATH).");
        }

        var options = new ClaudeCliConversationOptions
        {
            ExecutablePath = executable,
            WorkspaceFolder = Path.GetFullPath(cli.WorkspaceFolder),
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
