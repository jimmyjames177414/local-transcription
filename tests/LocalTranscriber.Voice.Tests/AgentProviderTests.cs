using LocalTranscriber.Shared;

namespace LocalTranscriber.Voice.Tests;

public class AgentProviderTests
{
    [Theory]
    [InlineData("openai", AgentProvider.OpenAI)]
    [InlineData("claude-cli", AgentProvider.ClaudeCli)]
    [InlineData("hybrid", AgentProvider.Hybrid)]
    public void Parse_KnownStrings_ReturnCorrectProvider(string input, AgentProvider expected)
        => Assert.Equal(expected, AgentProviders.Parse(input));

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("  ")]
    public void Parse_UnknownOrEmpty_FallsBackToOpenAi(string input)
        => Assert.Equal(AgentProvider.OpenAI, AgentProviders.Parse(input));

    [Fact]
    public void Parse_Null_FallsBackToOpenAi()
        => Assert.Equal(AgentProvider.OpenAI, AgentProviders.Parse(null));

    [Theory]
    [InlineData("OPENAI", AgentProvider.OpenAI)]
    [InlineData("Claude-Cli", AgentProvider.ClaudeCli)]
    [InlineData("HYBRID", AgentProvider.Hybrid)]
    public void Parse_CaseInsensitive(string input, AgentProvider expected)
        => Assert.Equal(expected, AgentProviders.Parse(input));

    [Theory]
    [InlineData("openai", AgentProvider.OpenAI, true)]
    [InlineData("openai", AgentProvider.ClaudeCli, false)]
    [InlineData("claude-cli", AgentProvider.ClaudeCli, true)]
    [InlineData("claude-cli", AgentProvider.Hybrid, false)]
    [InlineData("hybrid", AgentProvider.Hybrid, true)]
    [InlineData("hybrid", AgentProvider.OpenAI, false)]
    [InlineData("unknown", AgentProvider.OpenAI, true)]  // unknown falls back to OpenAI
    [InlineData("unknown", AgentProvider.ClaudeCli, false)]
    public void Is_ReturnsCorrectBoolean(string configValue, AgentProvider provider, bool expected)
        => Assert.Equal(expected, AgentProviders.Is(configValue, provider));
}
