namespace LocalTranscriber.Engine;

public sealed record TranscriptionSessionOptions
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    public required string OutputTextPath { get; init; }
    public required string OutputJsonlPath { get; init; }
    public bool EnableMicrophone { get; init; } = true;
    public bool EnableSystemAudio { get; init; } = true;
    public string MicrophoneSpeakerName { get; init; } = "Me";
    public int FakeEventIntervalMs { get; init; } = 2000;

    // Real pipeline settings (ignored by the fake engine)
    public string? WhisperModelPath { get; init; }
    public string? SpeakerModelDir { get; init; }
    public string? Language { get; init; }
    public int ChunkSeconds { get; init; } = 10;
    public int OverlapMs { get; init; } = 500;

    /// <summary>When true, non-speech annotations (e.g. "(engine revving)", "[Music]") are dropped.</summary>
    public bool FilterNonSpeech { get; init; } = true;
}
