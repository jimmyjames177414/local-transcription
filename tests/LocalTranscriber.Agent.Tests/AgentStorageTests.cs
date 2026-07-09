using LocalTranscriber.Agent;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Agent.Tests;

public class AgentStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-agentstore-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteDatabase _db;

    public AgentStorageTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new SqliteDatabase(Path.Combine(_dir, "test.sqlite"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AgentSuggestion Make(string title = "Test suggestion", AgentSuggestionType type = AgentSuggestionType.Risk)
        => new(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: "s1",
            CreatedAt: DateTimeOffset.Now,
            Type: type,
            Priority: AgentSuggestionPriority.High,
            Title: title,
            Message: "Something risky.",
            Source: "fake",
            RelatedSpeaker: "Joe",
            Confidence: 0.8);

    [Fact]
    public async Task Jsonl_AppendsOneObjectPerLine()
    {
        var writer = new JsonlAgentSuggestionWriter(_dir);
        await writer.WriteAsync(Make("First"));
        await writer.WriteAsync(Make("Second"));

        string[] lines = File.ReadAllLines(Path.Combine(_dir, "suggestions.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"title\":\"First\"", lines[0]);
        Assert.Contains("\"type\":\"Risk\"", lines[0]);
    }

    [Fact]
    public async Task Sqlite_InsertRetrieveDismiss()
    {
        var store = new SqliteAgentSuggestionStore(_db);
        var suggestion = Make("Persisted");
        await store.InsertAsync(suggestion);
        await store.InsertAsync(Make("Another"));

        var recent = await store.GetRecentAsync(10);
        Assert.Equal(2, recent.Count);
        Assert.Contains(recent, s => s.Title == "Persisted" && s.Priority == AgentSuggestionPriority.High && s.RelatedSpeaker == "Joe");

        Assert.True(await store.DismissAsync(suggestion.Id));
        recent = await store.GetRecentAsync(10);
        Assert.Single(recent);

        var all = await store.GetRecentAsync(10, includeDismissed: true);
        Assert.Equal(2, all.Count);
        Assert.True(all.Single(s => s.Id == suggestion.Id).IsDismissed);
    }

    [Fact]
    public async Task Sqlite_RunningSummaryRoundTrips()
    {
        var store = new SqliteAgentSuggestionStore(_db);
        Assert.Null(await store.GetRunningSummaryAsync());

        await store.UpdateStateAsync("s1", "We discussed deployment timing.");
        Assert.Equal("We discussed deployment timing.", await store.GetRunningSummaryAsync());

        await store.UpdateStateAsync("s1", "Updated summary.");
        Assert.Equal("Updated summary.", await store.GetRunningSummaryAsync());
    }

    [Fact]
    public async Task Markdown_WritesActionItemsAndRisks()
    {
        var writer = new MarkdownAgentOutputWriter(_dir);
        await writer.WriteAsync(Make("Ship it", AgentSuggestionType.ActionItem));
        await writer.WriteAsync(Make("Danger", AgentSuggestionType.Risk));
        await writer.UpdateSummaryAsync("s1", "Short meeting.");

        Assert.Contains("Ship it", File.ReadAllText(Path.Combine(_dir, "action-items.md")));
        Assert.Contains("Danger", File.ReadAllText(Path.Combine(_dir, "risks.md")));
        Assert.Contains("Short meeting.", File.ReadAllText(Path.Combine(_dir, "meeting-summary.md")));
    }

    [Fact]
    public async Task Composite_FansOutToAllSinks()
    {
        var sqlite = new SqliteAgentSuggestionStore(_db);
        var sink = new CompositeAgentSuggestionSink(_dir, sqlite);

        await sink.WriteAsync(Make("Everywhere", AgentSuggestionType.ActionItem));
        await sink.UpdateSummaryAsync("s1", "Summary text.");

        Assert.Contains("Everywhere", File.ReadAllText(Path.Combine(_dir, "suggestions.jsonl")));
        Assert.Contains("Everywhere", File.ReadAllText(Path.Combine(_dir, "action-items.md")));
        Assert.Single(await sqlite.GetRecentAsync(10));
        Assert.Equal("Summary text.", await sqlite.GetRunningSummaryAsync());
    }
}
