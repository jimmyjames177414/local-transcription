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
/// in chunk 5), so unknown voices are re-identified by comparing new embeddings
/// against per-speaker running centroids.
///
/// Threshold semantics:
///   similarity >= assignThreshold  → assign + update centroid (confident match)
///   similarity in [newSpeakerThreshold, assignThreshold) → assign nearest, but do NOT
///       update centroid (poisoning brake — prevents a borderline match from drifting the mean)
///   similarity < newSpeakerThreshold → mint a new "Speaker N"
/// </summary>
public sealed class SessionSpeakerRegistry
{
    private readonly double _assignThreshold;
    private readonly double _newSpeakerThreshold;

    private sealed class SpeakerEntry
    {
        public string Label { get; }
        public string SessionSpeakerId { get; }
        // Running centroid components.
        public float[] CentroidSum { get; private set; }
        public int CentroidCount { get; private set; }
        // Raw embeddings kept only for enrolment (NameSessionSpeakerAsync); not used for matching.
        public List<SpeakerEmbedding> Embeddings { get; } = new();

        public SpeakerEntry(string label, SpeakerEmbedding seed)
        {
            Label = label;
            SessionSpeakerId = SessionSpeakerRegistry.LabelToSessionSpeakerId(label);
            CentroidSum = (float[])seed.Vector.Clone();
            CentroidCount = 1;
            Embeddings.Add(seed);
        }

        public float[] Centroid()
        {
            var c = new float[CentroidSum.Length];
            for (int i = 0; i < c.Length; i++) c[i] = CentroidSum[i] / CentroidCount;
            return c;
        }

        public void UpdateCentroid(SpeakerEmbedding e)
        {
            if (e.Vector.Length != CentroidSum.Length) return;
            for (int i = 0; i < CentroidSum.Length; i++) CentroidSum[i] += e.Vector[i];
            CentroidCount++;
            if (Embeddings.Count < 20) Embeddings.Add(e);
        }
    }

    private readonly List<SpeakerEntry> _speakers = new();
    private readonly object _lock = new();

    public SessionSpeakerRegistry(double assignThreshold = 0.50, double newSpeakerThreshold = 0.40)
    {
        _assignThreshold = assignThreshold;
        _newSpeakerThreshold = newSpeakerThreshold;
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
            if (_speakers.Count == 0)
            {
                var first = new SpeakerEntry("Speaker 1", embedding);
                _speakers.Add(first);
                return first.Label;
            }

            SpeakerEntry? best = null;
            double bestSim = double.MinValue;
            foreach (var s in _speakers)
            {
                var centroid = s.Centroid();
                if (centroid.Length != embedding.Dimensions) continue;
                double sim = CosineSimilarity.Compute(embedding.Vector, centroid);
                if (sim > bestSim) { bestSim = sim; best = s; }
            }

            if (best is not null && bestSim >= _assignThreshold)
            {
                best.UpdateCentroid(embedding);
                return best.Label;
            }

            if (best is not null && bestSim >= _newSpeakerThreshold)
            {
                // Gray zone: assign to nearest but don't move the centroid (poisoning brake).
                if (best.Embeddings.Count < 20) best.Embeddings.Add(embedding);
                return best.Label;
            }

            string newLabel = $"Speaker {_speakers.Count + 1}";
            _speakers.Add(new SpeakerEntry(newLabel, embedding));
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
