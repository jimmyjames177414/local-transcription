using System.Diagnostics;
using LocalTranscriber.Shared;
using Whisper.net;

namespace LocalTranscriber.AI;

/// <summary>
/// Local transcription via Whisper.net — the official .NET bindings over whisper.cpp.
/// Fully offline: model file + native library only, no network, no keys.
/// </summary>
public sealed class WhisperCppTranscriptionService : ILocalTranscriptionService, IDisposable
{
    // TEMP DIAGNOSTIC: set LT_TRANSCRIBE_DEBUG=1 to log when the Silero VAD pre-filter
    // drops a window (finds no speech) so its threshold can be tuned from real numbers.
    private static readonly bool TranscribeDebug =
        Environment.GetEnvironmentVariable("LT_TRANSCRIBE_DEBUG") == "1";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private WhisperFactory? _factory;
    private string? _loadedModelPath;
    private WhisperVadFactory? _vadFactory;
    private string? _loadedVadPath;

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
            // A4: VAD pre-filter — skip whisper entirely if no speech detected.
            bool vadWouldDrop = false;
            int vadSegmentCount = -1;
            if (request.EnableVad && !string.IsNullOrEmpty(request.VadModelPath) && File.Exists(request.VadModelPath))
            {
                var vadFactory = GetVadFactory(request.VadModelPath);
                using var vadProcessor = vadFactory.CreateBuilder().Build();
                var speechSegments = vadProcessor.DetectSpeech(samples);
                vadSegmentCount = speechSegments.Count;
                vadWouldDrop = vadSegmentCount == 0;

                // Normal behavior: drop the window when the VAD finds no speech.
                // TEMP DIAGNOSTIC: when LT_TRANSCRIBE_DEBUG=1, DON'T early-return —
                // fall through to whisper so we can log what the VAD would have discarded,
                // then still drop it below to keep production behavior identical.
                if (vadWouldDrop && !TranscribeDebug)
                {
                    return new TranscriptionResult("", Array.Empty<TranscribedSegment>(), null, stopwatch.Elapsed);
                }
            }

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

            // A3: beam search replaces greedy when BeamSize > 0.
            if (request.BeamSize > 0)
            {
                builder = builder.WithBeamSearchSamplingStrategy(b => b.WithBeamSize(request.BeamSize));
            }

            // A3: explicit thread count; 0 means let whisper.cpp auto-detect.
            int threads = request.Threads > 0
                ? request.Threads
                : Math.Max(1, Environment.ProcessorCount - 1);
            builder = builder.WithThreads(threads);

            // A3: domain vocabulary prompt.
            if (!string.IsNullOrWhiteSpace(request.InitialPrompt))
            {
                builder = builder.WithPrompt(request.InitialPrompt);
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

            if (TranscribeDebug && vadSegmentCount >= 0)
            {
                double seconds = samples.Length / (double)WavSampleReader.WhisperSampleRate;
                string preview = text.Length > 120 ? text[..120] + "…" : text;
                if (vadWouldDrop)
                {
                    // The decisive line: what did whisper hear in a window the VAD wanted to discard?
                    AppLog.Info("transcribe-debug",
                        $"VAD-DROP CHECK dur={seconds:F1}s vadSegments=0 -> whisper text=\"{preview}\" " +
                        (string.IsNullOrWhiteSpace(text)
                            ? "(empty — VAD was CORRECT)"
                            : "(NON-EMPTY — VAD DROPPED REAL SPEECH)"));
                }
                else
                {
                    AppLog.Info("transcribe-debug",
                        $"VAD passed: {vadSegmentCount} segment(s) -> whisper text=\"{preview}\"");
                }
            }

            // Preserve production behavior: a window the VAD rejected is still dropped
            // (we only ran whisper above to log what would have been lost).
            if (vadWouldDrop)
            {
                return new TranscriptionResult("", Array.Empty<TranscribedSegment>(), null, stopwatch.Elapsed);
            }

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

    private WhisperVadFactory GetVadFactory(string vadModelPath)
    {
        string full = Path.GetFullPath(vadModelPath);
        if (_vadFactory is null || _loadedVadPath != full)
        {
            _vadFactory?.Dispose();
            _vadFactory = WhisperVadFactory.FromPath(full);
            _loadedVadPath = full;
        }

        return _vadFactory;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
        _vadFactory?.Dispose();
        _vadFactory = null;
        _lock.Dispose();
    }
}
