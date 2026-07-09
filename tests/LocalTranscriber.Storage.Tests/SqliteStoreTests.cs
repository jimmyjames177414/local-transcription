using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage.Tests;

public class SqliteStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-sqlite-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteDatabase _db;

    public SqliteStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new SqliteDatabase(Path.Combine(_dir, "test.sqlite"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Database_InitializesAndSessionRoundTrips()
    {
        var store = new SqliteSessionStore(_db);
        var session = new SessionRecord("s1", DateTimeOffset.Now, null, "a.txt", "a.jsonl", "recording");
        await store.CreateAsync(session);

        var loaded = await store.GetAsync("s1");
        Assert.NotNull(loaded);
        Assert.Equal("recording", loaded!.Status);
        Assert.Null(loaded.EndedAt);

        await store.EndAsync("s1", DateTimeOffset.Now, "stopped");
        loaded = await store.GetAsync("s1");
        Assert.Equal("stopped", loaded!.Status);
        Assert.NotNull(loaded.EndedAt);

        var all = await store.ListAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task TranscriptEvents_InsertAndListBySession()
    {
        var sessions = new SqliteSessionStore(_db);
        await sessions.CreateAsync(new SessionRecord("s1", DateTimeOffset.Now, null, "a.txt", "a.jsonl", "recording"));

        var store = new SqliteTranscriptEventStore(_db);
        var e = new TranscriptEvent("e1", "s1", DateTimeOffset.Now,
            new SpeakerLabel("speaker_1", "Speaker 1", false), AudioSourceType.SystemAudio,
            "Hello.", 0.9, 0, 1500);
        await store.InsertAsync(e);
        await store.InsertAsync(e with { Id = "e2", Text = "World." });

        var events = await store.ListBySessionAsync("s1");
        Assert.Equal(2, events.Count);
        Assert.Equal("Hello.", events[0].Text);
        Assert.Equal(AudioSourceType.SystemAudio, events[0].Source);
        Assert.Equal(1500, events[0].EndMs);

        var none = await store.ListBySessionAsync("other");
        Assert.Empty(none);
    }

    [Fact]
    public async Task Speakers_CreateRenameForget()
    {
        var store = new SqliteKnownSpeakerStore(_db);
        await store.CreateAsync("Speaker 2");

        Assert.True(await store.RenameAsync("Speaker 2", "Joe"));
        var joe = await store.GetByNameAsync("Joe");
        Assert.NotNull(joe);
        Assert.Null(await store.GetByNameAsync("Speaker 2"));

        Assert.True(await store.ForgetAsync("Joe"));
        Assert.Null(await store.GetByNameAsync("Joe"));
        Assert.False(await store.ForgetAsync("Joe"));
    }

    [Fact]
    public async Task Rename_UnknownSpeaker_CreatesIt()
    {
        var store = new SqliteKnownSpeakerStore(_db);
        Assert.True(await store.RenameAsync("Speaker 9", "Martina"));
        Assert.NotNull(await store.GetByNameAsync("Martina"));
    }

    [Fact]
    public async Task MarkSeen_UpdatesLastSeenAndSampleCount()
    {
        var store = new SqliteKnownSpeakerStore(_db);
        var speaker = await store.CreateAsync("Joe");

        await store.MarkSeenAsync(speaker.Id, DateTimeOffset.Now, sampleCountDelta: 3);
        var loaded = await store.GetByNameAsync("Joe");
        Assert.Equal(3, loaded!.SampleCount);
        Assert.NotNull(loaded.LastSeenAt);
    }

    [Fact]
    public async Task Embeddings_StoreAndRetrieveBlob()
    {
        var speakers = new SqliteKnownSpeakerStore(_db);
        var joe = await speakers.CreateAsync("Joe");

        var store = new SqliteSpeakerEmbeddingStore(_db);
        byte[] blob = { 1, 2, 3, 4, 5, 6, 7, 8 };
        await store.AddAsync(new StoredEmbedding("emb1", joe.Id, blob, 2, "fake-model", DateTimeOffset.Now, "s1"));

        var list = await store.ListBySpeakerAsync(joe.Id);
        Assert.Single(list);
        Assert.Equal(blob, list[0].Embedding);
        Assert.Equal(2, list[0].Dimensions);
        Assert.Equal("fake-model", list[0].ModelName);

        var all = await store.ListAllAsync();
        Assert.Single(all);

        await store.DeleteBySpeakerAsync(joe.Id);
        Assert.Empty(await store.ListBySpeakerAsync(joe.Id));
    }

    [Fact]
    public async Task Forget_RemovesEmbeddingsToo()
    {
        var speakers = new SqliteKnownSpeakerStore(_db);
        var joe = await speakers.CreateAsync("Joe");
        var embeddings = new SqliteSpeakerEmbeddingStore(_db);
        await embeddings.AddAsync(new StoredEmbedding("emb1", joe.Id, new byte[] { 1 }, 1, "m", DateTimeOffset.Now, null));

        await speakers.ForgetAsync("Joe");
        Assert.Empty(await embeddings.ListBySpeakerAsync(joe.Id));
    }
}
