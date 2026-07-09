using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage;

public interface ITranscriptWriter : IAsyncDisposable
{
    Task WriteAsync(TranscriptEvent transcriptEvent, CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
}
