using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage.Tests;

public class MinutesExporterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "minutes-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private static readonly DateTimeOffset Start = new(2026, 7, 10, 10, 0, 0, TimeSpan.Zero);

    private static SessionRecord Session(DateTimeOffset? ended = null)
        => new("abc123", Start, ended ?? Start.AddMinutes(42), "t.txt", "t.jsonl", "stopped");

    private static TranscriptEvent Line(string speaker, string text, int secondsIn, bool known = true)
        => new(Guid.NewGuid().ToString("N"), "abc123", Start.AddSeconds(secondsIn),
            new SpeakerLabel(speaker, speaker, known), AudioSourceType.SystemAudio, text);

    [Fact]
    public void Render_Frontmatter_WithNotes()
    {
        var notes = new NotesDocument("abc123");
        notes.Add(NoteSection.Decisions, "Deploy Friday");
        notes.Add(NoteSection.ActionItems, "Martina — tests by standup");
        notes.Add(NoteSection.ActionItems, "release notes before deploy");

        string md = MinutesExporter.Render(Session(), Array.Empty<TranscriptEvent>(), notes, title: null);

        Assert.StartsWith("---", md);
        Assert.Contains("title: \"Meeting 2026-07-10", md);
        Assert.Contains("type: meeting", md);
        Assert.Contains("duration: 42m", md);
        Assert.Contains("  - assignee: \"Martina\"", md);
        Assert.Contains("    task: \"tests by standup\"", md);
        Assert.Contains("    status: open", md);
        Assert.Contains("  - assignee: \"\"", md); // no em-dash owner
        Assert.Contains("  - text: \"Deploy Friday\"", md);
    }

    [Fact]
    public void Render_Frontmatter_WithoutNotes_UsesEmptyLists()
    {
        string md = MinutesExporter.Render(Session(), Array.Empty<TranscriptEvent>(), notes: null, title: null);

        Assert.Contains("action_items: []", md);
        Assert.Contains("decisions: []", md);
        Assert.Contains("_No summary was generated for this session._", md);
    }

    [Fact]
    public void Render_Transcript_UsesOffsetsFromSessionStart()
    {
        var events = new[]
        {
            Line("Joe", "Let's move deployment to Friday.", 23),
            Line("Martina", "I need to check the test results.", 95)
        };

        string md = MinutesExporter.Render(Session(), events, null, null);

        Assert.Contains("[Joe 0:23] Let's move deployment to Friday.", md);
        Assert.Contains("[Martina 1:35] I need to check the test results.", md);
    }

    [Fact]
    public void Render_Summary_ComesFromRisksAndNotes()
    {
        var notes = new NotesDocument("abc123");
        notes.Add(NoteSection.Risks, "Staging down until noon");
        notes.Add(NoteSection.Notes, "General remark");

        string md = MinutesExporter.Render(Session(), Array.Empty<TranscriptEvent>(), notes, null);

        Assert.Contains("- Risk: Staging down until noon", md);
        Assert.Contains("- General remark", md);
        Assert.DoesNotContain("_No summary was generated", md);
    }

    [Fact]
    public void Render_NullEndedAt_FallsBackToLastEventTimestamp()
    {
        var session = new SessionRecord("abc123", Start, EndedAt: null, "t.txt", "t.jsonl", "faulted");
        var events = new[] { Line("Joe", "hi", 600) };

        string md = MinutesExporter.Render(session, events, null, null);

        Assert.Contains("duration: 10m", md);
    }

    [Fact]
    public void Render_QuotesAndEscapesYamlStrings()
    {
        var notes = new NotesDocument("abc123");
        notes.Add(NoteSection.Decisions, "Use \"quoted\" names");

        string md = MinutesExporter.Render(Session(), Array.Empty<TranscriptEvent>(), notes, null);

        Assert.Contains("- text: \"Use \\\"quoted\\\" names\"", md);
    }

    [Fact]
    public void Export_WritesFile_AndAvoidsCollisions()
    {
        string first = MinutesExporter.Export(Session(), Array.Empty<TranscriptEvent>(), null, _folder);
        string second = MinutesExporter.Export(Session(), Array.Empty<TranscriptEvent>(), null, _folder);

        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.NotEqual(first, second);
        Assert.EndsWith("-2.md", second);
        Assert.Contains("2026-07-10-meeting-abc123", Path.GetFileName(first));
    }

    [Fact]
    public void Export_ExpandsTildeToUserProfile()
    {
        // Only checks the expansion logic indirectly: a relative "~/..." must land under the profile.
        string sub = "minutes-tests-" + Guid.NewGuid().ToString("N");
        string path = MinutesExporter.Export(Session(), Array.Empty<TranscriptEvent>(), null, "~/" + sub);
        try
        {
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }
}
