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
}
