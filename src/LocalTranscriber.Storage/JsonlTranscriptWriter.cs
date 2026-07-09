using System.Text.Json;
using System.Text.Json.Serialization;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage;

public sealed class JsonlTranscriptWriter : ITranscriptWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonlTranscriptWriter(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
    }

    public static string Serialize(TranscriptEvent e)
    {
        var line = new JsonlLine(
            e.SessionId,
            e.Timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            e.Speaker.SpeakerId,
            e.Speaker.DisplayName,
            SourceToString(e.Source),
            e.Text,
            e.Confidence,
            e.StartMs,
            e.EndMs);
        return JsonSerializer.Serialize(line, JsonOptions);
    }

    private static string SourceToString(AudioSourceType source) => source switch
    {
        AudioSourceType.Microphone => "microphone",
        AudioSourceType.SystemAudio => "systemAudio",
        _ => "unknown"
    };

    public async Task WriteAsync(TranscriptEvent transcriptEvent, CancellationToken cancellationToken = default)
    {
        string json = Serialize(transcriptEvent);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
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

    private sealed record JsonlLine(
        [property: JsonPropertyName("sessionId")] string SessionId,
        [property: JsonPropertyName("timestamp")] string Timestamp,
        [property: JsonPropertyName("speakerId")] string SpeakerId,
        [property: JsonPropertyName("speakerName")] string SpeakerName,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("confidence")] double? Confidence,
        [property: JsonPropertyName("startMs")] long? StartMs,
        [property: JsonPropertyName("endMs")] long? EndMs);
}
