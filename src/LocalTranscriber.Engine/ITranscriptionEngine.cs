using LocalTranscriber.Shared;

namespace LocalTranscriber.Engine;

public interface ITranscriptionEngine
{
    Task StartAsync(TranscriptionSessionOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task<TranscriptionSessionStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<TranscriptEvent> StreamEventsAsync(CancellationToken cancellationToken = default);
}
