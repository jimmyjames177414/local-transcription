using System.Text.Json;
using LocalTranscriber.Agent;
using LocalTranscriber.Agent.OpenAI;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Agent.Tests;

public class RealtimeProviderTests
{
    private sealed class FakeRealtimeTransport : IRealtimeTransport
    {
        public List<string> Sent { get; } = new();
        public Queue<string> ServerEvents { get; } = new();
        public int ConnectCalls;
        public bool DropAfterNextSend;

        public bool IsConnected { get; private set; }

        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            IsConnected = true;
            LastUri = uri;
            LastHeaders = headers;
            return Task.CompletedTask;
        }

        public Uri? LastUri;
        public IReadOnlyDictionary<string, string>? LastHeaders;

        public Task SendAsync(string json, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("not connected");
            }

            Sent.Add(json);
            if (DropAfterNextSend)
            {
                DropAfterNextSend = false;
                IsConnected = false;
            }
            return Task.CompletedTask;
        }

        public Task<string?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult<string?>(ServerEvents.Count > 0 ? ServerEvents.Dequeue() : null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string ResponseDone(string content)
        => JsonSerializer.Serialize(new
        {
            type = "response.done",
            response = new
            {
                output = new[]
                {
                    new { content = new[] { new { type = "text", text = content } } }
                }
            }
        });

    private static AgentProviderRequest MakeRequest(params string[] lines)
    {
        var events = lines.Select((text, i) => new TranscriptEvent(
            $"e{i}-{text.GetHashCode():x}", "s1", DateTimeOffset.Parse("2026-07-09T10:00:00Z").AddSeconds(i),
            new SpeakerLabel("sp1", "Joe", false), AudioSourceType.SystemAudio, text)).ToArray();
        return new AgentProviderRequest
        {
            WindowEvents = events,
            ContextSummary = "Project X context.",
            SessionId = "s1"
        };
    }

    private const string ValidContent = "{\"suggestions\":[{\"type\":\"Risk\",\"priority\":\"High\",\"title\":\"T\",\"message\":\"M\",\"confidence\":0.9}],\"runningSummaryUpdate\":\"Sum.\"}";

    [Fact]
    public async Task Connects_ConfiguresSession_SendsTextEvents()
    {
        var transport = new FakeRealtimeTransport();
        transport.ServerEvents.Enqueue(ResponseDone(ValidContent));
        var provider = new OpenAIRealtimeMeetingAgentProvider(
            new RealtimeConnectionOptions { ApiKey = "test-key", Model = "gpt-realtime-2.1-mini" },
            () => transport);

        var result = await provider.AnalyzeAsync(MakeRequest("We deploy Friday."));

        Assert.Equal(1, transport.ConnectCalls);
        Assert.Contains("model=gpt-realtime-2.1-mini", transport.LastUri!.Query);
        Assert.Equal("Bearer test-key", transport.LastHeaders!["Authorization"]);

        Assert.Contains(transport.Sent, s => s.Contains("session.update") && s.Contains("\"output_modalities\":[\"text\"]"));
        Assert.Contains(transport.Sent, s => s.Contains("conversation.item.create") && s.Contains("We deploy Friday."));
        Assert.Contains(transport.Sent, s => s.Contains("response.create"));

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal("realtime", suggestion.Source);
        Assert.Equal(AgentSuggestionType.Risk, suggestion.Type);
        Assert.Equal("Sum.", result.RunningSummaryUpdate);
    }

    [Fact]
    public async Task SecondAnalyze_SendsOnlyNewEvents()
    {
        var transport = new FakeRealtimeTransport();
        transport.ServerEvents.Enqueue(ResponseDone(ValidContent));
        transport.ServerEvents.Enqueue(ResponseDone(ValidContent));
        var provider = new OpenAIRealtimeMeetingAgentProvider(new RealtimeConnectionOptions { ApiKey = "k" }, () => transport);

        await provider.AnalyzeAsync(MakeRequest("Line one."));
        int sentAfterFirst = transport.Sent.Count;
        await provider.AnalyzeAsync(MakeRequest("Line one.", "Line two."));

        string secondItem = transport.Sent.Skip(sentAfterFirst).First(s => s.Contains("conversation.item.create"));
        Assert.Contains("Line two.", secondItem);
        Assert.DoesNotContain("Line one.", secondItem);
    }

    [Fact]
    public async Task NoNewEvents_SkipsNetworkRoundTrip()
    {
        var transport = new FakeRealtimeTransport();
        transport.ServerEvents.Enqueue(ResponseDone(ValidContent));
        var provider = new OpenAIRealtimeMeetingAgentProvider(new RealtimeConnectionOptions { ApiKey = "k" }, () => transport);

        await provider.AnalyzeAsync(MakeRequest("Only line."));
        int sentAfterFirst = transport.Sent.Count;

        var result = await provider.AnalyzeAsync(MakeRequest("Only line."));
        Assert.Empty(result.Suggestions);
        Assert.Equal(sentAfterFirst, transport.Sent.Count);
    }

    [Fact]
    public async Task ErrorEvent_TriggersReconnectAndRetry()
    {
        int factoryCalls = 0;
        var transports = new List<FakeRealtimeTransport>();
        var provider = new OpenAIRealtimeMeetingAgentProvider(
            new RealtimeConnectionOptions { ApiKey = "k", MaxReconnectAttempts = 2 },
            () =>
            {
                factoryCalls++;
                var t = new FakeRealtimeTransport();
                if (factoryCalls == 1)
                {
                    t.ServerEvents.Enqueue("{\"type\":\"error\",\"error\":{\"message\":\"boom\"}}");
                }
                else
                {
                    t.ServerEvents.Enqueue(ResponseDone(ValidContent));
                }
                transports.Add(t);
                return t;
            });

        var result = await provider.AnalyzeAsync(MakeRequest("Hello."));

        Assert.Equal(2, factoryCalls);
        Assert.Single(result.Suggestions);
        // Retry resent the transcript line on the new connection (no silent loss).
        Assert.Contains(transports[1].Sent, s => s.Contains("Hello."));
    }

    [Fact]
    public void Mapper_ExtractsOutputTextVariants()
    {
        var done = RealtimeEventMapper.Map(ResponseDone("payload"));
        Assert.Equal(RealtimeEventKind.ResponseDone, done.Kind);
        Assert.Equal("payload", done.Text);

        var outputText = RealtimeEventMapper.Map("""
            {"type":"response.done","response":{"output":[{"content":[{"type":"output_text","text":"alt"}]}]}}
            """);
        Assert.Equal("alt", outputText.Text);

        var error = RealtimeEventMapper.Map("{\"type\":\"error\",\"error\":{\"message\":\"bad\"}}");
        Assert.Equal(RealtimeEventKind.Error, error.Kind);
        Assert.Equal("bad", error.Text);

        Assert.Equal(RealtimeEventKind.Other, RealtimeEventMapper.Map("{\"type\":\"response.output_text.delta\"}").Kind);
        Assert.Equal(RealtimeEventKind.Other, RealtimeEventMapper.Map("not json").Kind);
    }

    [Fact]
    public void Factory_RealtimeDisabledByDefault_AndSendAudioBlocked()
    {
        var noSecrets = new SecretsService(Path.Combine(Path.GetTempPath(), "none-" + Guid.NewGuid().ToString("N") + ".json"));

        var config = new AppConfig();
        config.Agent.Provider = "realtime";
        var resolution = AgentProviderFactory.Create(config, noSecrets);
        Assert.Equal("fake", resolution.Provider.Name);
        Assert.Contains("not enabled", resolution.Notice);

        config.Agent.Realtime.Enabled = true;
        config.Agent.Realtime.SendAudio = true;
        resolution = AgentProviderFactory.Create(config, noSecrets);
        Assert.Equal("fake", resolution.Provider.Name);
        Assert.Contains("audio", resolution.Notice, StringComparison.OrdinalIgnoreCase);
    }
}
