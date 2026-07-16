using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage.Tests;

public class SessionRecoveryServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-recovery-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteDatabase _db;
    private readonly SqliteSessionStore _sessions;
    private readonly SqliteTranscriptEventStore _events;

    public SessionRecoveryServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new SqliteDatabase(Path.Combine(_dir, "test.sqlite"));
        _sessions = new SqliteSessionStore(_db);
        _events = new SqliteTranscriptEventStore(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static TranscriptEvent Event(string id, string sessionId, DateTimeOffset at) => new(
        id, sessionId, at, new SpeakerLabel("session_speaker_1", "Speaker 1", false),
        AudioSourceType.SystemAudio, "hi", 0.9, 0, 1000);

    [Fact]
    public async Task Recovers_RecordingSession_FinalizesToInterruptedWithLastEventTimestamp()
    {
        var started = DateTimeOffset.Parse("2026-07-16T10:00:00+00:00");
        var lastEvent = started.AddMinutes(24);
        await _sessions.CreateAsync(new SessionRecord("s1", started, null, "a.txt", "a.jsonl", "recording"));
        await _events.InsertAsync(Event("e1", "s1", started));
        await _events.InsertAsync(Event("e2", "s1", lastEvent));

        var recovered = await new SessionRecoveryService(_sessions, _events).RecoverOrphanedSessionsAsync();

        Assert.Equal(new[] { "s1" }, recovered);
        var loaded = await _sessions.GetAsync("s1");
        Assert.Equal(SessionRecoveryService.InterruptedStatus, loaded!.Status);
        Assert.Equal(lastEvent, loaded.EndedAt);
    }

    [Fact]
    public async Task Recovers_RecordingSessionWithNoEvents_FallsBackToStartedAt()
    {
        var started = DateTimeOffset.Parse("2026-07-16T10:00:00+00:00");
        await _sessions.CreateAsync(new SessionRecord("empty", started, null, "a.txt", "a.jsonl", "recording"));

        await new SessionRecoveryService(_sessions, _events).RecoverOrphanedSessionsAsync();

        var loaded = await _sessions.GetAsync("empty");
        Assert.Equal(SessionRecoveryService.InterruptedStatus, loaded!.Status);
        Assert.Equal(started, loaded.EndedAt);
    }

    [Fact]
    public async Task LeavesStoppedSessionsUntouched()
    {
        var started = DateTimeOffset.Parse("2026-07-16T10:00:00+00:00");
        var ended = started.AddMinutes(10);
        await _sessions.CreateAsync(new SessionRecord("done", started, ended, "a.txt", "a.jsonl", "stopped"));

        var recovered = await new SessionRecoveryService(_sessions, _events).RecoverOrphanedSessionsAsync();

        Assert.Empty(recovered);
        var loaded = await _sessions.GetAsync("done");
        Assert.Equal("stopped", loaded!.Status);
        Assert.Equal(ended, loaded.EndedAt);
    }

    [Fact]
    public async Task GetLastTimestamp_ReturnsMax_OrNullWhenNoEvents()
    {
        var t0 = DateTimeOffset.Parse("2026-07-16T10:00:00+00:00");
        await _sessions.CreateAsync(new SessionRecord("s1", t0, null, "a.txt", "a.jsonl", "recording"));
        await _events.InsertAsync(Event("e1", "s1", t0));
        await _events.InsertAsync(Event("e2", "s1", t0.AddMinutes(5)));
        await _events.InsertAsync(Event("e3", "s1", t0.AddMinutes(2)));

        Assert.Equal(t0.AddMinutes(5), await _events.GetLastTimestampAsync("s1"));
        Assert.Null(await _events.GetLastTimestampAsync("no-such-session"));
    }

    [Fact]
    public async Task ListSummaries_SurfacesLastEventTimestamp()
    {
        var t0 = DateTimeOffset.Parse("2026-07-16T10:00:00+00:00");
        await _sessions.CreateAsync(new SessionRecord("s1", t0, null, "a.txt", "a.jsonl", "recording"));
        await _events.InsertAsync(Event("e1", "s1", t0));
        await _events.InsertAsync(Event("e2", "s1", t0.AddMinutes(24)));

        var summary = Assert.Single(await _sessions.ListSummariesAsync());
        Assert.Equal(t0.AddMinutes(24), summary.LastEventAt);
        Assert.Equal(2, summary.EventCount);
    }
}
