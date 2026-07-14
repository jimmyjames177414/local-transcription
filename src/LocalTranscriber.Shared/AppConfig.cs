namespace LocalTranscriber.Shared;

public sealed class AppConfig
{
    public string TranscriptFolder { get; set; } = "output/transcripts";
    public string DatabasePath { get; set; } = "output/localtranscriber.sqlite";
    public string WhisperModelPath { get; set; } = "models/whisper/ggml-base.en.bin";
    public string SpeakerModelPath { get; set; } = "models/speaker";
    public bool EnableMicCapture { get; set; } = true;
    public bool EnableSystemCapture { get; set; } = true;
    public string DefaultMicSpeakerName { get; set; } = "Me";
    public double SpeakerMatchThreshold { get; set; } = 0.72;
    public double SpeakerUncertainThreshold { get; set; } = 0.62;
    public int ChunkSeconds { get; set; } = 10;
    public int OverlapMs { get; set; } = 500;
    public int FlushIntervalMs { get; set; } = 1000;
    public bool FilterNonSpeech { get; set; } = true;
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
