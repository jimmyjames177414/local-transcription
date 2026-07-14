using LocalTranscriber.Agent;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Agent.Tests;

public class TranscriptEventTailerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-tailer-tests-" + Guid.NewGuid().ToString("N"));

    public TranscriptEventTailerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string JsonlPath => Path.Combine(_dir, "t.jsonl");
    private string CheckpointPath => Path.Combine(_dir, "checkpoint.json");

    private static string Line(string text, string speaker = "Speaker 1", string session = "s1", string time = "2026-07-09T10:00:00.000Z")
        => $"{{\"sessionId\":\"{session}\",\"timestamp\":\"{time}\",\"speakerId\":\"sp1\",\"speakerName\":\"{speaker}\",\"source\":\"systemAudio\",\"text\":\"{text}\"}}";

    private async Task<List<TranscriptEvent>> ReadAllAsync(TranscriptTailOptions options, int timeoutMs = 5000)
    {
        var results = new List<TranscriptEvent>();
        using var cts = new CancellationTokenSource(timeoutMs);
        await using var tailer = new TranscriptEventTailer();
        try
        {
            await foreach (var e in tailer.TailAsync(options, cts.Token))
            {
                results.Add(e);
            }
        }
        catch (OperationCanceledException)
        {
        }
        return results;
    }

    [Fact]
    public async Task ReadsExistingLines()
    {
        File.WriteAllLines(JsonlPath, new[] { Line("Hello."), Line("World.", time: "2026-07-09T10:00:01.000Z") });

        var events = await ReadAllAsync(new TranscriptTailOptions { JsonlPath = JsonlPath, FromStart = true, StopAtEndOfFile = true });

        Assert.Equal(2, events.Count);
        Assert.Equal("Hello.", events[0].Text);
        Assert.Equal("Speaker 1", events[0].Speaker.DisplayName);
        Assert.Equal(AudioSourceType.SystemAudio, events[0].Source);
    }

    [Fact]
    public async Task PicksUpAppendedLines()
    {
        File.WriteAllText(JsonlPath, Line("First.") + "\n");

        var results = new List<TranscriptEvent>();
        using var cts = new CancellationTokenSource(8000);
        await using var tailer = new TranscriptEventTailer();
        var options = new TranscriptTailOptions { JsonlPath = JsonlPath, FromStart = true, PollIntervalMs = 50 };

        var readTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in tailer.TailAsync(options, cts.Token))
                {
                    lock (results) { results.Add(e); }
                    if (results.Count >= 2) { cts.Cancel(); }
                }
            }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(300);
        File.AppendAllText(JsonlPath, Line("Second.", time: "2026-07-09T10:00:02.000Z") + "\n");
        await readTask;

        Assert.Equal(2, results.Count);
        Assert.Equal("Second.", results[1].Text);
    }

    [Fact]
    public async Task PartialLine_IsBufferedUntilComplete()
    {
        string full = Line("Complete.");
        File.WriteAllText(JsonlPath, full[..20]); // no newline, mid-JSON

        var results = new List<TranscriptEvent>();
        using var cts = new CancellationTokenSource(8000);
        await using var tailer = new TranscriptEventTailer();

        var readTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in tailer.TailAsync(new TranscriptTailOptions { JsonlPath = JsonlPath, FromStart = true, PollIntervalMs = 50 }, cts.Token))
                {
                    lock (results) { results.Add(e); }
                    cts.Cancel();
                }
            }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(300);
        File.AppendAllText(JsonlPath, full[20..] + "\n");
        await readTask;

        Assert.Single(results);
        Assert.Equal("Complete.", results[0].Text);
    }

    [Fact]
    public async Task Checkpoint_ResumesWhereItLeftOff()
    {
        File.WriteAllLines(JsonlPath, new[] { Line("One."), Line("Two.", time: "2026-07-09T10:00:01.000Z") });

        var first = await ReadAllAsync(new TranscriptTailOptions
        {
            JsonlPath = JsonlPath,
            FromStart = true,
            CheckpointPath = CheckpointPath,
            StopAtEndOfFile = true
        });
        Assert.Equal(2, first.Count);

        File.AppendAllText(JsonlPath, Line("Three.", time: "2026-07-09T10:00:02.000Z") + "\n");

        var second = await ReadAllAsync(new TranscriptTailOptions
        {
            JsonlPath = JsonlPath,
            CheckpointPath = CheckpointPath,
            StopAtEndOfFile = true
        });

        Assert.Single(second);
        Assert.Equal("Three.", second[0].Text);
    }

    [Fact]
    public async Task MissingFile_WaitsWithoutThrowing_AndStopsWhenAsked()
    {
        var events = await ReadAllAsync(new TranscriptTailOptions
        {
            JsonlPath = Path.Combine(_dir, "missing.jsonl"),
            FromStart = true,
            StopAtEndOfFile = true
        });
        Assert.Empty(events);
    }

    [Fact]
    public async Task MalformedLines_AreSkipped()
    {
        File.WriteAllLines(JsonlPath, new[] { "{not json", Line("Good."), "", "{\"missing\":\"fields\"}" });

        var events = await ReadAllAsync(new TranscriptTailOptions { JsonlPath = JsonlPath, FromStart = true, StopAtEndOfFile = true });

        Assert.Single(events);
        Assert.Equal("Good.", events[0].Text);
    }

    [Fact]
    public void Parser_ParsesWriterShape()
    {
        string line = "{\"sessionId\":\"abc\",\"timestamp\":\"2026-07-09T05:05:14.093Z\",\"speakerId\":\"mic\",\"speakerName\":\"Me\",\"source\":\"microphone\",\"text\":\"Hi.\",\"confidence\":0.9,\"startMs\":0,\"endMs\":10}";
        var e = TranscriptEventJsonParser.TryParse(line);
        Assert.NotNull(e);
        Assert.Equal("abc", e!.SessionId);
        Assert.Equal("Me", e.Speaker.DisplayName);
        Assert.Equal(AudioSourceType.Microphone, e.Source);
        Assert.Equal(0.9, e.Confidence);
        Assert.Equal(10, e.EndMs);
        Assert.False(string.IsNullOrEmpty(e.Id));
    }

    [Fact]
    public void Deduplicator_SuppressesRepeats()
    {
        var dedup = new TranscriptEventDeduplicator();
        var e = TranscriptEventJsonParser.TryParse(Line("Same."))!;
        Assert.True(dedup.TryAdd(e));
        Assert.False(dedup.TryAdd(e));
    }
}
