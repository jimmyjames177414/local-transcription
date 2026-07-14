using LocalTranscriber.Speakers;

namespace LocalTranscriber.Engine;

/// <summary>Snapshot of a single unnamed session speaker and their captured embeddings.</summary>
public sealed record SessionSpeakerInfo(
    string Label,
    string SessionSpeakerId,
    int EmbeddingCount,
    IReadOnlyList<SpeakerEmbedding> Embeddings);

/// <summary>
/// Keeps unnamed speakers consistent across chunks within one session.
/// Diarization cluster ids are chunk-local (speaker_1 in chunk 3 is not speaker_1
/// in chunk 5), so unknown voices are re-identified by embedding similarity against
/// speakers already seen this session.
/// </summary>
public sealed class SessionSpeakerRegistry
{
    private readonly double _sameSpeakerThreshold;
    private readonly List<(string Label, string SessionSpeakerId, List<SpeakerEmbedding> Embeddings)> _speakers = new();
    private readonly object _lock = new();

    public SessionSpeakerRegistry(double sameSpeakerThreshold = 0.60)
    {
        _sameSpeakerThreshold = sameSpeakerThreshold;
    }

    /// <summary>Derives the stable session speaker ID from a human-readable label ("Speaker 1" → "session_speaker_1").</summary>
    public static string LabelToSessionSpeakerId(string label)
        => $"session_{label.Replace(' ', '_').ToLowerInvariant()}";

    /// <summary>Returns a snapshot of all session speakers with their captured embeddings.</summary>
    public IReadOnlyList<SessionSpeakerInfo> Snapshot()
    {
        lock (_lock)
        {
            return _speakers
                .Select(s => new SessionSpeakerInfo(s.Label, s.SessionSpeakerId, s.Embeddings.Count, s.Embeddings.ToList()))
                .ToList();
        }
    }

    /// <summary>Returns a stable in-session label ("Speaker 1", "Speaker 2", ...) for an unknown voice.</summary>
    public string ResolveLabel(SpeakerEmbedding embedding)
    {
        lock (_lock)
        {
            string? bestLabel = null;
            double best = 0;
            foreach (var (label, _, embeddings) in _speakers)
            {
                foreach (var stored in embeddings)
                {
                    if (stored.Dimensions != embedding.Dimensions) continue;
                    double similarity = CosineSimilarity.Compute(embedding.Vector, stored.Vector);
                    if (similarity > best) { best = similarity; bestLabel = label; }
                }
            }

            if (bestLabel is not null && best >= _sameSpeakerThreshold)
            {
                var entry = _speakers.First(s => s.Label == bestLabel);
                if (entry.Embeddings.Count < 10)
                    entry.Embeddings.Add(embedding);
                return bestLabel;
            }

            string newLabel = $"Speaker {_speakers.Count + 1}";
            _speakers.Add((newLabel, LabelToSessionSpeakerId(newLabel), new List<SpeakerEmbedding> { embedding }));
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

