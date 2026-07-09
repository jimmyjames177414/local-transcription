using System.Diagnostics;
using Whisper.net;

namespace LocalTranscriber.AI;

/// <summary>
/// Local transcription via Whisper.net — the official .NET bindings over whisper.cpp.
/// Fully offline: model file + native library only, no network, no keys.
/// </summary>
public sealed class WhisperCppTranscriptionService : ILocalTranscriptionService, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WhisperFactory? _factory;
    private string? _loadedModelPath;

    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.ModelPath))
        {
            throw new WhisperModelNotFoundException(request.ModelPath);
        }

        if (!File.Exists(request.AudioPath))
        {
            throw new FileNotFoundException($"Audio file not found: {Path.GetFullPath(request.AudioPath)}", request.AudioPath);
        }

        using var timeoutCts = request.Timeout is TimeSpan timeout
            ? new CancellationTokenSource(timeout)
            : null;
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        float[] samples = WavSampleReader.ReadMono16k(request.AudioPath);
        var stopwatch = Stopwatch.StartNew();

        await _lock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var factory = GetFactory(request.ModelPath);

            var builder = factory.CreateBuilder().WithProbabilities();
            if (!string.IsNullOrWhiteSpace(request.Language))
            {
                builder = builder.WithLanguage(request.Language);
            }
            else
            {
                builder = builder.WithLanguageDetection();
            }

            if (request.TranslateToEnglish)
            {
                builder = builder.WithTranslate();
            }

            await using var processor = builder.Build();

            var segments = new List<TranscribedSegment>();
            await foreach (var segment in processor.ProcessAsync(samples, linked.Token).ConfigureAwait(false))
            {
                segments.Add(new TranscribedSegment(
                    segment.Text.Trim(),
                    (long)segment.Start.TotalMilliseconds,
                    (long)segment.End.TotalMilliseconds,
                    segment.Probability));
            }

            stopwatch.Stop();
            string text = string.Join(" ", segments.Select(s => s.Text)).Trim();
            double? confidence = segments.Count > 0 ? segments.Average(s => s.Confidence ?? 0) : null;

            return new TranscriptionResult(text, segments, confidence, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            throw new TimeoutException($"Transcription timed out after {request.Timeout}.");
        }
        finally
        {
            _lock.Release();
        }
    }

    private WhisperFactory GetFactory(string modelPath)
    {
        string full = Path.GetFullPath(modelPath);
        if (_factory is null || _loadedModelPath != full)
        {
            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(full);
            _loadedModelPath = full;
        }

        return _factory;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
        _lock.Dispose();
    }
}
