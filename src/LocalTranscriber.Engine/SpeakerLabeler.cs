using LocalTranscriber.AI;
using LocalTranscriber.Shared;
using LocalTranscriber.Speakers;

namespace LocalTranscriber.Engine;

/// <summary>
/// The system-audio speaker pipeline extracted from <see cref="RealTranscriptionEngine"/>:
/// diarize a window, average an embedding per diarized cluster, resolve each cluster to a speaker
/// (known-voice memory first, else a session-stable label), and assign each whisper segment to the
/// cluster it overlaps most. The engine keeps transcription and emission; this owns labeling.
/// Behavior is unchanged from the inline version — only the ownership moved.
/// </summary>
internal sealed class SpeakerLabeler
{
    private readonly ISpeakerDiarizationService? _diarization;
    private readonly ISpeakerEmbeddingService? _embedding;
    private readonly ISpeakerRecognitionService? _recognition;
    private readonly Action<string> _addWarning;

    // Smoothing: the last resolved speaker, carried across windows so a segment that overlaps no
    // diarized cluster (or a window whose clusters can't be embedded) inherits the current speaker
    // instead of emitting a bare "Unknown"/"Speaker 1" that interrupts one person's turn. This is
    // the most recent labeled segment, not the window's most prominent speaker — a simple, safe
    // continuity heuristic. Mutated only on the single processor task; no synchronization needed.
    private SpeakerLabel? _lastSpeaker;

    /// <summary>Clears cross-window smoothing state. Call once per session.</summary>
    public void Reset() => _lastSpeaker = null;

    public SpeakerLabeler(
        ISpeakerDiarizationService? diarization,
        ISpeakerEmbeddingService? embedding,
        ISpeakerRecognitionService? recognition,
        Action<string> addWarning)
    {
        _diarization = diarization;
        _embedding = embedding;
        _recognition = recognition;
        _addWarning = addWarning;
    }

