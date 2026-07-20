namespace LocalTranscriber.Engine.Tests;

public class TranscriptStitcherTests
{
    [Fact]
    public void TrimOverlap_RemovesRepeatedBoundaryPhrase()
    {
        string prev = "how many reps if you do at 135 and then I was just trying to mess";
        string next = "just trying to mess around to seek in expectations";
        Assert.Equal("around to seek in expectations", TranscriptStitcher.TrimOverlap(prev, next));
    }

    [Fact]
    public void TrimOverlap_IgnoresCaseAndPunctuation()
    {
        string prev = "Let's pin down the exact number.";
        string next = "Exact number, so it's logged.";
        Assert.Equal("so it's logged.", TranscriptStitcher.TrimOverlap(prev, next));
    }

    [Fact]
    public void TrimOverlap_FullRepeatCollapsesToEmpty()
    {
        Assert.Equal("", TranscriptStitcher.TrimOverlap("and like, share.", "and like, share."));
    }

    [Fact]
    public void TrimOverlap_NoOverlap_ReturnsNextUnchanged()
    {
        string prev = "So I can take a look at it.";
        string next = "Checked. Yep, and again.";
        Assert.Equal(next, TranscriptStitcher.TrimOverlap(prev, next));
    }

    [Fact]
    public void TrimOverlap_DoesNotTrimSingleShortCommonWord()
    {
        // "and" is a coincidental one-word match, not a boundary echo — keep it.
        string prev = "resolved and";
        string next = "and again I think all of these are minor";
        Assert.Equal(next, TranscriptStitcher.TrimOverlap(prev, next));
    }

    [Fact]
    public void TrimOverlap_TrimsLongSingleWord()
    {
        // "highlighted" (11 chars) is distinctive enough to trim as a genuine boundary echo.
        string prev = "We'll see you highlighted";
        string next = "highlighted text and logs";
        Assert.Equal("text and logs", TranscriptStitcher.TrimOverlap(prev, next));
    }

    [Fact]
    public void TrimOverlap_DoesNotTrimShortWordThatBeginsNewSentence()
    {
        // "this" (4 chars) can legitimately start the next sentence — dropping it would lose content.
        string prev = "Let's move on, we should discuss this";
        string next = "This week we need to ship the build";
        Assert.Equal(next, TranscriptStitcher.TrimOverlap(prev, next));
    }

    [Theory]
    [InlineData("", "hello there")]
    [InlineData("hello there", "")]
    public void TrimOverlap_EmptyInput_ReturnsNext(string prev, string next)
    {
        Assert.Equal(next, TranscriptStitcher.TrimOverlap(prev, next));
    }
}
