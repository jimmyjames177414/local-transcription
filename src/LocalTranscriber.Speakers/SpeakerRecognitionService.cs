using LocalTranscriber.Storage;

namespace LocalTranscriber.Speakers;

/// <summary>
/// Persistent speaker memory: matches new voice embeddings against embeddings stored
/// in SQLite and enrolls named speakers. Never claims certainty it does not have —
/// matches below the confident threshold are marked Uncertain ("possibly Name").
/// </summary>
public sealed class SpeakerRecognitionService : ISpeakerRecognitionService
{
    private readonly IKnownSpeakerStore _speakers;
    private readonly ISpeakerEmbeddingStore _embeddings;
    private readonly SpeakerMemoryOptions _options;

    public SpeakerRecognitionService(
        IKnownSpeakerStore speakers,
        ISpeakerEmbeddingStore embeddings,
        SpeakerMemoryOptions? options = null)
    {
        _speakers = speakers;
        _embeddings = embeddings;
        _options = options ?? new SpeakerMemoryOptions();
    }

    public async Task<SpeakerMatch?> MatchAsync(SpeakerEmbedding embedding, CancellationToken cancellationToken = default)
    {
        var stored = await _embeddings.ListAllAsync(cancellationToken).ConfigureAwait(false);
        if (stored.Count == 0)
        {
            return null;
        }

        // Best similarity per speaker across all of that speaker's samples.
        var bestPerSpeaker = new Dictionary<string, double>();
        foreach (var candidate in stored)
        {
            if (candidate.Dimensions != embedding.Dimensions)
            {
                continue; // embedding from a different model
            }

            double similarity = CosineSimilarity.Compute(embedding.Vector, CosineSimilarity.FromBlob(candidate.Embedding));
            if (!bestPerSpeaker.TryGetValue(candidate.SpeakerId, out double best) || similarity > best)
            {
                bestPerSpeaker[candidate.SpeakerId] = similarity;
            }
        }

        if (bestPerSpeaker.Count == 0)
        {
            return null;
        }

        var (speakerId, topSimilarity) = bestPerSpeaker.MaxBy(kv => kv.Value);
        if (topSimilarity < _options.UncertainThreshold)
        {
            return null;
        }

        var allSpeakers = await _speakers.ListAsync(cancellationToken).ConfigureAwait(false);
        var speaker = allSpeakers.FirstOrDefault(s => s.Id == speakerId);
        if (speaker is null)
        {
            return null;
        }

        return new SpeakerMatch(
            speaker.Id,
            speaker.DisplayName,
            topSimilarity,
            topSimilarity >= _options.MatchThreshold ? SpeakerMatchCertainty.Confident : SpeakerMatchCertainty.Uncertain);
    }

    public async Task EnrollAsync(string speakerName, SpeakerEmbedding embedding, string? sessionId, CancellationToken cancellationToken = default)
    {
        var speaker = await _speakers.GetByNameAsync(speakerName, cancellationToken).ConfigureAwait(false)
            ?? await _speakers.CreateAsync(speakerName, cancellationToken: cancellationToken).ConfigureAwait(false);

        await _embeddings.AddAsync(new StoredEmbedding(
            Id: Guid.NewGuid().ToString("N"),
            SpeakerId: speaker.Id,
            Embedding: CosineSimilarity.ToBlob(embedding.Vector),
            Dimensions: embedding.Dimensions,
            ModelName: embedding.ModelName,
            CreatedAt: DateTimeOffset.Now,
            SourceSessionId: sessionId), cancellationToken).ConfigureAwait(false);

        await _speakers.MarkSeenAsync(speaker.Id, DateTimeOffset.Now, sampleCountDelta: 1, cancellationToken).ConfigureAwait(false);
    }
}
