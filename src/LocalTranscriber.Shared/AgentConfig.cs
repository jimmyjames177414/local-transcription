namespace LocalTranscriber.Shared;

/// <summary>
/// Real-time voice conversation input mode. All non-<see cref="Off"/> modes produce natural
/// OpenAI audio output; they differ only in how the user's turn is captured and sent.
/// </summary>
public enum RealtimeVoiceMode
{
    /// <summary>Voice conversation disabled.</summary>
    Off,
    /// <summary>Hold-to-talk → local whisper STT → send text only (no audio leaves the machine).</summary>
    Hybrid,
    /// <summary>Hold key → stream mic PCM, commit on release (your voice only, no server VAD).</summary>
    PushToTalk,
    /// <summary>Mic streamed continuously with server VAD and barge-in.</summary>
    Continuous
}

/// <summary>
/// Configuration for the optional Live Meeting AI Sidecar. Disabled by default —
/// the offline transcriber must work with none of this configured.
/// </summary>
public sealed class AgentConfig
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Assistant backend: "openai" (default — OpenAI realtime voice/text) or "claude-cli"
    /// (shell out to the local Claude Code CLI in a chosen workspace). Defaulting to "openai"
    /// preserves behaviour for existing configs.
    /// </summary>
    public string Provider { get; set; } = "openai";

    public string ContextFolder { get; set; } = "context";
    public string AgentOutputFolder { get; set; } = Path.Combine("output", "agent");
    public int RollingWindowMinutes { get; set; } = 5;
    public int MaxTranscriptEventsPerPrompt { get; set; } = 80;
    public int MaxContextCharacters { get; set; } = 20000;
    public List<string> RequiredContextFiles { get; set; } = new() { "codename-summary.md" };
    public RealtimeAgentConfig Realtime { get; set; } = new();
    public ClaudeCliAgentConfig ClaudeCli { get; set; } = new();
}

/// <summary>
/// Configuration for the optional Claude Code CLI assistant backend. Off by default — the CLI is
/// launched one-shot per turn with its working directory set to <see cref="WorkspaceFolder"/> so it
/// reads that project's CLAUDE.md, files, memory, and MCP tools. File-edit/command capability is
/// gated behind <see cref="AllowEditsAndCommands"/> (explicit one-time consent). Transcription stays
/// fully local regardless of this setting.
/// </summary>
public sealed class ClaudeCliAgentConfig
{
    public bool Enabled { get; set; }

    /// <summary>CLI executable; resolved on PATH (→ claude.exe on Windows) when not an absolute path.</summary>
    public string ExecutablePath { get; set; } = "claude";

    /// <summary>Process working directory the CLI runs in (required). The chosen project root.
    /// When <see cref="UseWsl"/> is true this is a Linux path inside the distro (e.g. /home/you/repos).</summary>
    public string WorkspaceFolder { get; set; } = "";

    /// <summary>Run the CLI inside WSL instead of natively on Windows. When true, the app launches
    /// <c>wsl.exe</c>, runs <see cref="ExecutablePath"/> (the WSL <c>claude</c>) in <see cref="WorkspaceFolder"/>
    /// (a Linux path), and translates any Windows paths handed to Claude (notes file) to <c>/mnt/…</c>.</summary>
    public bool UseWsl { get; set; }

    /// <summary>WSL distro name passed to <c>wsl.exe -d</c>; empty uses the default distro.</summary>
    public string WslDistro { get; set; } = "";

    /// <summary>Model alias passed with --model (e.g. "opus"); empty uses the CLI default.</summary>
    public string Model { get; set; } = "";

    /// <summary>Stored consent letting the CLI edit files and run commands in the workspace (full agent).</summary>
    public bool AllowEditsAndCommands { get; set; }

    /// <summary>Per-turn timeout in seconds before the child process is killed and an error surfaced.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Recent transcript lines snapshotted into each turn's prompt for live-meeting grounding.</summary>
    public int MaxTranscriptEvents { get; set; } = 10;

    /// <summary>Recently used workspaces, for the meeting-screen quick-switch dropdown.</summary>
    public List<string> RecentWorkspaces { get; set; } = new();
}

public sealed class RealtimeAgentConfig
{
    public bool Enabled { get; set; }
    public string ApiKeyEnvironmentVariable { get; set; } = "OPENAI_API_KEY";
    public string Model { get; set; } = "gpt-realtime-2.1-mini";
    public string Transport { get; set; } = "websocket";

    /// <summary>
    /// Real-time voice conversation mode: off | hybrid | pushToTalk | continuous.
    /// Default off. See <see cref="RealtimeVoiceMode"/>. All non-off modes produce
    /// natural OpenAI audio output; only pushToTalk/continuous stream your microphone.
    /// </summary>
    public string VoiceMode { get; set; } = "off";

    /// <summary>OpenAI realtime output voice (e.g. marin, cedar, alloy).</summary>
    public string Voice { get; set; } = "marin";

    /// <summary>Server VAD activation threshold (0..1). Higher = less sensitive (helps with echo).</summary>
    public double VadThreshold { get; set; } = 0.5;

    /// <summary>Server VAD silence (ms) before a turn is considered finished.</summary>
    public int VadSilenceMs { get; set; } = 500;

    /// <summary>Server VAD audio (ms) prepended before detected speech.</summary>
    public int VadPrefixPaddingMs { get; set; } = 300;

    /// <summary>Input (microphone) device id; null uses the default recording device.</summary>
    public string? InputAudioDeviceId { get; set; }

    /// <summary>Output (playback) device id; null uses the default playback device.</summary>
    public string? OutputAudioDeviceId { get; set; }

    /// <summary>
    /// Play the AI reply as audio (TTS). When false, replies are shown only as live captions and
    /// nothing is played back — so a Bluetooth headset used as the mic is never pulled from
    /// high-quality A2DP into hands-free mode by playback, which is what makes its mic drop.
    /// Default true.
    /// </summary>
    public bool SpeakReplies { get; set; } = true;

    /// <summary>Interval (seconds) at which new transcript lines are injected as silent grounding.</summary>
    public int GroundingIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// Explicit consent to stream your microphone audio to the provider. Required by the
    /// pushToTalk and continuous voice modes (hybrid needs only voiceMode != off, since it
    /// transcribes locally and sends text). Default false. Meeting/system audio is never streamed.
    /// </summary>
    public bool SendAudio { get; set; }
}
