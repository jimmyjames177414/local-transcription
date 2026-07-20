using LocalTranscriber.AI;
using LocalTranscriber.Audio;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Engine.Tests;

public class MinutesExportHookTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-minutes-hook-" + Guid.NewGuid().ToString("N"));
    private string _fakeModelPath = "";
    private string _minutesFolder = "";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _fakeModelPath = Path.Combine(_dir, "fake-whisper.bin");
        File.WriteAllBytes(_fakeModelPath, new byte[] { 1 });
        _minutesFolder = Path.Combine(_dir, "meetings");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private sealed class SilentCaptureService : IAudioCaptureService
    {
        public AudioSourceType Source => AudioSourceType.Microphone;
        public event EventHandler<AudioChunk>? ChunkAvailable { add { } remove { } }
        public bool IsCapturing { get; private set; }
        public Task StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default)
        {
            IsCapturing = true;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopTranscriptionService : ILocalTranscriptionService
    {
        public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new TranscriptionResult("", Array.Empty<TranscribedSegment>(), 0, TimeSpan.Zero));
    }

    private sealed class FakeEventStore : ITranscriptEventStore
    {
        public bool Throw { get; set; }
        public List<TranscriptEvent> Events { get; } = new();

        public Task InsertAsync(TranscriptEvent transcriptEvent, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<TranscriptEvent>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => Throw
                ? throw new InvalidOperationException("boom")
                : Task.FromResult<IReadOnlyList<TranscriptEvent>>(Events);

        public Task<DateTimeOffset?> GetLastTimestampAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(Events.Count == 0 ? (DateTimeOffset?)null : Events.Max(e => e.Timestamp));

        public Task DeleteBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string eventId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> SearchSessionIdsAsync(string text, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private TranscriptionSessionOptions MakeOptions() => new()
    {
        OutputTextPath = Path.Combine(_dir, "out.txt"),
        OutputJsonlPath = Path.Combine(_dir, "out.jsonl"),
        EnableMicrophone = true,
        EnableSystemAudio = false,
        WhisperModelPath = _fakeModelPath,
        SpeakerModelDir = _dir,
        ChunkSeconds = 1,
        OverlapMs = 0
    };

    private RealTranscriptionEngine MakeEngine(FakeEventStore eventStore, bool enabled)
        => new(
            new NoopTranscriptionService(),
            micFactory: () => new SilentCaptureService(),
            eventStore: eventStore,
            minutesExport: new MinutesExportConfig { Enabled = enabled, Folder = _minutesFolder },
            notesFolder: _dir);

    [Fact]
    public async Task Stop_WithExportEnabled_WritesMinutesFile_IncludingNotes()
    {
        var options = MakeOptions();
        var store = new FakeEventStore();
        store.Events.Add(new TranscriptEvent("e1", options.SessionId, DateTimeOffset.Now,
            new SpeakerLabel("joe", "Joe", true), AudioSourceType.SystemAudio, "Ship it Friday."));

        var notes = new NotesDocument(options.SessionId);
        notes.Add(NoteSection.Decisions, "Ship Friday");
        File.WriteAllText(Path.Combine(_dir, $"notes-{options.SessionId}.md"), notes.ToMarkdown());

        await using var engine = MakeEngine(store, enabled: true);
        await engine.StartAsync(options);
        await engine.StopAsync();

        string file = Assert.Single(Directory.GetFiles(_minutesFolder, "*.md"));
        string md = File.ReadAllText(file);
        Assert.Contains("type: meeting", md);
        Assert.Contains("Ship it Friday.", md);
        Assert.Contains("- text: \"Ship Friday\"", md);
    }

    [Fact]
    public async Task Stop_WithExportDisabled_WritesNothing()
    {
        await using var engine = MakeEngine(new FakeEventStore(), enabled: false);
        await engine.StartAsync(MakeOptions());
        await engine.StopAsync();

        Assert.False(Directory.Exists(_minutesFolder));
    }

    [Fact]
    public async Task Stop_ExportFailure_DoesNotAffectStop()
    {
        await using var engine = MakeEngine(new FakeEventStore { Throw = true }, enabled: true);
        await engine.StartAsync(MakeOptions());
        await engine.StopAsync();

        Assert.Equal(TranscriptionSessionState.Stopped, (await engine.GetStatusAsync()).State);
    }
}
