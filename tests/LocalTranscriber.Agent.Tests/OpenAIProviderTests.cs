using System.Text.Json;
using LocalTranscriber.Agent;
using LocalTranscriber.Agent.OpenAI;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Agent.Tests;

public class OpenAIProviderTests
{
    private sealed class FakeTransport : IOpenAIHttpTransport
    {
        public string? LastUrl;
        public string? LastApiKey;
        public string? LastBody;
        public string Response = "{}";

        public Task<string> PostJsonAsync(string url, string apiKey, string jsonBody, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            LastUrl = url;
            LastApiKey = apiKey;
            LastBody = jsonBody;
            return Task.FromResult(Response);
        }
    }

    private static AgentProviderRequest MakeRequest(string? question = null) => new()
    {
        WindowEvents = new[]
        {
            new TranscriptEvent("e1", "s1", DateTimeOffset.Parse("2026-07-09T10:00:00Z"),
                new SpeakerLabel("sp1", "Joe", false), AudioSourceType.SystemAudio, "We deploy Friday.")
        },
        ContextSummary = "Project X: QA signoff required before deploys.",
        RunningSummary = "Deployment timing discussion.",
        SessionId = "s1",
        UserQuestion = question
    };

    private static string WrapChatResponse(string content)
        => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    [Fact]
    public async Task RequestBody_ContainsTranscriptContextAndSchema()
    {
        var transport = new FakeTransport { Response = WrapChatResponse("{\"suggestions\":[],\"runningSummaryUpdate\":null}") };
        var provider = new OpenAITextMeetingAgentProvider(new OpenAITextAgentOptions { ApiKey = "test-key", Model = "gpt-5.4-mini" }, transport);

        await provider.AnalyzeAsync(MakeRequest());

        Assert.Equal("test-key", transport.LastApiKey);
        Assert.Contains("We deploy Friday.", transport.LastBody);
        Assert.Contains("QA signoff", transport.LastBody);
        Assert.Contains("json_schema", transport.LastBody);
        Assert.Contains("max_completion_tokens", transport.LastBody);
        // gpt-5.x: no custom temperature
        Assert.DoesNotContain("\"temperature\"", transport.LastBody);
    }

    [Fact]
    public async Task RequestBody_IncludesTemperature_ForNonRestrictedModels()
    {
        var transport = new FakeTransport { Response = WrapChatResponse("{\"suggestions\":[],\"runningSummaryUpdate\":null}") };
        var provider = new OpenAITextMeetingAgentProvider(new OpenAITextAgentOptions { ApiKey = "k", Model = "gpt-4o-mini", Temperature = 0.3 }, transport);

        await provider.AnalyzeAsync(MakeRequest());
        Assert.Contains("\"temperature\":0.3", transport.LastBody);
    }

    [Fact]
    public async Task UserQuestion_IsIncluded()
    {
        var transport = new FakeTransport { Response = WrapChatResponse("{\"suggestions\":[],\"runningSummaryUpdate\":null}") };
        var provider = new OpenAITextMeetingAgentProvider(new OpenAITextAgentOptions { ApiKey = "k" }, transport);

        await provider.AnalyzeAsync(MakeRequest("What did I miss?"));
        Assert.Contains("What did I miss?", transport.LastBody);
    }

    [Fact]
    public async Task ValidResponse_MapsToSuggestions()
    {
        string content = """
            {"suggestions":[{"type":"Risk","priority":"High","title":"Deploy conflict","message":"Friday deploy conflicts with QA signoff rule.","confidence":0.83}],"runningSummaryUpdate":"Team wants Friday deploy."}
            """;
        var transport = new FakeTransport { Response = WrapChatResponse(content) };
        var provider = new OpenAITextMeetingAgentProvider(new OpenAITextAgentOptions { ApiKey = "k" }, transport);

        var result = await provider.AnalyzeAsync(MakeRequest());

        var s = Assert.Single(result.Suggestions);
        Assert.Equal(AgentSuggestionType.Risk, s.Type);
        Assert.Equal(AgentSuggestionPriority.High, s.Priority);
        Assert.Equal("Deploy conflict", s.Title);
        Assert.Equal(0.83, s.Confidence);
        Assert.Equal("openai", s.Source);
        Assert.Equal("Team wants Friday deploy.", result.RunningSummaryUpdate);
    }

    [Fact]
    public async Task MalformedContent_DegradesToEmpty()
    {
        var transport = new FakeTransport { Response = WrapChatResponse("this is not json {{{") };
        var provider = new OpenAITextMeetingAgentProvider(new OpenAITextAgentOptions { ApiKey = "k" }, transport);

        var result = await provider.AnalyzeAsync(MakeRequest());
        Assert.Empty(result.Suggestions);
        Assert.Null(result.RunningSummaryUpdate);
    }

    [Fact]
    public async Task MalformedEnvelope_DegradesToEmpty()
    {
        var transport = new FakeTransport { Response = "garbage" };
        var provider = new OpenAITextMeetingAgentProvider(new OpenAITextAgentOptions { ApiKey = "k" }, transport);

        var result = await provider.AnalyzeAsync(MakeRequest());
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void Factory_FakeByDefault()
    {
        var config = new AppConfig();
        var resolution = AgentProviderFactory.Create(config, new SecretsService(Path.Combine(Path.GetTempPath(), "no-secrets-" + Guid.NewGuid().ToString("N") + ".json")));
        Assert.Equal("fake", resolution.Provider.Name);
        Assert.Null(resolution.Notice);
    }

    [Fact]
    public void Factory_OpenAIWithoutEnable_FallsBackWithNotice()
    {
        var config = new AppConfig();
        config.Agent.Provider = "openai";
        var resolution = AgentProviderFactory.Create(config, new SecretsService(Path.Combine(Path.GetTempPath(), "no-secrets-" + Guid.NewGuid().ToString("N") + ".json")));
        Assert.Equal("fake", resolution.Provider.Name);
        Assert.Contains("not enabled", resolution.Notice);
    }

    [Fact]
    public void Factory_OpenAIEnabledWithoutKey_FallsBackWithNotice()
    {
        var config = new AppConfig();
        config.Agent.Provider = "openai";
        config.Agent.OpenAI.Enabled = true;
        config.Agent.OpenAI.ApiKeyEnvironmentVariable = "LT_TEST_NO_SUCH_ENV_VAR";
        var resolution = AgentProviderFactory.Create(config, new SecretsService(Path.Combine(Path.GetTempPath(), "no-secrets-" + Guid.NewGuid().ToString("N") + ".json")));
        Assert.Equal("fake", resolution.Provider.Name);
        Assert.Contains("no API key", resolution.Notice);
    }

    [Fact]
    public void Secrets_EnvVarTakesPrecedence_AndFileWorks()
    {
        string path = Path.Combine(Path.GetTempPath(), "lt-secrets-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new SecretsService(path);
            service.SaveOpenAIKey("file-key");

            var (key, reason) = service.ResolveOpenAIKey("LT_TEST_NO_SUCH_ENV_VAR");
            Assert.Equal("file-key", key);
            Assert.Null(reason);

            Environment.SetEnvironmentVariable("LT_TEST_SECRET_VAR", "env-key");
            (key, _) = service.ResolveOpenAIKey("LT_TEST_SECRET_VAR");
            Assert.Equal("env-key", key);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LT_TEST_SECRET_VAR", null);
            File.Delete(path);
        }
    }
}
