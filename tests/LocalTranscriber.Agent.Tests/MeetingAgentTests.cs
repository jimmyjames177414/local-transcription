using LocalTranscriber.Agent;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Agent.Tests;

public class MeetingAgentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-agent-tests-" + Guid.NewGuid().ToString("N"));

    public MeetingAgentTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "context"));
        File.WriteAllText(Path.Combine(_dir, "context", "codename-summary.md"), "# Test Project\nDeploys happen only after QA signoff.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string JsonlPath => Path.Combine(_dir, "t.jsonl");

    private static string Line(string text, string speaker = "Speaker 1", string time = "2026-07-09T10:00:00.000Z")
        => $"{{\"sessionId\":\"s1\",\"timestamp\":\"{time}\",\"speakerId\":\"sp1\",\"speakerName\":\"{speaker}\",\"source\":\"systemAudio\",\"text\":\"{text}\"}}";

    private MeetingAgentOptions MakeOptions() => new()
    {
        TranscriptJsonlPath = JsonlPath,
        ContextFolder = Path.Combine(_dir, "context"),
        AgentOutputFolder = Path.Combine(_dir, "agent-out"),
        SuggestionIntervalSeconds = 1,
        RollingWindowMinutes = 60
    };

    [Fact]
    public async Task Agent_GeneratesSuggestions_FromKeywords()
    {
        File.WriteAllLines(JsonlPath, new[]
        {
            Line("We plan to deploy on Friday."),
            Line("I am blocked on the database migration.", "Speaker 2", "2026-07-09T10:00:05.000Z")
        });

        await using var agent = new MeetingAgent(new FakeMeetingAgentProvider());
        await agent.StartAsync(MakeOptions());

        var collected = new List<AgentSuggestion>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var s in agent.StreamSuggestionsAsync(cts.Token))
        {
            collected.Add(s);
            if (collected.Any(x => x.Type == AgentSuggestionType.Blocker) &&
                collected.Any(x => x.Type == AgentSuggestionType.ActionItem))
            {
                break;
            }
        }

        await agent.StopAsync();

        Assert.Contains(collected, s => s.Type == AgentSuggestionType.ActionItem);
        Assert.Contains(collected, s => s.Type == AgentSuggestionType.Blocker && s.Priority == AgentSuggestionPriority.High);

        var status = await agent.GetStatusAsync();
        Assert.Equal(MeetingAgentState.Stopped, status.State);
        Assert.Equal(2, status.EventsSeen);
        Assert.True(status.SuggestionsEmitted >= 2);
        Assert.NotEmpty(status.RunningSummary);
    }

    [Fact]
    public async Task Agent_SuppressesDuplicateSuggestions()
    {
        File.WriteAllLines(JsonlPath, new[]
        {
            Line("We should deploy this."),
            Line("Deploy it again I say.", time: "2026-07-09T10:00:05.000Z")
        });

        await using var agent = new MeetingAgent(new FakeMeetingAgentProvider());
        await agent.StartAsync(MakeOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var collected = new List<AgentSuggestion>();
        try
        {
            await foreach (var s in agent.StreamSuggestionsAsync(cts.Token))
            {
                collected.Add(s);
            }
        }
        catch (OperationCanceledException)
        {
        }

        await agent.StopAsync();

        // Both lines trip "deploy" but title dedup allows only one ActionItem "Deployment mentioned".
        Assert.Single(collected.Where(s => s.Title == "Deployment mentioned"));
    }

    [Fact]
    public async Task Agent_StartWhileRunning_Throws_And_StopIsIdempotent()
    {
        File.WriteAllText(JsonlPath, Line("hello") + "\n");
        await using var agent = new MeetingAgent(new FakeMeetingAgentProvider());
        await agent.StartAsync(MakeOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.StartAsync(MakeOptions()));

        await agent.StopAsync();
        await agent.StopAsync();
        Assert.Equal(MeetingAgentState.Stopped, (await agent.GetStatusAsync()).State);
    }

    [Fact]
    public async Task Agent_ModeOff_RefusesToStart()
    {
        await using var agent = new MeetingAgent(new FakeMeetingAgentProvider());
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.StartAsync(MakeOptions() with { Mode = AgentMode.Off }));
    }

    [Fact]
    public async Task Agent_WritesToSink()
    {
        File.WriteAllText(JsonlPath, Line("We are blocked on approvals.") + "\n");
        string outFolder = Path.Combine(_dir, "agent-out");
        var sink = new CompositeAgentSuggestionSink(outFolder);

        await using var agent = new MeetingAgent(new FakeMeetingAgentProvider(), sink: sink);
        await agent.StartAsync(MakeOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var _ in agent.StreamSuggestionsAsync(cts.Token))
        {
            break;
        }
        await agent.StopAsync();

        string jsonl = Path.Combine(outFolder, "suggestions.jsonl");
        Assert.True(File.Exists(jsonl));
        Assert.Contains("Blocker", File.ReadAllText(jsonl));
        Assert.True(File.Exists(Path.Combine(outFolder, "risks.md")));
    }

    [Fact]
    public async Task Ask_ReturnsAnswerEvenWithRepeatTitles()
    {
        File.WriteAllText(JsonlPath, Line("hello") + "\n");
        await using var agent = new MeetingAgent(new FakeMeetingAgentProvider());
        await agent.StartAsync(MakeOptions());
        await Task.Delay(1500); // let the tailer ingest

        var first = await agent.AskAsync("What did I miss?");
        var second = await agent.AskAsync("What did I miss again?");
        await agent.StopAsync();

        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.Equal(AgentSuggestionType.SuggestedResponse, first[0].Type);
    }

    [Fact]
    public void RollingWindow_TrimsByCountAndAge()
    {
        var window = new RollingTranscriptWindow(TimeSpan.FromMinutes(5), maxEvents: 3);
        var t0 = DateTimeOffset.Parse("2026-07-09T10:00:00Z");

        for (int i = 0; i < 5; i++)
        {
            window.Add(new TranscriptEvent($"e{i}", "s", t0.AddSeconds(i), new SpeakerLabel("x", "X", false), AudioSourceType.Unknown, $"line {i}"));
        }
        Assert.Equal(3, window.Count);

        window.Add(new TranscriptEvent("late", "s", t0.AddMinutes(10), new SpeakerLabel("x", "X", false), AudioSourceType.Unknown, "much later"));
        Assert.Equal(1, window.Count); // older ones aged out
    }
}
