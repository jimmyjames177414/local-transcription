using LocalTranscriber.Speakers;

namespace LocalTranscriber.Speakers.Tests;

public class CosineSimilarityTests
{
    [Fact]
    public void IdenticalVectors_SimilarityIsOne()
    {
        float[] v = { 0.5f, -0.2f, 0.8f };
        Assert.Equal(1.0, CosineSimilarity.Compute(v, v), precision: 6);
    }

    [Fact]
    public void OrthogonalVectors_SimilarityIsZero()
    {
        Assert.Equal(0.0, CosineSimilarity.Compute(new float[] { 1, 0 }, new float[] { 0, 1 }), precision: 6);
    }

    [Fact]
    public void OppositeVectors_SimilarityIsMinusOne()
    {
        Assert.Equal(-1.0, CosineSimilarity.Compute(new float[] { 1, 2 }, new float[] { -1, -2 }), precision: 6);
    }

    [Fact]
    public void DifferentDimensions_Throws()
    {
        Assert.Throws<ArgumentException>(() => CosineSimilarity.Compute(new float[] { 1 }, new float[] { 1, 2 }));
    }

    [Fact]
    public void ZeroVector_ReturnsZero()
    {
        Assert.Equal(0.0, CosineSimilarity.Compute(new float[] { 0, 0 }, new float[] { 1, 2 }));
    }

    [Fact]
    public void BlobRoundTrip_PreservesVector()
    {
        float[] v = { 0.1f, -0.5f, 3.14f, float.MaxValue };
        Assert.Equal(v, CosineSimilarity.FromBlob(CosineSimilarity.ToBlob(v)));
    }
}
