using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LocalTranscriber.Agent;
using LocalTranscriber.Agent.OpenAI;
using LocalTranscriber.AI;
using LocalTranscriber.Audio;
using LocalTranscriber.Context;
using LocalTranscriber.Shared;
using LocalTranscriber.Voice;

namespace LocalTranscriber.Voice.Tests;

/// <summary>Headless websocket stand-in: records everything sent, feeds queued server events.</summary>
internal sealed class FakeRealtimeTransport : IRealtimeTransport
{
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();

    public List<string> Sent { get; } = new();
    public int ConnectCalls;
    public Uri? LastUri;
    public IReadOnlyDictionary<string, string>? LastHeaders;
    public bool IsConnected { get; private set; }

    /// <summary>Number of initial ConnectAsync calls that should throw (simulates a cold/reset handshake).</summary>
    public int FailFirstNConnects;

    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        ConnectCalls++;
        if (ConnectCalls <= FailFirstNConnects)
        {
            throw new IOException("simulated cold-connect reset");
        }
        LastUri = uri;
        LastHeaders = headers;
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(string json, CancellationToken cancellationToken = default)
    {
        lock (Sent)
        {
            Sent.Add(json);
        }
        return Task.CompletedTask;
    }

    /// <summary>Blocks until an event is queued or cancelled (never spuriously returns null).</summary>
    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken = default)
        => await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public void EnqueueServerEvent(string json) => _incoming.Writer.TryWrite(json);

    public IReadOnlyList<string> SentSnapshot()
    {
        lock (Sent)
        {
            return Sent.ToArray();
        }
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeAudioOutput : IAgentAudioOutput
{
    public ConcurrentQueue<byte[]> Enqueued { get; } = new();
    public int StopCalls;
    public int FlushCalls;

    public void EnqueuePcm16(byte[] pcm) => Enqueued.Enqueue(pcm);
    public void EnqueueBase64(string base64Pcm) => Enqueued.Enqueue(Convert.FromBase64String(base64Pcm));
    public void Stop() => Interlocked.Increment(ref StopCalls);
    public void Flush() => Interlocked.Increment(ref FlushCalls);
    public long PlayedMilliseconds { get; set; }
    public bool IsPlaying => !Enqueued.IsEmpty;
    public void Dispose() { }
}

internal sealed class FakeTranscriber : ILocalTranscriptionService
{
    private readonly string _text;
    public FakeTranscriber(string text) => _text = text;

    public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new TranscriptionResult(_text, Array.Empty<TranscribedSegment>(), 0.9, TimeSpan.Zero));
}

internal sealed class FakeRecorder : IPushToTalkRecorder
{
    private readonly string? _path;
    public FakeRecorder(string? path) => _path = path;
    public bool IsAvailable => true;
    public int StartCalls;
    public Task StartAsync(CancellationToken cancellationToken = default) { StartCalls++; return Task.CompletedTask; }
    public Task<string?> StopAsync(CancellationToken cancellationToken = default) => Task.FromResult(_path);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeMicStream : IAgentMicStream
{
    private readonly int _frames;
    private readonly int _frameBytes;

    public FakeMicStream(int frames, int frameBytes = 8)
    {
        _frames = frames;
        _frameBytes = frameBytes;
    }

    public event EventHandler<byte[]>? FrameAvailable;
    public bool IsCapturing { get; private set; }
    public bool IsAvailable => true;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        IsCapturing = true;
        for (int i = 0; i < _frames; i++)
        {
            FrameAvailable?.Invoke(this, new byte[_frameBytes]);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsCapturing = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeTailer : ITranscriptEventTailer
{
    private readonly IReadOnlyList<TranscriptEvent> _events;
    public FakeTailer(IReadOnlyList<TranscriptEvent> events) => _events = events;

    public async IAsyncEnumerable<TranscriptEvent> TailAsync(
        TranscriptTailOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var e in _events)
        {
            yield return e;
        }

        if (!options.StopAtEndOfFile)
        {
            // Continuous tail: park until cancelled instead of ending.
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class EmptyContextService : IContextPackService
{
    public Task<ContextPack> LoadAsync(ContextPackOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(ContextPack.Empty);
    public Task<IReadOnlyList<string>> ListDocumentsAsync(ContextPackOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task<ContextDocument?> ReadDocumentAsync(ContextPackOptions options, string fileName, CancellationToken cancellationToken = default)
        => Task.FromResult<ContextDocument?>(null);
    public Task<IReadOnlyList<string>> ValidateAsync(ContextPackOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}

internal static class TestEvents
{
    public static TranscriptEvent Line(string text, int seconds = 0)
        => new(
            Id: $"e{seconds}-{Math.Abs(text.GetHashCode()):x}",
            SessionId: "s1",
            Timestamp: DateTimeOffset.Parse("2026-07-10T10:00:00Z").AddSeconds(seconds),
            Speaker: new SpeakerLabel("sp1", "Alex", false),
            Source: AudioSourceType.SystemAudio,
            Text: text);
}
