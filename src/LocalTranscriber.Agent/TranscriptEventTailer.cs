using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Agent;

public sealed record TranscriptTailOptions
{
    public required string JsonlPath { get; init; }

    /// <summary>Ignore any saved checkpoint and read from the beginning.</summary>
    public bool FromStart { get; init; }

    /// <summary>Where the offset checkpoint is persisted. Null disables checkpointing.</summary>
    public string? CheckpointPath { get; init; }

    public int PollIntervalMs { get; init; } = 500;

    /// <summary>Stop when the end of file is reached instead of waiting for appends.</summary>
    public bool StopAtEndOfFile { get; init; }
}

public sealed record TranscriptTailCheckpoint(string FilePath, long Offset, string? LastLineHash);

public interface ITranscriptEventTailer : IAsyncDisposable
{
    IAsyncEnumerable<TranscriptEvent> TailAsync(TranscriptTailOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Incrementally reads new transcript events from a live-growing .jsonl file.
/// Never rereads consumed bytes: tracks a byte offset, persists it as a checkpoint,
/// buffers partial trailing lines until the writer completes them, and survives
/// restarts and file truncation/rotation.
/// </summary>
public sealed class TranscriptEventTailer : ITranscriptEventTailer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async IAsyncEnumerable<TranscriptEvent> TailAsync(
        TranscriptTailOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long offset = 0;
        string? lastHash = null;

        if (!options.FromStart && options.CheckpointPath is not null)
        {
            var checkpoint = LoadCheckpoint(options.CheckpointPath);
            if (checkpoint is not null &&
                string.Equals(Path.GetFullPath(checkpoint.FilePath), Path.GetFullPath(options.JsonlPath), StringComparison.OrdinalIgnoreCase))
            {
                offset = checkpoint.Offset;
                lastHash = checkpoint.LastLineHash;
            }
        }

        var pending = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!File.Exists(options.JsonlPath))
            {
                if (options.StopAtEndOfFile)
                {
                    yield break;
                }
                await Task.Delay(options.PollIntervalMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            long length;
            byte[]? newBytes = null;
            using (var stream = new FileStream(options.JsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                length = stream.Length;
                if (length < offset)
                {
                    // File truncated or rotated: start over and drop stale partials.
                    offset = 0;
                    lastHash = null;
                    pending.Clear();
                }

                if (length > offset)
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                    newBytes = new byte[length - offset];
                    int read = await stream.ReadAtLeastAsync(newBytes, newBytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                    if (read < newBytes.Length)
                    {
                        Array.Resize(ref newBytes, read);
                    }
                    offset += read;
                }
            }

            if (newBytes is { Length: > 0 })
            {
                pending.Append(Encoding.UTF8.GetString(newBytes));

                while (TryTakeLine(pending, out string line))
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    string hash = HashLine(line);
                    if (hash == lastHash)
                    {
                        continue; // exact replay of the last delivered line
                    }

                    var transcriptEvent = TranscriptEventJsonParser.TryParse(line);
                    if (transcriptEvent is null)
                    {
                        continue; // malformed line: skip, don't kill the stream
                    }

                    lastHash = hash;
                    if (options.CheckpointPath is not null)
                    {
                        // Checkpoint the offset up to the END of consumed complete lines only.
                        long consumedOffset = offset - Encoding.UTF8.GetByteCount(pending.ToString());
                        SaveCheckpoint(options.CheckpointPath, new TranscriptTailCheckpoint(options.JsonlPath, consumedOffset, lastHash));
                    }

                    yield return transcriptEvent;
                }
            }
            else if (options.StopAtEndOfFile)
            {
                yield break;
            }

            if (newBytes is null || newBytes.Length == 0)
            {
                await Task.Delay(options.PollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool TryTakeLine(StringBuilder pending, out string line)
    {
        for (int i = 0; i < pending.Length; i++)
        {
            if (pending[i] == '\n')
            {
                line = pending.ToString(0, i).TrimEnd('\r');
                pending.Remove(0, i + 1);
                return true;
            }
        }

        line = "";
        return false;
    }

    private static string HashLine(string line)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)))[..16];

    private static TranscriptTailCheckpoint? LoadCheckpoint(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            return JsonSerializer.Deserialize<TranscriptTailCheckpoint>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void SaveCheckpoint(string path, TranscriptTailCheckpoint checkpoint)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(checkpoint, Json));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Parses one transcript .jsonl line (the shape written by JsonlTranscriptWriter:
/// sessionId, timestamp, speakerId, speakerName, source, text, confidence?, startMs?, endMs?).
/// Lines carry no id — a deterministic one is derived from content.
/// </summary>
public static class TranscriptEventJsonParser
{
    public static TranscriptEvent? TryParse(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            string? text = root.TryGetProperty("text", out var t) ? t.GetString() : null;
            string? sessionId = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
            if (text is null || sessionId is null)
            {
                return null;
            }

            DateTimeOffset timestamp = root.TryGetProperty("timestamp", out var ts) &&
                DateTimeOffset.TryParse(ts.GetString(), out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            string speakerId = root.TryGetProperty("speakerId", out var sid) ? sid.GetString() ?? "" : "";
            string speakerName = root.TryGetProperty("speakerName", out var sn) ? sn.GetString() ?? "Unknown" : "Unknown";

            var source = root.TryGetProperty("source", out var src) ? src.GetString() switch
            {
                "microphone" => AudioSourceType.Microphone,
                "systemAudio" => AudioSourceType.SystemAudio,
                _ => AudioSourceType.Unknown
            } : AudioSourceType.Unknown;

            string id = DeriveId(sessionId, timestamp, speakerId, text);

            return new TranscriptEvent(
                Id: id,
                SessionId: sessionId,
                Timestamp: timestamp,
                Speaker: new SpeakerLabel(speakerId, speakerName, IsKnown: false),
                Source: source,
                Text: text,
                Confidence: root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null,
                StartMs: root.TryGetProperty("startMs", out var sm) && sm.ValueKind == JsonValueKind.Number ? sm.GetInt64() : null,
                EndMs: root.TryGetProperty("endMs", out var em) && em.ValueKind == JsonValueKind.Number ? em.GetInt64() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DeriveId(string sessionId, DateTimeOffset timestamp, string speakerId, string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}|{timestamp:o}|{speakerId}|{text}"));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}

/// <summary>
/// Suppresses transcript events already seen (by derived id), bounded memory.
/// </summary>
public sealed class TranscriptEventDeduplicator
{
    private readonly HashSet<string> _seen = new();
    private readonly Queue<string> _order = new();
    private readonly int _capacity;

    public TranscriptEventDeduplicator(int capacity = 5000)
    {
        _capacity = capacity;
    }

    /// <summary>Returns true when the event is new.</summary>
    public bool TryAdd(TranscriptEvent e)
    {
        if (!_seen.Add(e.Id))
        {
            return false;
        }

        _order.Enqueue(e.Id);
        while (_order.Count > _capacity)
        {
            _seen.Remove(_order.Dequeue());
        }
        return true;
    }
}
