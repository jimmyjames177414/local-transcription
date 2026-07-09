using LocalTranscriber.Shared;

namespace LocalTranscriber.Audio;

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    bool IsInput,
    bool IsDefault
);

public sealed record AudioCaptureOptions
{
    /// <summary>Device id from <see cref="IAudioDeviceService"/>; null uses the default device.</summary>
    public string? DeviceId { get; init; }

    public AudioCaptureOptions Validate()
    {
        return this;
    }
}

public sealed record AudioChunk(
    AudioSourceType Source,
    byte[] Data,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    bool IsIeeeFloat,
    DateTimeOffset CapturedAt)
{
    public int BytesPerSecond => SampleRate * Channels * (BitsPerSample / 8);

    public TimeSpan Duration => BytesPerSecond == 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds((double)Data.Length / BytesPerSecond);

    /// <summary>Timestamp of the first sample in this chunk (CapturedAt marks the chunk end).</summary>
    public DateTimeOffset StartsAt => CapturedAt - Duration;
}
