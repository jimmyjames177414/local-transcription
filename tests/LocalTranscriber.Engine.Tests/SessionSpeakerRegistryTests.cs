using LocalTranscriber.Engine;
using LocalTranscriber.Speakers;

namespace LocalTranscriber.Engine.Tests;

public class SessionSpeakerRegistryTests
{
    private static SpeakerEmbedding Embed(params float[] v) => new(v, v.Length, "test");

    [Theory]
    [InlineData("Speaker 1", "session_speaker_1")]
    [InlineData("Speaker 12", "session_speaker_12")]
    public void LabelToSessionSpeakerId_MatchesLabelConvention(string label, string expected)
    {
        Assert.Equal(expected, SessionSpeakerRegistry.LabelToSessionSpeakerId(label));
    }

    [Fact]
    public void ResolveLabel_KeepsSameVoiceStable_AndSeparatesDistinctVoices()
    {
        var registry = new SessionSpeakerRegistry(sameSpeakerThreshold: 0.60);

        var a1 = registry.ResolveLabel(Embed(1f, 0f, 0f));
        var a2 = registry.ResolveLabel(Embed(0.98f, 0.02f, 0f)); // near-identical -> same speaker
        var b1 = registry.ResolveLabel(Embed(0f, 0f, 1f));       // orthogonal -> new speaker

        Assert.Equal("Speaker 1", a1);
        Assert.Equal("Speaker 1", a2);
        Assert.Equal("Speaker 2", b1);
    }

    [Fact]
    public void Snapshot_ExposesLabelsIdsAndEmbeddingCounts()
    {
        var registry = new SessionSpeakerRegistry(sameSpeakerThreshold: 0.60);
        registry.ResolveLabel(Embed(1f, 0f, 0f));
        registry.ResolveLabel(Embed(0.99f, 0.01f, 0f)); // same speaker, second embedding
        registry.ResolveLabel(Embed(0f, 1f, 0f));       // distinct speaker

        var snapshot = registry.Snapshot();

        Assert.Equal(2, snapshot.Count);
        var first = snapshot.Single(s => s.Label == "Speaker 1");
        Assert.Equal("session_speaker_1", first.SessionSpeakerId);
        Assert.Equal(2, first.EmbeddingCount);
        Assert.Equal(first.EmbeddingCount, first.Embeddings.Count);
    }
}
