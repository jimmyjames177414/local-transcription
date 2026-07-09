namespace LocalTranscriber.Shared;

public sealed class AppConfig
{
    public string TranscriptFolder { get; set; } = "output/transcripts";
    public string DatabasePath { get; set; } = "output/localtranscriber.sqlite";
    public string WhisperModelPath { get; set; } = "models/whisper/model.bin";
    public string SpeakerModelPath { get; set; } = "models/speaker";
    public bool EnableMicCapture { get; set; } = true;
    public bool EnableSystemCapture { get; set; } = true;
    public string DefaultMicSpeakerName { get; set; } = "Me";
    public double SpeakerMatchThreshold { get; set; } = 0.72;
    public double SpeakerUncertainThreshold { get; set; } = 0.62;
    public int ChunkSeconds { get; set; } = 10;
    public int OverlapMs { get; set; } = 500;
    public int FlushIntervalMs { get; set; } = 1000;
}
