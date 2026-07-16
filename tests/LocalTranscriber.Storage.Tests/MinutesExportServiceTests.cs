using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage.Tests;

public class MinutesExportServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-minutes-svc-" + Guid.NewGuid().ToString("N"));

    public MinutesExportServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        public List<SessionRecord> Sessions { get; } = new();
        public Task CreateAsync(SessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task EndAsync(string sessionId, DateTimeOffset endedAt, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Sessions.FirstOrDefault(s => s.Id == sessionId));
        public Task<IReadOnlyList<SessionRecord>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionRecord>>(Sessions);
        public Task UpdateTitleAsync(string sessionId, string? title, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionSummary>> ListSummariesAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task ReopenAsync(string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeEventStore : ITranscriptEventStore
    {
        public Task InsertAsync(TranscriptEvent e, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TranscriptEvent>> ListBySessionAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TranscriptEvent>>(Array.Empty<TranscriptEvent>());
        public Task<DateTimeOffset?> GetLastTimestampAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult<DateTimeOffset?>(null);
        public Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SearchSessionIdsAsync(string text, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static SessionRecord Session(string id, DateTimeOffset startedAt)
        => new(id, startedAt, startedAt.AddMinutes(10), "t.txt", "t.jsonl", "stopped");

    private MinutesExportService MakeService(FakeSessionStore sessions)
    {
        var config = new AppConfig
        {
            DatabasePath = Path.Combine(_dir, "unused.sqlite"),
            MinutesExport = new MinutesExportConfig { Folder = Path.Combine(_dir, "meetings") }
        };
        return new MinutesExportService(config, sessions, new FakeEventStore());
    }

    [Fact]
    public async Task Export_DefaultsToMostRecentSession()
    {
        var sessions = new FakeSessionStore();
        sessions.Sessions.Add(Session("older", DateTimeOffset.Now.AddHours(-2)));
        sessions.Sessions.Add(Session("newest", DateTimeOffset.Now.AddMinutes(-30)));

        string path = await MakeService(sessions).ExportAsync();

        Assert.Contains("meeting-newest", Path.GetFileName(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Export_ResolvesSessionIdPrefix()
    {
        var sessions = new FakeSessionStore();
        sessions.Sessions.Add(Session("abc123def", DateTimeOffset.Now.AddHours(-1)));

        string path = await MakeService(sessions).ExportAsync("abc123");

        Assert.Contains("meeting-abc123def", Path.GetFileName(path));
    }

    [Fact]
    public async Task Export_UnknownSession_ThrowsFriendlyMessage()
    {
        var sessions = new FakeSessionStore();
        sessions.Sessions.Add(Session("abc", DateTimeOffset.Now));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => MakeService(sessions).ExportAsync("zzz"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task Export_NoSessions_ThrowsFriendlyMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => MakeService(new FakeSessionStore()).ExportAsync());
        Assert.Contains("No recorded sessions", ex.Message);
    }
}
