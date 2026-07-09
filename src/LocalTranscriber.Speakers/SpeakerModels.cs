namespace LocalTranscriber.Speakers;

public sealed record SpeakerModelConfig
{
    /// <summary>Folder that contains segmentation.onnx and embedding.onnx.</summary>
    public required string ModelDir { get; init; }

    public string SegmentationModelPath => Path.Combine(ModelDir, "segmentation.onnx");
    public string EmbeddingModelPath => Path.Combine(ModelDir, "embedding.onnx");
}

public sealed record SpeakerDiarizationRequest
{
    public required string AudioPath { get; init; }
    public required SpeakerModelConfig Models { get; init; }

    /// <summary>Exact speaker count when known; otherwise clustering threshold is used.</summary>
    public int? NumSpeakers { get; init; }
}

public sealed record SpeakerSegment(
    string TemporarySpeakerId,
    long StartMs,
    long EndMs,
    double? Confidence
);

public sealed record SpeakerEmbeddingRequest
{
    public required string AudioPath { get; init; }
    public required SpeakerModelConfig Models { get; init; }

    /// <summary>Optional window inside the audio file to embed (whole file when null).</summary>
    public long? StartMs { get; init; }
    public long? EndMs { get; init; }
}

public sealed record SpeakerEmbedding(
    float[] Vector,
    int Dimensions,
    string ModelName
);

public enum SpeakerMatchCertainty
{
    Confident,
    Uncertain
}

public sealed record SpeakerMatch(
    string SpeakerId,
    string DisplayName,
    double Similarity,
    SpeakerMatchCertainty Certainty
);

public sealed record SpeakerMemoryOptions
{
    public double MatchThreshold { get; init; } = 0.72;
    public double UncertainThreshold { get; init; } = 0.62;
}

public interface ISpeakerDiarizationService
{
    Task<IReadOnlyList<SpeakerSegment>> DiarizeAsync(SpeakerDiarizationRequest request, CancellationToken cancellationToken = default);
}

public interface ISpeakerEmbeddingService
{
    Task<SpeakerEmbedding> ExtractEmbeddingAsync(SpeakerEmbeddingRequest request, CancellationToken cancellationToken = default);
}

public interface ISpeakerRecognitionService
{
    Task<SpeakerMatch?> MatchAsync(SpeakerEmbedding embedding, CancellationToken cancellationToken = default);
    Task EnrollAsync(string speakerName, SpeakerEmbedding embedding, string? sessionId, CancellationToken cancellationToken = default);
}

public sealed class SpeakerModelNotFoundException : FileNotFoundException
{
    public SpeakerModelNotFoundException(string expectedPath)
        : base($"Speaker model not found.{Environment.NewLine}" +
               $"Expected path: {Path.GetFullPath(expectedPath)}{Environment.NewLine}" +
               "Run ./scripts/setup.ps1 -DownloadModels or place the sherpa-onnx model there.", expectedPath)
    {
    }
}
