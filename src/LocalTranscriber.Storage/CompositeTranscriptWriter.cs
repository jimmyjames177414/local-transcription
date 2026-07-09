using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage;

public sealed class CompositeTranscriptWriter : ITranscriptWriter
{
    private readonly IReadOnlyList<ITranscriptWriter> _writers;

    public CompositeTranscriptWriter(params ITranscriptWriter[] writers)
    {
        _writers = writers;
    }

    public async Task WriteAsync(TranscriptEvent transcriptEvent, CancellationToken cancellationToken = default)
    {
        foreach (var writer in _writers)
        {
            await writer.WriteAsync(transcriptEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        foreach (var writer in _writers)
        {
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var writer in _writers)
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
