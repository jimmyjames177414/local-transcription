using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage;

public sealed class PlainTextTranscriptWriter : ITranscriptWriter
{
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly double _uncertainBelow;

    public PlainTextTranscriptWriter(string path, double uncertainBelow = TranscriptFormatting.DefaultUncertainBelow)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
        _uncertainBelow = uncertainBelow;
    }

    public async Task WriteAsync(TranscriptEvent transcriptEvent, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(TranscriptFormatting.FormatLine(transcriptEvent, _uncertainBelow).AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.FlushAsync().ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _lock.Dispose();
    }
}