    /// <summary>
    /// Labels every whisper segment of a system-audio window with a speaker. Returns pairs in the
    /// input order; callers emit them in sequence.
    /// </summary>
    public async Task<IReadOnlyList<(TranscribedSegment Segment, SpeakerLabel Speaker)>> LabelSegmentsAsync(
        string wavPath,
        IReadOnlyList<TranscribedSegment> segments,
        SessionSpeakerRegistry registry,
        string? speakerModelDir,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SpeakerSegment> diarized = Array.Empty<SpeakerSegment>();
        if (_diarization is not null && speakerModelDir is not null)
        {
            try
            {
                diarized = await _diarization.DiarizeAsync(new SpeakerDiarizationRequest
                {
                    AudioPath = wavPath,
                    Models = new SpeakerModelConfig { ModelDir = speakerModelDir }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _addWarning($"Diarization failed, falling back to single speaker: {ex.Message}");
            }
        }

        // Resolve each diarized cluster to a speaker label once per window.
        // Average embeddings from up to 3 qualifying segments per cluster for robustness.
        // Clusters whose segments are all too short to embed are excluded from the map; their
        // whisper segments fall through to defaultLabel instead of inheriting a stale label.
        var clusterLabels = new Dictionary<string, SpeakerLabel>();
        foreach (var cluster in diarized.GroupBy(s => s.TemporarySpeakerId))
        {
            var embedding = await ExtractAveragedEmbeddingAsync(wavPath, cluster, speakerModelDir, cancellationToken).ConfigureAwait(false);
            if (embedding is not null)
            {
                clusterLabels[cluster.Key] = await ResolveSpeakerFromEmbeddingAsync(embedding, registry, cancellationToken).ConfigureAwait(false);
            }
        }

        // Fallback for segments that overlap no diarized cluster. Prefer continuity (the last
        // resolved speaker) over a bare "Unknown"/"Speaker 1" that would visibly break a turn.
        var defaultLabel = clusterLabels.Count switch
        {
            1 => clusterLabels.Values.First(),
            0 => _lastSpeaker ?? new SpeakerLabel("speaker_unknown", "Speaker 1", IsKnown: false),
            _ => _lastSpeaker ?? new SpeakerLabel("speaker_unknown", "Unknown", IsKnown: false)
        };

        var labeled = new List<(TranscribedSegment, SpeakerLabel)>(segments.Count);
        foreach (var segment in segments)
        {
            var speaker = AssignSpeaker(segment, diarized, clusterLabels) ?? defaultLabel;
            labeled.Add((segment, speaker));
            // Only carry forward a resolved identity, never the "speaker_unknown" placeholder.
            if (speaker.SpeakerId != "speaker_unknown")
            {
                _lastSpeaker = speaker;
            }
        }

        return labeled;
    }

    /// <summary>Assigns the diarized cluster with the largest time overlap with the whisper segment.</summary>
    private static SpeakerLabel? AssignSpeaker(
        TranscribedSegment segment,
        IReadOnlyList<SpeakerSegment> diarized,
        IReadOnlyDictionary<string, SpeakerLabel> clusterLabels)
    {
        string? bestCluster = null;
        long bestOverlap = 0;
        foreach (var d in diarized)
        {
            long overlap = Math.Min(segment.EndMs, d.EndMs) - Math.Max(segment.StartMs, d.StartMs);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestCluster = d.TemporarySpeakerId;
            }
        }

        return bestCluster is not null && clusterLabels.TryGetValue(bestCluster, out var label) ? label : null;
    }

    /// <summary>
    /// Extracts embeddings from up to 3 qualifying segments (≥ 700 ms, longest-first) and
    /// returns their average, or null if none qualify. Capped to limit per-window CPU cost.
    /// </summary>
    private async Task<SpeakerEmbedding?> ExtractAveragedEmbeddingAsync(
        string wavPath, IEnumerable<SpeakerSegment> segments, string? speakerModelDir, CancellationToken ct)
    {
        if (_embedding is null || speakerModelDir is null)
        {
            return null;
        }

        var qualifying = segments
            .Where(s => s.EndMs - s.StartMs >= 700)
            .OrderByDescending(s => s.EndMs - s.StartMs)
            .Take(3)
            .ToList();

        if (qualifying.Count == 0)
        {
            return null;
        }

        var embeddings = new List<SpeakerEmbedding>(qualifying.Count);
        foreach (var seg in qualifying)
        {
            try
            {
                var emb = await _embedding.ExtractEmbeddingAsync(new SpeakerEmbeddingRequest
                {
                    AudioPath = wavPath,
                    Models = new SpeakerModelConfig { ModelDir = speakerModelDir },
                    StartMs = seg.StartMs,
                    EndMs = seg.EndMs
                }, ct).ConfigureAwait(false);
                embeddings.Add(emb);
            }
            catch (Exception ex)
            {
                _addWarning($"Speaker embedding failed: {ex.Message}");
            }
        }

        if (embeddings.Count == 0)
        {
            return null;
        }

        if (embeddings.Count == 1)
        {
            return embeddings[0];
        }

        int dim = embeddings[0].Dimensions;
        var sum = new float[dim];
        int contributing = 0;
        foreach (var e in embeddings)
        {
            if (e.Dimensions != dim) continue;
            for (int i = 0; i < dim; i++) sum[i] += e.Vector[i];
            contributing++;
        }
        var avg = new float[dim];
        for (int i = 0; i < dim; i++) avg[i] = sum[i] / contributing;
        return new SpeakerEmbedding(avg, dim, embeddings[0].ModelName);
    }

    private async Task<SpeakerLabel> ResolveSpeakerFromEmbeddingAsync(
        SpeakerEmbedding embedding, SessionSpeakerRegistry registry, CancellationToken ct)
    {
        if (_recognition is not null)
        {
            try
            {
                var match = await _recognition.MatchAsync(embedding, ct).ConfigureAwait(false);
                if (match is not null)
                {
                    // Confidence below the writer's threshold renders as "possibly Name".
                    return new SpeakerLabel(match.SpeakerId, match.DisplayName, IsKnown: true, Confidence: match.Similarity);
                }
            }
            catch (Exception ex)
            {
                _addWarning($"Speaker recognition failed: {ex.Message}");
            }
        }

        string label = registry.ResolveLabel(embedding);
        return new SpeakerLabel(SessionSpeakerRegistry.LabelToSessionSpeakerId(label), label, IsKnown: false);
    }
}
