using SherpaOnnx;

namespace LocalTranscriber.Speakers;

public sealed class SherpaOnnxDiarizationService : ISpeakerDiarizationService
{
    public Task<IReadOnlyList<SpeakerSegment>> DiarizeAsync(SpeakerDiarizationRequest request, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.Models.SegmentationModelPath))
        {
            throw new SpeakerModelNotFoundException(request.Models.SegmentationModelPath);
        }

        if (!File.Exists(request.Models.EmbeddingModelPath))
        {
            throw new SpeakerModelNotFoundException(request.Models.EmbeddingModelPath);
        }

        if (!File.Exists(request.AudioPath))
        {
            throw new FileNotFoundException($"Audio file not found: {Path.GetFullPath(request.AudioPath)}", request.AudioPath);
        }

        return Task.Run<IReadOnlyList<SpeakerSegment>>(() =>
        {
            var config = new OfflineSpeakerDiarizationConfig();
            config.Segmentation.Pyannote.Model = request.Models.SegmentationModelPath;
            config.Embedding.Model = request.Models.EmbeddingModelPath;
            if (request.NumSpeakers is int n && n > 0)
            {
                config.Clustering.NumClusters = n;
            }

            using var diarizer = new OfflineSpeakerDiarization(config);
            float[] samples = AudioSamples.ReadMono16k(request.AudioPath);
            if (samples.Length == 0)
            {
                return Array.Empty<SpeakerSegment>();
            }

            var segments = diarizer.Process(samples);
            return segments
                .Select(s => new SpeakerSegment(
                    TemporarySpeakerId: $"speaker_{s.Speaker + 1}",
                    StartMs: (long)(s.Start * 1000),
                    EndMs: (long)(s.End * 1000),
                    Confidence: null)) // pyannote clustering does not expose per-segment confidence
                .OrderBy(s => s.StartMs)
                .ToArray();
        }, cancellationToken);
    }
}

public sealed class SherpaOnnxEmbeddingService : ISpeakerEmbeddingService, IDisposable
{
    private readonly object _lock = new();
    private SpeakerEmbeddingExtractor? _extractor;
    private string? _loadedModelPath;

    public Task<SpeakerEmbedding> ExtractEmbeddingAsync(SpeakerEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.Models.EmbeddingModelPath))
        {
            throw new SpeakerModelNotFoundException(request.Models.EmbeddingModelPath);
        }

        if (!File.Exists(request.AudioPath))
        {
            throw new FileNotFoundException($"Audio file not found: {Path.GetFullPath(request.AudioPath)}", request.AudioPath);
        }

        return Task.Run(() =>
        {
            float[] samples = AudioSamples.ReadMono16k(request.AudioPath, request.StartMs, request.EndMs);
            if (samples.Length < AudioSamples.SampleRate / 2)
            {
                throw new InvalidOperationException("Audio segment is too short for a reliable voice embedding (need at least 0.5s).");
            }

            lock (_lock)
            {
                var extractor = GetExtractor(request.Models.EmbeddingModelPath);
                using var stream = extractor.CreateStream();
                stream.AcceptWaveform(AudioSamples.SampleRate, samples);
                stream.InputFinished();

                float[] vector = extractor.Compute(stream);
                return new SpeakerEmbedding(vector, vector.Length, Path.GetFileNameWithoutExtension(request.Models.EmbeddingModelPath));
            }
        }, cancellationToken);
    }

    private SpeakerEmbeddingExtractor GetExtractor(string modelPath)
    {
        string full = Path.GetFullPath(modelPath);
        if (_extractor is null || _loadedModelPath != full)
        {
            _extractor?.Dispose();
            var config = new SpeakerEmbeddingExtractorConfig { Model = full, NumThreads = 2 };
            _extractor = new SpeakerEmbeddingExtractor(config);
            _loadedModelPath = full;
        }

        return _extractor;
    }

    public void Dispose()
    {
        _extractor?.Dispose();
        _extractor = null;
    }
}
