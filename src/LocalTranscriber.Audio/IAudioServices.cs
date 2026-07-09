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

    Task StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
