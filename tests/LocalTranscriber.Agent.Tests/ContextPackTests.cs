using LocalTranscriber.Context;

namespace LocalTranscriber.Agent.Tests;

public class ContextPackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-context-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MarkdownContextPackService _service = new();

    public ContextPackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ContextPackOptions Options(int maxTotal = 20000, int maxPerDoc = 8000) => new()
    {
        ContextFolder = _dir,
        MaxTotalCharacters = maxTotal,
        MaxCharactersPerDocument = maxPerDoc,
        RequiredFiles = new[] { "codename-summary.md" }
    };

    [Fact]
    public async Task Load_ReadsMarkdownWithTitles()
    {
        File.WriteAllText(Path.Combine(_dir, "codename-summary.md"), "# Project Phoenix\nA rebuild of the billing system.");
        File.WriteAllText(Path.Combine(_dir, "people.md"), "# People\n- Joe owns deploys");

        var pack = await _service.LoadAsync(Options());

        Assert.Equal(2, pack.Documents.Count);
        Assert.Equal("Project Phoenix", pack.Documents[0].Title);
        Assert.Contains("billing system", pack.CombinedText);
        Assert.Contains("Joe owns deploys", pack.CombinedText);
        Assert.Empty(pack.Warnings);
    }

    [Fact]
    public async Task MissingRequiredFile_ProducesWarning()
    {
        File.WriteAllText(Path.Combine(_dir, "people.md"), "# People");
        var pack = await _service.LoadAsync(Options());
        Assert.Contains(pack.Warnings, w => w.Contains("codename-summary.md"));
    }

    [Fact]
    public async Task MissingFolder_WarnsInsteadOfThrowing()
    {
        var pack = await _service.LoadAsync(Options() with { ContextFolder = Path.Combine(_dir, "nope") });
        Assert.Empty(pack.Documents);
        Assert.Contains(pack.Warnings, w => w.Contains("not found"));
    }

    [Fact]
    public async Task Budget_TruncatesOversizedDocuments()
    {
        File.WriteAllText(Path.Combine(_dir, "codename-summary.md"), "# Big\n" + new string('x', 50000));

        var pack = await _service.LoadAsync(Options(maxTotal: 1000, maxPerDoc: 1000));

        Assert.Single(pack.Documents);
        Assert.True(pack.Documents[0].Truncated);
        Assert.True(pack.Documents[0].Content.Length <= 1000);
    }

    [Fact]
    public async Task ReadDocument_BlocksTraversalAndNonMarkdown()
    {
        File.WriteAllText(Path.Combine(_dir, "codename-summary.md"), "# OK");
        string outside = Path.Combine(Path.GetTempPath(), "lt-outside-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(outside, "# Secret");

        try
        {
            Assert.Null(await _service.ReadDocumentAsync(Options(), Path.Combine("..", Path.GetFileName(outside))));
            Assert.Null(await _service.ReadDocumentAsync(Options(), outside));
            Assert.Null(await _service.ReadDocumentAsync(Options(), "notes.txt"));
            Assert.NotNull(await _service.ReadDocumentAsync(Options(), "codename-summary.md"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task ListDocuments_IncludesNestedMeetingNotes()
    {
        File.WriteAllText(Path.Combine(_dir, "codename-summary.md"), "# S");
        Directory.CreateDirectory(Path.Combine(_dir, "meeting-notes"));
        File.WriteAllText(Path.Combine(_dir, "meeting-notes", "standup.md"), "# Standup");

        var docs = await _service.ListDocumentsAsync(Options());

        Assert.Contains("codename-summary.md", docs);
        Assert.Contains("meeting-notes/standup.md", docs);
    }

    [Fact]
    public async Task Validate_ReportsProblems()
    {
        var problems = await _service.ValidateAsync(Options());
        Assert.NotEmpty(problems);

        File.WriteAllText(Path.Combine(_dir, "codename-summary.md"), "# OK");
        problems = await _service.ValidateAsync(Options());
        Assert.Empty(problems);
    }
}
