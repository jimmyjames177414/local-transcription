namespace LocalTranscriber.AI;

public sealed record TranscriptionRequest
{
    public required string AudioPath { get; init; }
    public required string ModelPath { get; init; }
    public string? Language { get; init; }
    public bool TranslateToEnglish { get; init; }
    public bool IncludeTimestamps { get; init; } = true;
    public TimeSpan? Timeout { get; init; }

    // A3: decoding tuning
    public int BeamSize { get; init; } = 0;       // 0 = greedy; >0 = beam search with this size
    public int Threads { get; init; } = 0;         // 0 = auto
    public string? InitialPrompt { get; init; }

    // A4: VAD pre-filter (silence gate before whisper)
    public bool EnableVad { get; init; }
    public string? VadModelPath { get; init; }
}

public sealed record TranscribedSegment(
    string Text,
    long StartMs,
    long EndMs,
    double? Confidence
);

public sealed record TranscriptionResult(
    string Text,
    IReadOnlyList<TranscribedSegment> Segments,
    double? Confidence,
    TimeSpan Duration
);

public interface ILocalTranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default);
}

public sealed class WhisperModelNotFoundException : FileNotFoundException
{
    public WhisperModelNotFoundException(string expectedPath)
        : base($"Whisper model not found.{Environment.NewLine}" +
               $"Expected path: {Path.GetFullPath(expectedPath)}{Environment.NewLine}" +
               "Place a whisper.cpp model file there or update config.", expectedPath)
    {
    }
}
