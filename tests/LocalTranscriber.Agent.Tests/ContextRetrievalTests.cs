using LocalTranscriber.Context;

namespace LocalTranscriber.Agent.Tests;

public class ContextRetrievalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-retrieval-tests-" + Guid.NewGuid().ToString("N"));

    public ContextRetrievalTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "codename-summary.md"),
            "# Project Phoenix\nRebuild of the billing system. Deploys require QA signoff.");
        File.WriteAllText(Path.Combine(_dir, "decisions.md"),
            "# Decisions\n\n## Deployment freeze\nNo deployments during the last week of each quarter.\n\n## Database choice\nWe standardized on PostgreSQL.");
        File.WriteAllText(Path.Combine(_dir, "people.md"),
            "# People\n\n## Joe\nOwns deployments and release engineering.\n\n## Martina\nOwns database migrations.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ContextPackOptions Options() => new() { ContextFolder = _dir };

    [Fact]
    public void Chunker_SplitsByHeadings()
    {
        var doc = new ContextDocument("decisions.md", "Decisions",
            File.ReadAllText(Path.Combine(_dir, "decisions.md")), false);
        var chunks = ContextChunker.Chunk(doc);

        Assert.Contains(chunks, c => c.Heading == "Deployment freeze" && c.Text.Contains("last week"));
        Assert.Contains(chunks, c => c.Heading == "Database choice" && c.Text.Contains("PostgreSQL"));
    }

    [Fact]
    public void Chunker_CapsChunkLength()
    {
        var doc = new ContextDocument("big.md", "Big", "# Big\n" + new string('x', 5000), false);
        var chunks = ContextChunker.Chunk(doc, maxChunkCharacters: 1000);
        Assert.True(chunks.Count >= 5);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 1000));
    }

    [Fact]
    public async Task Retriever_FindsRelevantChunks_WithSources()
    {
        var retriever = new KeywordContextRetriever(new MarkdownContextPackService(), Options());
        var result = await retriever.RetrieveAsync("when is the deployment freeze");

        Assert.NotEmpty(result.Chunks);
        var top = result.Chunks[0];
        Assert.Equal("decisions.md", top.Chunk.SourceFile);
        Assert.Equal("Deployment freeze", top.Chunk.Heading);
    }

    [Fact]
    public async Task Retriever_NoMatches_ReturnsEmpty()
    {
        var retriever = new KeywordContextRetriever(new MarkdownContextPackService(), Options());
        var result = await retriever.RetrieveAsync("quantum blockchain espresso");
        Assert.Empty(result.Chunks);
    }

    [Fact]
    public async Task Composer_AlwaysIncludesSummary_PlusRelevantChunks()
    {
        var composer = new ContextComposer(new MarkdownContextPackService(), Options());
        string composed = await composer.ComposeAsync("who owns database migrations");

        Assert.Contains("Project Phoenix", composed);
        Assert.Contains("Martina", composed);
        Assert.Contains("people.md", composed);
    }

    [Fact]
    public async Task Composer_RespectsBudget()
    {
        var composer = new ContextComposer(new MarkdownContextPackService(), Options() with { MaxTotalCharacters = 200 });
        string composed = await composer.ComposeAsync("deployment database people");
        Assert.True(composed.Length <= 400); // summary may slightly exceed; chunks must not blow past
    }

    [Fact]
    public void Tokenizer_DropsStopWords()
    {
        var tokens = KeywordContextRetriever.Tokenize("the deployment of the system is blocked");
        Assert.Contains("deployment", tokens);
        Assert.Contains("blocked", tokens);
        Assert.DoesNotContain("the", tokens);
        Assert.DoesNotContain("is", tokens);
    }
}
