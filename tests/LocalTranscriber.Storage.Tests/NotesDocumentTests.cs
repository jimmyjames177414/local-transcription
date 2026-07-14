using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage.Tests;

public class NotesDocumentTests
{
    [Fact]
    public void ToMarkdown_RendersAllSectionsWithBullets()
    {
        var doc = new NotesDocument("abc123");
        doc.Add(NoteSection.Decisions, "Deploy Friday (pending tests)");
        doc.Add(NoteSection.ActionItems, "Me — release notes");
        doc.Add(NoteSection.Risks, "Staging down until noon");

        string md = doc.ToMarkdown();

        Assert.Contains("# Meeting notes — abc123", md);
        Assert.Contains("## Decisions", md);
        Assert.Contains("- Deploy Friday (pending tests)", md);
        Assert.Contains("## Action items", md);
        Assert.Contains("- Me — release notes", md);
        Assert.Contains("## Risks", md);
        Assert.Contains("## Notes", md);
    }

    [Fact]
    public void Parse_RoundTripsMarkdown()
    {
        var original = new NotesDocument("s1");
        original.Add(NoteSection.Decisions, "Ship v2");
        original.Add(NoteSection.ActionItems, "Ana — update docs");
        original.Add(NoteSection.Notes, "General remark");

        var parsed = NotesDocument.Parse(original.ToMarkdown());

        Assert.Equal("s1", parsed.SessionId);
        Assert.Equal("Ship v2", Assert.Single(parsed[NoteSection.Decisions]).Text);
        Assert.Equal("Ana — update docs", Assert.Single(parsed[NoteSection.ActionItems]).Text);
        Assert.Equal("General remark", Assert.Single(parsed[NoteSection.Notes]).Text);
        Assert.Empty(parsed[NoteSection.Risks]);
    }

    [Fact]
    public void Parse_ToleratesUnknownContent()
    {
        string md = """
            # Meeting notes — x
            Random prose that is not a bullet.

            ## Something unknown
            - ignored item

            ## Risks
            - real risk
            """;

        var parsed = NotesDocument.Parse(md);

        Assert.Equal("real risk", Assert.Single(parsed[NoteSection.Risks]).Text);
        Assert.Empty(parsed[NoteSection.Decisions]);
    }

    [Theory]
    [InlineData("decisions", NoteSection.Decisions)]
    [InlineData("action_items", NoteSection.ActionItems)]
    [InlineData("Risks", NoteSection.Risks)]
    [InlineData("notes", NoteSection.Notes)]
    [InlineData("bogus", null)]
    public void SectionFromKey_MapsToolArguments(string key, NoteSection? expected)
        => Assert.Equal(expected, NotesDocument.SectionFromKey(key));
}
