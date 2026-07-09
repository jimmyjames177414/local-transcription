using LocalTranscriber.Engine;

namespace LocalTranscriber.Engine.Tests;

public class FakeTranscriptionEngineTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-engine-tests-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private TranscriptionSessionOptions MakeOptions(int intervalMs = 10) => new()
    {
        OutputTextPath = Path.Combine(_dir, "out.txt"),
        OutputJsonlPath = Path.Combine(_dir, "out.jsonl"),
        FakeEventIntervalMs = intervalMs
    };

    private static async Task WaitForEventsAsync(FakeTranscriptionEngine engine, long minCount, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var status = await engine.GetStatusAsync();
            if (status.EventCount >= minCount)
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"Engine did not reach {minCount} events in {timeoutMs}ms.");
    }

    [Fact]
    public async Task Start_TransitionsToRecording_AndEmitsEvents()
    {
        await using var engine = new FakeTranscriptionEngine();
        await engine.StartAsync(MakeOptions());

        var status = await engine.GetStatusAsync();
        Assert.Equal(TranscriptionSessionState.Recording, status.State);

        await WaitForEventsAsync(engine, 3);
        await engine.StopAsync();

        status = await engine.GetStatusAsync();
        Assert.Equal(TranscriptionSessionState.Stopped, status.State);
        Assert.True(status.EventCount >= 3);
    }

    [Fact]
    public async Task Engine_WritesTranscriptFiles()
    {
        var options = MakeOptions();
        await using var engine = new FakeTranscriptionEngine();
        await engine.StartAsync(options);
        await WaitForEventsAsync(engine, 2);
        await engine.StopAsync();

        Assert.True(File.Exists(options.OutputTextPath));
        Assert.True(File.Exists(options.OutputJsonlPath));
        Assert.True(File.ReadAllLines(options.OutputTextPath).Length >= 2);
        Assert.True(File.ReadAllLines(options.OutputJsonlPath).Length >= 2);
    }

    [Fact]
    public async Task Engine_StreamsEvents()
    {
        await using var engine = new FakeTranscriptionEngine();
        await engine.StartAsync(MakeOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int received = 0;
        await foreach (var e in engine.StreamEventsAsync(cts.Token))
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Text));
            if (++received >= 3)
            {
                break;
            }
        }

        Assert.Equal(3, received);
        await engine.StopAsync();
    }

    [Fact]
    public async Task Pause_StopsEmission_Resume_Restarts()
    {
        await using var engine = new FakeTranscriptionEngine();
        await engine.StartAsync(MakeOptions());
        await WaitForEventsAsync(engine, 1);

        await engine.PauseAsync();
        var paused = await engine.GetStatusAsync();
        Assert.Equal(TranscriptionSessionState.Paused, paused.State);

        long countAtPause = paused.EventCount;
        await Task.Delay(200);
        var stillPaused = await engine.GetStatusAsync();
        Assert.Equal(countAtPause, stillPaused.EventCount);

        await engine.ResumeAsync();
        var resumed = await engine.GetStatusAsync();
        Assert.Equal(TranscriptionSessionState.Recording, resumed.State);
        await WaitForEventsAsync(engine, countAtPause + 1);

        await engine.StopAsync();
    }

    [Fact]
    public async Task Stop_IsIdempotent()
    {
        await using var engine = new FakeTranscriptionEngine();
        await engine.StartAsync(MakeOptions());
        await engine.StopAsync();
        await engine.StopAsync();

        var status = await engine.GetStatusAsync();
        Assert.Equal(TranscriptionSessionState.Stopped, status.State);
    }

    [Fact]
    public async Task Stop_WithoutStart_DoesNotThrow()
    {
        await using var engine = new FakeTranscriptionEngine();
        await engine.StopAsync();
        var status = await engine.GetStatusAsync();
        Assert.Equal(TranscriptionSessionState.Stopped, status.State);
    }

    [Fact]
    public async Task Start_WhileRunning_Throws()
    {
        await using var engine = new FakeTranscriptionEngine();
        await engine.StartAsync(MakeOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(MakeOptions()));
        await engine.StopAsync();
    }
}
