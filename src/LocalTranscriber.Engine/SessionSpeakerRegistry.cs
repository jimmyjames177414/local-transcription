using LocalTranscriber.Speakers;

namespace LocalTranscriber.Engine;

/// <summary>
/// Keeps unnamed speakers consistent across chunks within one session.
/// Diarization cluster ids are chunk-local (speaker_1 in chunk 3 is not speaker_1
/// in chunk 5), so unknown voices are re-identified by embedding similarity against
/// speakers already seen this session.
/// </summary>
public sealed class SessionSpeakerRegistry
{
    private readonly double _sameSpeakerThreshold;
    private readonly List<(string Label, List<float[]> Embeddings)> _speakers = new();
    private readonly object _lock = new();

    public SessionSpeakerRegistry(double sameSpeakerThreshold = 0.60)
    {
        _sameSpeakerThreshold = sameSpeakerThreshold;
    }

    /// <summary>Returns a stable in-session label ("Speaker 1", "Speaker 2", ...) for an unknown voice.</summary>
    public string ResolveLabel(SpeakerEmbedding embedding)
    {
        lock (_lock)
        {
            string? bestLabel = null;
            double best = 0;
            foreach (var (label, embeddings) in _speakers)
            {
                foreach (var stored in embeddings)
                {
                    if (stored.Length != embedding.Vector.Length)
                    {
                        continue;
                    }

                    double similarity = CosineSimilarity.Compute(embedding.Vector, stored);
                    if (similarity > best)
                    {
                        best = similarity;
                        bestLabel = label;
                    }
                }
            }

            if (bestLabel is not null && best >= _sameSpeakerThreshold)
            {
                var entry = _speakers.First(s => s.Label == bestLabel);
                if (entry.Embeddings.Count < 10) // cap memory per speaker
                {
                    entry.Embeddings.Add(embedding.Vector);
                }
                return bestLabel;
            }

            string newLabel = $"Speaker {_speakers.Count + 1}";
            _speakers.Add((newLabel, new List<float[]> { embedding.Vector }));
            return newLabel;
        }
    }

    /// <summary>Fallback label when no embedding could be extracted (very short segment).</summary>
    public string FallbackLabel()
    {
        lock (_lock)
        {
            return _speakers.Count > 0 ? _speakers[^1].Label : "Speaker 1";
        }
    }
}
