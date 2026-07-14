using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage.Tests;

public class SessionDeletionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-del-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppConfig _config;
    private readonly SqliteSessionStore _sessions;
    private readonly SqliteTranscriptEventStore _events;
    private readonly SessionDeletionService _service;

    public SessionDeletionServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _config = new AppConfig
        {
            DatabasePath = Path.Combine(_dir, "test.sqlite"),
            MinutesExport = new MinutesExportConfig { Folder = Path.Combine(_dir, "meetings") },
            Agent = { AgentOutputFolder = _dir }
        };
        var db = new SqliteDatabase(_config.DatabasePath);
        _sessions = new SqliteSessionStore(db);
        _events = new SqliteTranscriptEventStore(db);
        _service = new SessionDeletionService(_config, _sessions, _events);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task<SessionRecord> SeedSessionWithFilesAsync(string id)
    {
        string txt = Path.Combine(_dir, $"{id}.txt");
        string jsonl = Path.Combine(_dir, $"{id}.jsonl");
        File.WriteAllText(txt, "transcript");
        File.WriteAllText(jsonl, "{}");
        File.WriteAllText(Path.Combine(_dir, $"notes-{id}.md"), "# notes");
        var record = new SessionRecord(id, DateTimeOffset.Now, DateTimeOffset.Now, txt, jsonl, "stopped");
        await _sessions.CreateAsync(record);
        await _events.InsertAsync(new TranscriptEvent("e-" + id, id, DateTimeOffset.Now,
            new SpeakerLabel("x", "Joe", false), AudioSourceType.SystemAudio, "hello"));
        return record;
    }

    [Fact]
    public async Task ListFiles_ReturnsExistingFilesWithSizes()
    {
        await SeedSessionWithFilesAsync("s1");

        var files = await _service.ListFilesAsync("s1");

        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.True(f.SizeBytes > 0));
        Assert.Contains(files, f => f.Name == "s1.txt");
        Assert.Contains(files, f => f.Name == "notes-s1.md");
    }

    [Fact]
    public async Task ListFiles_SkipsMissingFiles()
    {
        var record = new SessionRecord("s2", DateTimeOffset.Now, null, Path.Combine(_dir, "gone.txt"), Path.Combine(_dir, "gone.jsonl"), "stopped");
        await _sessions.CreateAsync(record);

        Assert.Empty(await _service.ListFilesAsync("s2"));
    }

    [Fact]
    public async Task Delete_RemovesFilesRowsAndOptionallyMinutes()
    {
        var record = await SeedSessionWithFilesAsync("s1");
        string minutesPath = MinutesExporter.Export(record, Array.Empty<TranscriptEvent>(), null, _config.MinutesExport.Folder);
        Assert.True(File.Exists(minutesPath));

        await _service.DeleteAsync("s1", alsoRemoveMinutes: true);

        Assert.False(File.Exists(record.OutputTextPath));
        Assert.False(File.Exists(record.OutputJsonlPath));
        Assert.False(File.Exists(Path.Combine(_dir, "notes-s1.md")));
        Assert.False(File.Exists(minutesPath));
        Assert.Null(await _sessions.GetAsync("s1"));
        Assert.Empty(await _events.ListBySessionAsync("s1"));
    }

    [Fact]
    public async Task Delete_KeepsMinutes_WhenNotRequested()
    {
        var record = await SeedSessionWithFilesAsync("s1");
        string minutesPath = MinutesExporter.Export(record, Array.Empty<TranscriptEvent>(), null, _config.MinutesExport.Folder);

        await _service.DeleteAsync("s1", alsoRemoveMinutes: false);

        Assert.True(File.Exists(minutesPath));
        Assert.Null(await _sessions.GetAsync("s1"));
    }

    [Fact]
    public async Task Delete_ToleratesAlreadyMissingFiles()
    {
        var record = await SeedSessionWithFilesAsync("s1");
        File.Delete(record.OutputTextPath);

        await _service.DeleteAsync("s1", alsoRemoveMinutes: false);

        Assert.Null(await _sessions.GetAsync("s1"));
    }
}
