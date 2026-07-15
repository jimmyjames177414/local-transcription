using System.Threading.Channels;
using LocalTranscriber.Audio;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Voice;

/// <summary>
/// Streams microphone frames to the realtime socket for the pushToTalk / continuous input modes.
/// WASAPI callbacks hand 24 kHz PCM16 frames to a bounded channel; a pump task drains it and sends
/// each frame via the supplied <c>sendFrame</c> delegate, so the capture thread never blocks on the
/// socket. Extracted from <see cref="RealtimeVoiceSession"/>; behavior is unchanged.
/// </summary>
internal sealed class MicStreamPump
{
    private readonly Func<IAgentMicStream> _micStreamFactory;
    private readonly Func<byte[], CancellationToken, Task> _sendFrame;

    private IAgentMicStream? _micStream;
    private Channel<byte[]>? _channel;
    private Task? _pump;
    private int _framesSent;
    private int _bytesSent;

    public MicStreamPump(Func<IAgentMicStream> micStreamFactory, Func<byte[], CancellationToken, Task> sendFrame)
    {
        _micStreamFactory = micStreamFactory;
        _sendFrame = sendFrame;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_micStream is not null)
        {
            return;
        }

        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        _micStream = _micStreamFactory();
        _micStream.FrameAvailable += OnMicFrame;
        _pump = Task.Run(() => PumpAsync(ct), CancellationToken.None);
        await _micStream.StartAsync(ct).ConfigureAwait(false);
    }

    private void OnMicFrame(object? sender, byte[] frame)
    {
        if (_channel?.Writer.TryWrite(frame) == true)
        {
            Interlocked.Increment(ref _framesSent);
            Interlocked.Add(ref _bytesSent, frame.Length);
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _channel!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _sendFrame(frame, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn("voice", $"Mic send pump ended: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads and resets the frames/bytes-sent counters. Call before <see cref="StopAsync"/> to gate
    /// a manual turn (the server rejects buffers under ~100 ms of audio).
    /// </summary>
    public (int Frames, int Bytes) TakeCounters()
        => (Interlocked.Exchange(ref _framesSent, 0), Interlocked.Exchange(ref _bytesSent, 0));

    public async Task StopAsync()
    {
        if (_micStream is not null)
        {
            _micStream.FrameAvailable -= OnMicFrame;
            try { await _micStream.StopAsync().ConfigureAwait(false); } catch { }
            await _micStream.DisposeAsync().ConfigureAwait(false);
            _micStream = null;
        }

        _channel?.Writer.TryComplete();
        if (_pump is not null)
        {
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
            _pump = null;
        }
        _channel = null;
    }
}
