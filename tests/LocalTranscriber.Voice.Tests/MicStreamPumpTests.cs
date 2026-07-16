using LocalTranscriber.Audio;
using LocalTranscriber.Voice;

namespace LocalTranscriber.Voice.Tests;

public class MicStreamPumpTests
{
    private sealed class FakeMicStream : IAgentMicStream
    {
        public event EventHandler<byte[]>? FrameAvailable;
        public bool IsCapturing { get; private set; }
        public bool IsAvailable => true;
        public int DisposedCount { get; private set; }
        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalled = true;
            IsCapturing = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalled = true;
            IsCapturing = false;
            return Task.CompletedTask;
        }

        public void EmitFrame(byte[] frame) => FrameAvailable?.Invoke(this, frame);

        public ValueTask DisposeAsync()
        {
            DisposedCount++;
            IsCapturing = false;
            return ValueTask.CompletedTask;
        }
    }

    private static (MicStreamPump pump, FakeMicStream mic) BuildPump()
    {
        var mic = new FakeMicStream();
        var pump = new MicStreamPump(
            () => mic,
            (frame, ct) => Task.CompletedTask);
        return (pump, mic);
    }

    [Fact]
    public async Task StartStop_Normal_StartsAndStopsCleanly()
    {
        var (pump, mic) = BuildPump();

        await pump.StartAsync(CancellationToken.None);
        Assert.True(mic.IsCapturing);

        await pump.StopAsync();
        Assert.Equal(1, mic.DisposedCount);
    }

    [Fact]
    public async Task DoubleStart_IsIdempotent()
    {
        var calls = 0;
        FakeMicStream? first = null;
        var pump = new MicStreamPump(
            () =>
            {
                calls++;
                first ??= new FakeMicStream();
                return first;
            },
            (_, _) => Task.CompletedTask);

        await pump.StartAsync(CancellationToken.None);
        await pump.StartAsync(CancellationToken.None); // second call must be a no-op

        Assert.Equal(1, calls); // factory only called once
        await pump.StopAsync();
    }

    /// <summary>
    /// MED-5 regression guard: a concurrent Start + Stop must not leave the mic open (disposed).
    /// </summary>
    [Fact]
    public async Task ConcurrentStartStop_MicIsDisposed()
    {
        var mic = new FakeMicStream();
        var pump = new MicStreamPump(() => mic, (_, _) => Task.CompletedTask);

        // Fire start and stop concurrently. Regardless of interleaving, the mic must end up disposed.
        await Task.WhenAll(
            pump.StartAsync(CancellationToken.None),
            pump.StopAsync());

        // One of two outcomes: Start ran first (mic disposed by Stop) or Stop ran first (Start was
        // a no-op after Stop cleared the state — mic may not have been created). Either way, if the
        // mic was started it must be disposed.
        if (mic.StartCalled)
        {
            Assert.Equal(1, mic.DisposedCount);
        }
    }

    [Fact]
    public async Task Cancellation_StopsCleanly()
    {
        var mic = new FakeMicStream();
        var cts = new CancellationTokenSource();
        var pump = new MicStreamPump(() => mic, async (frame, ct) =>
        {
            await Task.Delay(10, ct);
        });

        await pump.StartAsync(cts.Token);
        mic.EmitFrame(new byte[10]);

        cts.Cancel();
        await pump.StopAsync(); // must not throw even after cancellation

        Assert.Equal(1, mic.DisposedCount);
    }
}
