using LocalTranscriber.Speakers;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Speakers.Tests;

public class SpeakerRecognitionServiceTests
{
    private sealed class FakeSpeakerStore : IKnownSpeakerStore
    {
        public List<KnownSpeaker> Items { get; } = new();

        public Task<KnownSpeaker> CreateAsync(string displayName, string? notes = null, CancellationToken ct = default)
        {
            var s = new KnownSpeaker(Guid.NewGuid().ToString("N"), displayName, DateTimeOffset.Now, DateTimeOffset.Now, null, 0, notes);
            Items.Add(s);
            return Task.FromResult(s);
        }

        public Task<IReadOnlyList<KnownSpeaker>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<KnownSpeaker>>(Items.ToList());

        public Task<KnownSpeaker?> GetByNameAsync(string displayName, CancellationToken ct = default)
            => Task.FromResult(Items.FirstOrDefault(s => string.Equals(s.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)));

        public Task<bool> RenameAsync(string fromName, string toName, CancellationToken ct = default)
        {
            int i = Items.FindIndex(s => s.DisplayName == fromName);
            if (i >= 0)
            {
                Items[i] = Items[i] with { DisplayName = toName };
            }
            return Task.FromResult(true);
        }

        public Task<bool> ForgetAsync(string displayName, CancellationToken ct = default)
            => Task.FromResult(Items.RemoveAll(s => s.DisplayName == displayName) > 0);

        public Task MarkSeenAsync(string speakerId, DateTimeOffset seenAt, int sampleCountDelta = 0, CancellationToken ct = default)
        {
            int i = Items.FindIndex(s => s.Id == speakerId);
            if (i >= 0)
            {
                Items[i] = Items[i] with { LastSeenAt = seenAt, SampleCount = Items[i].SampleCount + sampleCountDelta };
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEmbeddingStore : ISpeakerEmbeddingStore
    {
        public List<StoredEmbedding> Items { get; } = new();

        public Task AddAsync(StoredEmbedding embedding, CancellationToken ct = default)
        {
            Items.Add(embedding);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredEmbedding>> ListBySpeakerAsync(string speakerId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StoredEmbedding>>(Items.Where(e => e.SpeakerId == speakerId).ToList());

        public Task<IReadOnlyList<StoredEmbedding>> ListAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StoredEmbedding>>(Items.ToList());

        public Task DeleteBySpeakerAsync(string speakerId, CancellationToken ct = default)
        {
            Items.RemoveAll(e => e.SpeakerId == speakerId);
            return Task.CompletedTask;
        }
    }

    private static SpeakerEmbedding Embedding(params float[] v) => new(v, v.Length, "fake-model");

    private static (SpeakerRecognitionService service, FakeSpeakerStore speakers, FakeEmbeddingStore embeddings) Make()
    {
        var speakers = new FakeSpeakerStore();
        var embeddings = new FakeEmbeddingStore();
        var service = new SpeakerRecognitionService(speakers, embeddings,
            new SpeakerMemoryOptions { MatchThreshold = 0.72, UncertainThreshold = 0.62 });
        return (service, speakers, embeddings);
    }

    [Fact]
    public async Task Match_NoEnrollments_ReturnsNull()
    {
        var (service, _, _) = Make();
        Assert.Null(await service.MatchAsync(Embedding(1, 0, 0)));
    }

    [Fact]
    public async Task Enroll_AddsSpeakerEmbeddingAndSampleCount()
    {
        var (service, speakers, embeddings) = Make();
        await service.EnrollAsync("Joe", Embedding(1, 0, 0), "session-1");

        Assert.Single(speakers.Items);
        Assert.Equal("Joe", speakers.Items[0].DisplayName);
        Assert.Equal(1, speakers.Items[0].SampleCount);
        Assert.Single(embeddings.Items);
        Assert.Equal("session-1", embeddings.Items[0].SourceSessionId);
    }

    [Fact]
    public async Task Match_IdenticalVoice_IsConfident()
    {
        var (service, _, _) = Make();
        await service.EnrollAsync("Joe", Embedding(0.3f, 0.7f, -0.2f), null);

        var match = await service.MatchAsync(Embedding(0.3f, 0.7f, -0.2f));
        Assert.NotNull(match);
        Assert.Equal("Joe", match!.DisplayName);
        Assert.Equal(SpeakerMatchCertainty.Confident, match.Certainty);
        Assert.Equal(1.0, match.Similarity, precision: 5);
    }

    [Fact]
    public async Task Match_SimilarVoice_InUncertainBand_IsUncertain()
    {
        var (service, _, _) = Make();
        await service.EnrollAsync("Joe", Embedding(1, 0), null);

        // cos(theta) ~ 0.66 -> between 0.62 and 0.72
        var match = await service.MatchAsync(Embedding(0.66f, 0.7512f));
        Assert.NotNull(match);
        Assert.Equal(SpeakerMatchCertainty.Uncertain, match!.Certainty);
        Assert.InRange(match.Similarity, 0.62, 0.72);
    }

    [Fact]
    public async Task Match_DissimilarVoice_ReturnsNull()
    {
        var (service, _, _) = Make();
        await service.EnrollAsync("Joe", Embedding(1, 0), null);

        Assert.Null(await service.MatchAsync(Embedding(0, 1)));
    }

    [Fact]
    public async Task Match_PicksBestSpeaker_AcrossMultipleEnrollments()
    {
        var (service, _, _) = Make();
        await service.EnrollAsync("Joe", Embedding(1, 0, 0), null);
        await service.EnrollAsync("Martina", Embedding(0, 1, 0), null);
        await service.EnrollAsync("Martina", Embedding(0, 0.9f, 0.1f), null);

        var match = await service.MatchAsync(Embedding(0, 0.95f, 0.05f));
        Assert.NotNull(match);
        Assert.Equal("Martina", match!.DisplayName);
        Assert.Equal(SpeakerMatchCertainty.Confident, match.Certainty);
    }

    [Fact]
    public async Task Match_SkipsEmbeddingsFromOtherModels()
    {
        var (service, _, _) = Make();
        await service.EnrollAsync("Joe", Embedding(1, 0), null); // 2-dim

        Assert.Null(await service.MatchAsync(Embedding(1, 0, 0))); // 3-dim query
    }

    [Fact]
    public async Task Enroll_SameNameTwice_ReusesSpeaker()
    {
        var (service, speakers, embeddings) = Make();
        await service.EnrollAsync("Joe", Embedding(1, 0), null);
        await service.EnrollAsync("Joe", Embedding(0.9f, 0.1f), null);

        Assert.Single(speakers.Items);
        Assert.Equal(2, speakers.Items[0].SampleCount);
        Assert.Equal(2, embeddings.Items.Count);
    }
}
