using System.Text.Json;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Storage.Tests;

public class TranscriptWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-tests-" + Guid.NewGuid().ToString("N"));

    public TranscriptWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static TranscriptEvent MakeEvent(
        string text = "Hello there.",
        string speakerName = "Speaker 1",
        bool isKnown = false,
        double? confidence = null)
    {
        return new TranscriptEvent(
            Id: "e1",
            SessionId: "s1",
            Timestamp: new DateTimeOffset(2026, 7, 8, 10, 4, 12, TimeSpan.Zero),
            Speaker: new SpeakerLabel("speaker_1", speakerName, isKnown, confidence),
            Source: AudioSourceType.Microphone,
            Text: text,
            Confidence: 0.91,
            StartMs: 0,
            EndMs: 2500);
    }

    [Fact]
    public void PlainTextFormat_UnknownSpeaker()
    {
        string line = TranscriptFormatting.FormatLine(MakeEvent());
        Assert.EndsWith("Speaker 1: Hello there.", line);
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\] ", line);
    }

    [Fact]
    public void PlainTextFormat_KnownSpeaker_HighConfidence()
    {
        string line = TranscriptFormatting.FormatLine(MakeEvent(speakerName: "Joe", isKnown: true, confidence: 0.95));
        Assert.Contains("] Joe: ", line);
        Assert.DoesNotContain("possibly", line);
    }

    [Fact]
    public void PlainTextFormat_KnownSpeaker_UncertainConfidence()
    {
        string line = TranscriptFormatting.FormatLine(MakeEvent(speakerName: "Joe", isKnown: true, confidence: 0.65));
        Assert.Contains("] possibly Joe: ", line);
    }

    [Fact]
    public async Task PlainTextWriter_CreatesFileAndWritesLine()
    {
        string path = Path.Combine(_dir, "nested", "out.txt");
        await using (var writer = new PlainTextTranscriptWriter(path))
        {
            await writer.WriteAsync(MakeEvent());
        }

        string[] lines = File.ReadAllLines(path);
        Assert.Single(lines);
        Assert.Contains("Speaker 1: Hello there.", lines[0]);
    }

    [Fact]
    public void Jsonl_SerializesExpectedFields()
    {
        string json = JsonlTranscriptWriter.Serialize(MakeEvent());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("s1", root.GetProperty("sessionId").GetString());
        Assert.Equal("2026-07-08T10:04:12.000Z", root.GetProperty("timestamp").GetString());
        Assert.Equal("speaker_1", root.GetProperty("speakerId").GetString());
        Assert.Equal("Speaker 1", root.GetProperty("speakerName").GetString());
        Assert.Equal("microphone", root.GetProperty("source").GetString());
        Assert.Equal("Hello there.", root.GetProperty("text").GetString());
        Assert.Equal(0.91, root.GetProperty("confidence").GetDouble());
        Assert.Equal(0, root.GetProperty("startMs").GetInt64());
        Assert.Equal(2500, root.GetProperty("endMs").GetInt64());
        Assert.DoesNotContain('\n', json);
    }

    [Fact]
    public void Jsonl_OmitsNullOptionalFields()
    {
        var e = MakeEvent() with { Confidence = null, StartMs = null, EndMs = null };
        string json = JsonlTranscriptWriter.Serialize(e);
        Assert.DoesNotContain("confidence", json);
        Assert.DoesNotContain("startMs", json);
    }

    [Fact]
    public async Task CompositeWriter_WritesToBothOutputs()
    {
        string txt = Path.Combine(_dir, "both.txt");
        string jsonl = Path.Combine(_dir, "both.jsonl");
        await using (var writer = new CompositeTranscriptWriter(
            new PlainTextTranscriptWriter(txt),
            new JsonlTranscriptWriter(jsonl)))
        {
            await writer.WriteAsync(MakeEvent());
            await writer.FlushAsync();
        }

        Assert.True(File.Exists(txt));
        Assert.True(File.Exists(jsonl));
        Assert.Single(File.ReadAllLines(txt));
        Assert.Single(File.ReadAllLines(jsonl));
    }
}
