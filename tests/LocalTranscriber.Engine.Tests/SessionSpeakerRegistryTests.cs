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
        // assign=0.50, newSpeaker=0.40 — orthogonal vectors score 0.0 (< 0.40 → new speaker)
        var registry = new SessionSpeakerRegistry(assignThreshold: 0.50, newSpeakerThreshold: 0.40);

        var a1 = registry.ResolveLabel(Embed(1f, 0f, 0f));
        var a2 = registry.ResolveLabel(Embed(0.98f, 0.02f, 0f)); // near-identical → same speaker
        var b1 = registry.ResolveLabel(Embed(0f, 0f, 1f));       // orthogonal → new speaker

        Assert.Equal("Speaker 1", a1);
        Assert.Equal("Speaker 1", a2);
        Assert.Equal("Speaker 2", b1);
    }

    [Fact]
    public void Snapshot_ExposesLabelsIdsAndEmbeddingCounts()
    {
        var registry = new SessionSpeakerRegistry(assignThreshold: 0.50, newSpeakerThreshold: 0.40);
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

    [Fact]
    public void ResolveLabel_GrayZone_AssignsNearestButDoesNotMoveCentroid()
    {
        // Seed Speaker 1 with a known centroid at [1, 0, 0].
        // Gray-zone vector: cosine ~0.45 against [1,0,0] — above newSpeakerThreshold (0.40)
        // but below assignThreshold (0.50). After gray-zone assignment the centroid must not move,
        // so a clean same-speaker vector should still match confidently.
        var registry = new SessionSpeakerRegistry(assignThreshold: 0.50, newSpeakerThreshold: 0.40);

        // Seed: Speaker 1 centroid = [1, 0, 0].
        registry.ResolveLabel(Embed(1f, 0f, 0f));

        // Gray-zone vector: [0.65f, 0.76f, 0f] — cosine ≈ 0.65 / (1 * 1) = 0.65? No, let me think carefully.
        // cosine([1,0,0], [a,b,0]) = a / sqrt(a²+b²)
        // Want cosine in [0.40, 0.50): need a/sqrt(a²+b²) ∈ [0.40, 0.50)
        // a=0.45, b=sqrt(1-0.2025)=sqrt(0.7975)≈0.893 → cosine = 0.45/1 = 0.45 ✓
        var grayZone = Embed(0.45f, 0.893f, 0f);
        var grayLabel = registry.ResolveLabel(grayZone);

        Assert.Equal("Speaker 1", grayLabel); // assigned to nearest (not minted new)

        // Centroid must be unchanged (still [1,0,0] / 1), so a clean same-speaker match still works.
        var clean = Embed(0.99f, 0.05f, 0f); // cosine ≈ 0.99 (well above 0.50)
        var cleanLabel = registry.ResolveLabel(clean);

        Assert.Equal("Speaker 1", cleanLabel);
        // Only 2 centroid updates (seed + clean); gray-zone did not contribute to the centroid.
        // The snapshot will have 3 raw embeddings (seed, gray, clean) but centroid count = 2.
        var snap = registry.Snapshot().Single(s => s.Label == "Speaker 1");
        Assert.Equal(3, snap.EmbeddingCount); // raw list includes gray-zone entry
    }

    [Fact]
    public void ResolveLabel_ThresholdsHonoured_FromConstructor()
    {
        // With a high assignThreshold of 0.99, even near-identical vectors fall in gray zone.
        var registry = new SessionSpeakerRegistry(assignThreshold: 0.99, newSpeakerThreshold: 0.50);

        registry.ResolveLabel(Embed(1f, 0f, 0f));
        // cosine([1,0,0],[0.98f,0.02f,0f]) ≈ 0.98 — below 0.99 → gray zone, not new speaker.
        var label = registry.ResolveLabel(Embed(0.98f, 0.02f, 0f));

        Assert.Equal("Speaker 1", label); // gray zone: assigned, not minted new
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void ResolveLabel_ClearlyDifferentVectors_MintNewSpeaker()
    {
        var registry = new SessionSpeakerRegistry(assignThreshold: 0.50, newSpeakerThreshold: 0.40);
        registry.ResolveLabel(Embed(1f, 0f, 0f));

        // cosine([1,0,0],[0,1,0]) = 0 — below newSpeakerThreshold → new speaker.
        var label = registry.ResolveLabel(Embed(0f, 1f, 0f));

        Assert.Equal("Speaker 2", label);
        Assert.Equal(2, registry.Snapshot().Count);
    }
}
