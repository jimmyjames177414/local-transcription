using LocalTranscriber.Shared;

namespace LocalTranscriber.Engine.Tests;

public class NonSpeechFilterTests
{
    [Theory]
    [InlineData("(engine revving)")]
    [InlineData("[Music]")]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("*laughs*")]
    [InlineData("♪")]
    [InlineData("  [Applause]  ")]
    public void StripAnnotations_DropsPureSoundEffects(string input)
    {
        Assert.Equal("", NonSpeechFilter.StripAnnotations(input));
    }

    [Theory]
    [InlineData("Let's move deployment to Friday.", "Let's move deployment to Friday.")]
    [InlineData("Hello (laughs) how are you", "Hello how are you")]
    [InlineData("[Music] Welcome everyone", "Welcome everyone")]
    public void StripAnnotations_KeepsSpokenText(string input, string expected)
    {
        Assert.Equal(expected, NonSpeechFilter.StripAnnotations(input));
    }

    [Fact]
    public void StripAnnotations_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", NonSpeechFilter.StripAnnotations(""));
    }
}
