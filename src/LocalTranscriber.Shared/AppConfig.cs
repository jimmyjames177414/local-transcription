namespace LocalTranscriber.Shared;

public sealed class AppConfig
{
    public string TranscriptFolder { get; set; } = "output/transcripts";
    public string DatabasePath { get; set; } = "output/localtranscriber.sqlite";
    public string WhisperModelPath { get; set; } = "models/whisper/ggml-small.en.bin";
    public string Language { get; set; } = "en";
    public string SpeakerModelPath { get; set; } = "models/speaker";
    public bool EnableMicCapture { get; set; } = true;
    public bool EnableSystemCapture { get; set; } = true;
    public string DefaultMicSpeakerName { get; set; } = "Me";
    public double SpeakerMatchThreshold { get; set; } = 0.72;
    public double SpeakerUncertainThreshold { get; set; } = 0.62;
    // In-session (unnamed) speaker clustering — independent of the enrolled-speaker thresholds above.
    public double SameSpeakerThreshold { get; set; } = 0.50;
    public double NewSpeakerThreshold { get; set; } = 0.40;
    public int ChunkSeconds { get; set; } = 10;
    public int OverlapMs { get; set; } = 500;
    public bool FilterNonSpeech { get; set; } = true;
    // Decoding tuning (A3)
    public int WhisperBeamSize { get; set; } = 5;
    public int WhisperThreads { get; set; } = 0;
    // Optional whisper priming. Empty by default: this app spans many meeting types, and a global
    // prompt gives only a marginal style nudge while risking prompt-echo hallucinations. Meaningful
    // priming is meeting-specific vocabulary (names/acronyms) and belongs in a per-session override.
    public string InitialPrompt { get; set; } = "";

    // VAD pre-filter (A4)
    public bool EnableVad { get; set; } = true;
    public string VadModelPath { get; set; } = "models/whisper/ggml-silero-v5.1.2.bin";

    public AgentConfig Agent { get; set; } = new();
    public MinutesExportConfig MinutesExport { get; set; } = new();
}

/// <summary>
/// Export finished sessions as markdown for the "minutes" tool (github.com/silverstein/minutes).
/// Local file output only — nothing is uploaded.
/// </summary>
public sealed class MinutesExportConfig
{
    /// <summary>Write a minutes-format markdown file when a session stops.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Destination folder; "~" expands to the user profile (minutes watches ~/meetings).</summary>
    public string Folder { get; set; } = "~/meetings";
}
