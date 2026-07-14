using LocalTranscriber.AI;
using LocalTranscriber.Audio;
using LocalTranscriber.Engine;
using LocalTranscriber.Shared;
using LocalTranscriber.Speakers;

namespace LocalTranscriber.Engine.Tests;

public class RealEngineIntegrationTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-real-tests-" + Guid.NewGuid().ToString("N"));
    private string _fakeModelPath = "";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _fakeModelPath = Path.Combine(_dir, "fake-whisper.bin");
        File.WriteAllBytes(_fakeModelPath, new byte[] { 1 });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    /// <summary>Emits loud 16-bit PCM chunks on demand so window buffers fill fast.</summary>
    private sealed class FakeCaptureService : IAudioCaptureService
    {
        private readonly AudioSourceType _source;
        private CancellationTokenSource? _cts;
        private Task? _pump;

        public FakeCaptureService(AudioSourceType source) => _source = source;

        public AudioSourceType Source => _source;
        public event EventHandler<AudioChunk>? ChunkAvailable;
        public bool IsCapturing { get; private set; }

        public Task StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default)
        {
            _cts = new CancellationTokenSource();
            IsCapturing = true;
            _pump = Task.Run(async () =>
            {
                // 0.5s of loud audio per tick at 16kHz mono 16-bit
                byte[] data = new byte[16000];
                for (int i = 0; i < data.Length; i += 2)
                {
                    BitConverter.TryWriteBytes(data.AsSpan(i), (short)20000);
                }

                while (!_cts.Token.IsCancellationRequested)
                {
                    ChunkAvailable?.Invoke(this, new AudioChunk(_source, data, 16000, 1, 16, false, DateTimeOffset.Now));
                    await Task.Delay(10, _cts.Token);
                }
            });
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _cts?.Cancel();
            IsCapturing = false;
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }

    private sealed class StallingCaptureService : IAudioCaptureService
    {
        private readonly AudioSourceType _source;
        private readonly TimeSpan _stallAfter;
        private CancellationTokenSource? _cts;

        public StallingCaptureService(AudioSourceType source, TimeSpan stallAfter)
        {
            _source = source;
            _stallAfter = stallAfter;
        }

        public AudioSourceType Source => _source;
        public event EventHandler<AudioChunk>? ChunkAvailable;
        public bool IsCapturing { get; private set; }

        public Task StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default)
        {
            _cts = new CancellationTokenSource();
            IsCapturing = true;
            byte[] data = new byte[16000];
            for (int i = 0; i < data.Length; i += 2)
                BitConverter.TryWriteBytes(data.AsSpan(i), (short)20000);
            var stopAt = DateTimeOffset.Now + _stallAfter;
            _ = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    if (DateTimeOffset.Now < stopAt)
                        ChunkAvailable?.Invoke(this, new AudioChunk(_source, data, 16000, 1, 16, false, DateTimeOffset.Now));
                    await Task.Delay(10, _cts.Token).ConfigureAwait(false);
                }
            });
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _cts?.Cancel();
            IsCapturing = false;
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }

    private sealed class FakeTranscriptionService : ILocalTranscriptionService
    {
        private int _calls;
        public int Calls => _calls;

        public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
        {
            int n = Interlocked.Increment(ref _calls);
            var segments = new[] { new TranscribedSegment($"Fake transcript line {n}.", 0, 2000, 0.95) };
            return Task.FromResult(new TranscriptionResult(segments[0].Text, segments, 0.95, TimeSpan.FromMilliseconds(5)));
        }
    }

    private sealed class FakeDiarizationService : ISpeakerDiarizationService
    {
        public Task<IReadOnlyList<SpeakerSegment>> DiarizeAsync(SpeakerDiarizationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SpeakerSegment>>(new[]
            {
                new SpeakerSegment("speaker_1", 0, 1000, null),
                new SpeakerSegment("speaker_2", 1000, 2000, null)
            });
    }

    private sealed class FakeEmbeddingService : ISpeakerEmbeddingService
    {
        public Task<SpeakerEmbedding> ExtractEmbeddingAsync(SpeakerEmbeddingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new SpeakerEmbedding(new float[] { 1, 0, 0 }, 3, "fake"));
    }

    private sealed class FakeRecognitionService : ISpeakerRecognitionService
    {
        public SpeakerMatch? NextMatch { get; set; }

        public Task<SpeakerMatch?> MatchAsync(SpeakerEmbedding embedding, CancellationToken cancellationToken = default)
            => Task.FromResult(NextMatch);

        public Task EnrollAsync(string speakerName, SpeakerEmbedding embedding, string? sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private TranscriptionSessionOptions MakeOptions(bool mic = true, bool system = false) => new()
    {
        OutputTextPath = Path.Combine(_dir, "out.txt"),
        OutputJsonlPath = Path.Combine(_dir, "out.jsonl"),
        EnableMicrophone = mic,
        EnableSystemAudio = system,
        WhisperModelPath = _fakeModelPath,
        SpeakerModelDir = _dir,
        ChunkSeconds = 1,
        OverlapMs = 0
    };

    [Fact]
    public async Task MicPipeline_ProducesMeLabeledEvents_AndWritesFiles()
    {
        var transcription = new FakeTranscriptionService();
        await using var engine = new RealTranscriptionEngine(
            transcription,
            micFactory: () => new FakeCaptureService(AudioSourceType.Microphone));

        await engine.StartAsync(MakeOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<TranscriptEvent>();
        await foreach (var e in engine.StreamEventsAsync(cts.Token))
        {
            events.Add(e);
            if (events.Count >= 2)
            {
                break;
            }
        }

        await engine.StopAsync();

        Assert.All(events, e => Assert.Equal("Me", e.Speaker.DisplayName));
        Assert.All(events, e => Assert.Equal(AudioSourceType.Microphone, e.Source));
        Assert.True(File.Exists(Path.Combine(_dir, "out.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "out.jsonl")));
        Assert.Contains("Me:", File.ReadAllText(Path.Combine(_dir, "out.txt")));
    }

    [Fact]
    public async Task SystemPipeline_UsesSpeakerMemoryMatch()
    {
        var recognition = new FakeRecognitionService
        {
            NextMatch = new SpeakerMatch("id-joe", "Joe", 0.9, SpeakerMatchCertainty.Confident)
        };

        await using var engine = new RealTranscriptionEngine(
            new FakeTranscriptionService(),
            systemFactory: () => new FakeCaptureService(AudioSourceType.SystemAudio),
            diarization: new FakeDiarizationService(),
            embedding: new FakeEmbeddingService(),
            recognition: recognition);

        await engine.StartAsync(MakeOptions(mic: false, system: true));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        TranscriptEvent? first = null;
        await foreach (var e in engine.StreamEventsAsync(cts.Token))
        {
            first = e;
            break;
        }

        await engine.StopAsync();

        Assert.NotNull(first);
        Assert.Equal("Joe", first!.Speaker.DisplayName);
        Assert.True(first.Speaker.IsKnown);
        Assert.Equal(AudioSourceType.SystemAudio, first.Source);
    }

    [Fact]
    public async Task Start_MissingWhisperModel_ThrowsHelpfulError()
    {
        await using var engine = new RealTranscriptionEngine(
            new FakeTranscriptionService(),
            micFactory: () => new FakeCaptureService(AudioSourceType.Microphone));

        var options = MakeOptions() with { WhisperModelPath = Path.Combine(_dir, "missing.bin") };
        var ex = await Assert.ThrowsAsync<WhisperModelNotFoundException>(() => engine.StartAsync(options));
        Assert.Contains("Whisper model not found", ex.Message);
    }

    [Fact]
    public async Task Start_NoSourcesEnabled_Throws()
    {
        await using var engine = new RealTranscriptionEngine(new FakeTranscriptionService());
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(MakeOptions(mic: false, system: false)));
    }

    [Fact]
    public async Task StartStopLifecycle_IsClean_AndIdempotent()
    {
        await using var engine = new RealTranscriptionEngine(
            new FakeTranscriptionService(),
            micFactory: () => new FakeCaptureService(AudioSourceType.Microphone));

        await engine.StartAsync(MakeOptions());
        Assert.Equal(TranscriptionSessionState.Recording, (await engine.GetStatusAsync()).State);

        await engine.StopAsync();
        await engine.StopAsync();
        Assert.Equal(TranscriptionSessionState.Stopped, (await engine.GetStatusAsync()).State);
    }

    [Fact]
    public async Task PauseResume_ControlsEventFlow()
    {
        await using var engine = new RealTranscriptionEngine(
            new FakeTranscriptionService(),
            micFactory: () => new FakeCaptureService(AudioSourceType.Microphone));

        await engine.StartAsync(MakeOptions());
        await engine.PauseAsync();
        Assert.Equal(TranscriptionSessionState.Paused, (await engine.GetStatusAsync()).State);

        await engine.ResumeAsync();
        Assert.Equal(TranscriptionSessionState.Recording, (await engine.GetStatusAsync()).State);
        await engine.StopAsync();
    }

    [Fact]
    public async Task IpcServer_StatusStopRoundTrip()
    {
        string pipeName = "lt-test-pipe-" + Guid.NewGuid().ToString("N");
        await using var engine = new RealTranscriptionEngine(
            new FakeTranscriptionService(),
            micFactory: () => new FakeCaptureService(AudioSourceType.Microphone));
        await engine.StartAsync(MakeOptions());

        await using var server = new Ipc.EngineIpcServer(engine, pipeName);
        server.Start();

        var status = await Ipc.EngineIpcClient.TrySendAsync("status", pipeName);
        Assert.NotNull(status);
        Assert.True(status!.Ok);
        Assert.Equal(TranscriptionSessionState.Recording, status.Status!.State);

        var stop = await Ipc.EngineIpcClient.TrySendAsync("stop", pipeName);
        Assert.True(stop!.Ok);
        Assert.Equal(TranscriptionSessionState.Stopped, (await engine.GetStatusAsync()).State);
    }

    [Fact]
    public async Task IpcClient_NoServer_ReturnsNull()
    {
        var response = await Ipc.EngineIpcClient.TrySendAsync("status", "lt-no-such-pipe", connectTimeoutMs: 200);
        Assert.Null(response);
    }

    [Fact]
    public async Task Watchdog_DetectsStall_AndReconnects()
    {
        var stale = TimeSpan.FromSeconds(2);
        int factoryCalls = 0;

        await using var engine = new RealTranscriptionEngine(
            new FakeTranscriptionService(),
            micFactory: () =>
            {
                return Interlocked.Increment(ref factoryCalls) == 1
                    ? (IAudioCaptureService)new StallingCaptureService(AudioSourceType.Microphone, TimeSpan.FromMilliseconds(200))
                    : new FakeCaptureService(AudioSourceType.Microphone);
            },
            captureStaleThreshold: stale);

        await engine.StartAsync(MakeOptions());

        // Wait: stale threshold (2s) + poll interval (max(1, 2/3)=1s) + buffer (1.5s)
        await Task.Delay(TimeSpan.FromSeconds(5));

        var status = await engine.GetStatusAsync();
        await engine.StopAsync();

        Assert.Contains("stalled", status.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, factoryCalls);
    }
}
