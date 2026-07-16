using LocalTranscriber.Audio;
using LocalTranscriber.Engine;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Engine.Tests;

public class CaptureHostTests
{
    /// <summary>An in-memory capture service that tracks lifecycle calls.</summary>
    private sealed class FakeCaptureService : IAudioCaptureService
    {
        private readonly bool _throwOnStart;

        public FakeCaptureService(bool throwOnStart = false) => _throwOnStart = throwOnStart;

        public AudioSourceType Source => AudioSourceType.Microphone;
        public event EventHandler<AudioChunk>? ChunkAvailable;
        public bool IsCapturing { get; private set; }
        public int DisposedCount { get; private set; }

        public Task StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default)
        {
            if (_throwOnStart) throw new InvalidOperationException("Simulated device error.");
            IsCapturing = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposedCount++;
            IsCapturing = false;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Builds a CaptureHost with the given mic factory and a noop system factory.</summary>
    private static CaptureHost BuildHost(
        Func<IAudioCaptureService> micFactory,
        Func<bool>? isActive = null,
        TimeSpan? staleThreshold = null)
        => new(
            micFactory,
            () => new FakeCaptureService(),
            warning => { },
            isActive: isActive ?? (() => true),
            staleThreshold: staleThreshold ?? TimeSpan.FromSeconds(60));

    [Fact]
    public async Task StartStop_Normal_StartsAndStopsCleanly()
    {
        var mic = new FakeCaptureService();
        var host = BuildHost(() => mic);

        await host.StartAsync(enableMic: true, enableSystem: false, CancellationToken.None);
        Assert.True(mic.IsCapturing);

        await host.StopAsync();
        Assert.Equal(1, mic.DisposedCount);
    }

    [Fact]
    public async Task DoubleStop_IsIdempotent()
    {
        var mic = new FakeCaptureService();
        var host = BuildHost(() => mic);

        await host.StartAsync(enableMic: true, enableSystem: false, CancellationToken.None);
        await host.StopAsync();
        await host.StopAsync(); // second stop must not throw

        // The mic is disposed once by StopAsync; the second StopAsync finds _mic null, so no double-dispose.
        Assert.Equal(1, mic.DisposedCount);
    }

    /// <summary>
    /// MED-4 regression guard: when the reconnect factory returns a service whose StartAsync throws,
    /// the CaptureHost must dispose that service rather than leaking it.
    /// </summary>
    [Fact]
    public async Task Reconnect_StartAsyncThrows_DisposesCapture()
    {
        // The initial mic is healthy. The reconnect factory returns a mic whose StartAsync throws.
        var initialMic = new FakeCaptureService();
        var reconnectMic = new FakeCaptureService(throwOnStart: true);
        bool reconnectCalled = false;

        IAudioCaptureService MicFactory()
        {
            if (!reconnectCalled)
            {
                reconnectCalled = true;
                return initialMic;
            }
            return reconnectMic;
        }

        // Use a very short stale threshold so the watchdog fires quickly. Note: the poll interval
        // floor is 1 second (Math.Max(1, staleSeconds / 3)), so we wait 1.5s for the first check.
        var host = new CaptureHost(
            MicFactory,
            () => new FakeCaptureService(),
            warning => { },
            isActive: () => true,
            staleThreshold: TimeSpan.FromMilliseconds(100));

        await host.StartAsync(enableMic: true, enableSystem: false, CancellationToken.None);

        // Wait for the watchdog initial delay + poll interval (~1.1s) then a little extra.
        await Task.Delay(1500);

        await host.StopAsync();

        // The reconnect capture whose StartAsync threw must have been disposed by the try/finally.
        Assert.Equal(1, reconnectMic.DisposedCount);
    }
}
