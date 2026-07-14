using LocalTranscriber.Shared;

namespace LocalTranscriber.Audio;

public interface IAudioDeviceService
{
    IReadOnlyList<AudioDeviceInfo> ListInputDevices();
    IReadOnlyList<AudioDeviceInfo> ListOutputDevices();
}

public interface IAudioCaptureService : IAsyncDisposable
{
    AudioSourceType Source { get; }

    /// <summary>Raised on the capture thread whenever a buffer of audio arrives.</summary>
    event EventHandler<AudioChunk>? ChunkAvailable;

    bool IsCapturing { get; }

    /// <summary>
    /// True when a usable audio endpoint exists for this source. Lets callers skip an
    /// unavailable source instead of faulting when the device is missing. Defaults to true.
    /// </summary>
    bool IsAvailable(AudioCaptureOptions options) => true;

    Task StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
