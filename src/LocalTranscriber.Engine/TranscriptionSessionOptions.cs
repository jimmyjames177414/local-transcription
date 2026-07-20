namespace LocalTranscriber.Engine;

public sealed record TranscriptionSessionOptions
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Resume an existing session — append to its files and reopen the DB row instead of creating a new one.</summary>
    public bool ContinueExisting { get; init; } = false;
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

    // In-session speaker clustering thresholds (independent of enrolled-speaker recognition).
    public double SameSpeakerThreshold { get; init; } = 0.50;
    public double NewSpeakerThreshold { get; init; } = 0.40;

    /// <summary>Enrolled-speaker confidence below which the transcript renders "possibly Name".
    /// Mirrors the recognition match threshold so the label and the recognition agree.</summary>
    public double SpeakerMatchThreshold { get; init; } = 0.72;

    // Decoding tuning
    public int WhisperBeamSize { get; init; } = 5;
    public int WhisperThreads { get; init; } = 0;
    public string? InitialPrompt { get; init; }

    // VAD pre-filter
    public bool EnableVad { get; init; } = true;
    public string? VadModelPath { get; init; }
}
