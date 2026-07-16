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
    public async Task Sessions_TitleRoundTripsAndUpdates()
    {
        var store = new SqliteSessionStore(_db);
        await store.CreateAsync(new SessionRecord("s1", DateTimeOffset.Now, null, "a.txt", "a.jsonl", "recording", "Sprint planning"));

        var loaded = await store.GetAsync("s1");
        Assert.Equal("Sprint planning", loaded!.Title);

        await store.UpdateTitleAsync("s1", "Sprint planning #42");
        Assert.Equal("Sprint planning #42", (await store.GetAsync("s1"))!.Title);

        await store.UpdateTitleAsync("s1", null);
        Assert.Null((await store.GetAsync("s1"))!.Title);
    }

    [Fact]
    public async Task Sessions_TitleColumn_MigratesOldDatabase()
    {
        // Simulate a pre-title database: create the old sessions schema directly, then open
        // through SqliteDatabase and confirm the column is added and usable.
        string path = Path.Combine(_dir, "old.sqlite");
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE sessions (
                    id TEXT PRIMARY KEY, started_at TEXT NOT NULL, ended_at TEXT,
                    output_text_path TEXT NOT NULL, output_jsonl_path TEXT NOT NULL, status TEXT NOT NULL);
                INSERT INTO sessions VALUES ('old1', '2026-07-01T10:00:00+00:00', NULL, 'a.txt', 'a.jsonl', 'stopped');
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new SqliteSessionStore(new SqliteDatabase(path));
        var loaded = await store.GetAsync("old1");
        Assert.NotNull(loaded);
        Assert.Null(loaded!.Title);

        await store.UpdateTitleAsync("old1", "Renamed later");
        Assert.Equal("Renamed later", (await store.GetAsync("old1"))!.Title);
    }

    [Fact]
    public async Task Sessions_DeleteRemovesOnlyTargetRows()
    {
        var sessions = new SqliteSessionStore(_db);
        var events = new SqliteTranscriptEventStore(_db);
        await sessions.CreateAsync(new SessionRecord("s1", DateTimeOffset.Now, null, "a.txt", "a.jsonl", "stopped"));
        await sessions.CreateAsync(new SessionRecord("s2", DateTimeOffset.Now, null, "b.txt", "b.jsonl", "stopped"));
        await events.InsertAsync(new TranscriptEvent("e1", "s1", DateTimeOffset.Now,
            new SpeakerLabel("x", "Joe", false), AudioSourceType.SystemAudio, "hello"));
        await events.InsertAsync(new TranscriptEvent("e2", "s2", DateTimeOffset.Now,
            new SpeakerLabel("x", "Ana", false), AudioSourceType.SystemAudio, "world"));

        await events.DeleteBySessionAsync("s1");
        await sessions.DeleteAsync("s1");

        Assert.Null(await sessions.GetAsync("s1"));
        Assert.NotNull(await sessions.GetAsync("s2"));
        Assert.Empty(await events.ListBySessionAsync("s1"));
        Assert.Single(await events.ListBySessionAsync("s2"));
    }

    [Fact]
    public async Task Events_SearchSessionIds_MatchesAndEscapes()
    {
        var sessions = new SqliteSessionStore(_db);
        await sessions.CreateAsync(new SessionRecord("s1", DateTimeOffset.Now, null, "a.txt", "a.jsonl", "stopped"));
        await sessions.CreateAsync(new SessionRecord("s2", DateTimeOffset.Now, null, "b.txt", "b.jsonl", "stopped"));
        var events = new SqliteTranscriptEventStore(_db);
        await events.InsertAsync(new TranscriptEvent("e1", "s1", DateTimeOffset.Now,
            new SpeakerLabel("x", "Joe", false), AudioSourceType.SystemAudio, "Deployment moves to Friday"));
        await events.InsertAsync(new TranscriptEvent("e2", "s2", DateTimeOffset.Now,
            new SpeakerLabel("x", "Ana", false), AudioSourceType.SystemAudio, "We are 50%_done with QA"));

        Assert.Equal(new[] { "s1" }, await events.SearchSessionIdsAsync("deployment"));
        Assert.Empty(await events.SearchSessionIdsAsync("kubernetes"));
        // LIKE metacharacters must be treated literally.
        Assert.Equal(new[] { "s2" }, await events.SearchSessionIdsAsync("50%_done"));
        Assert.Empty(await events.SearchSessionIdsAsync("50änderungen"));
        Assert.Empty(await events.SearchSessionIdsAsync("   "));
    }

    [Fact]
    public async Task Sessions_ListSummaries_CountsAndDistinctSpeakers()
    {
        var sessions = new SqliteSessionStore(_db);
        var events = new SqliteTranscriptEventStore(_db);
        await sessions.CreateAsync(new SessionRecord("s1", DateTimeOffset.Now.AddHours(-1), null, "a.txt", "a.jsonl", "stopped"));
        await sessions.CreateAsync(new SessionRecord("s2", DateTimeOffset.Now, null, "b.txt", "b.jsonl", "stopped"));
        foreach (var (id, speaker) in new[] { ("e1", "Me"), ("e2", "Joe"), ("e3", "Joe"), ("e4", "Martina") })
        {
            await events.InsertAsync(new TranscriptEvent(id, "s1", DateTimeOffset.Now,
                new SpeakerLabel("x", speaker, false), AudioSourceType.SystemAudio, "text"));
        }

        var summaries = await sessions.ListSummariesAsync();

        Assert.Equal(2, summaries.Count);
        Assert.Equal("s2", summaries[0].Session.Id); // newest first
        var s1 = summaries.Single(s => s.Session.Id == "s1");
        Assert.Equal(4, s1.EventCount);
        Assert.Equal(3, s1.SpeakerNames.Count);
        Assert.Contains("Joe", s1.SpeakerNames);
        var s2 = summaries.Single(s => s.Session.Id == "s2");
        Assert.Equal(0, s2.EventCount);
        Assert.Empty(s2.SpeakerNames);
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
    public async Task SpeakerAliases_UpsertResolveAndListBySession()
    {
        var store = new SqliteSpeakerAliasStore(_db);

        Assert.Null(await store.ResolveAsync("s1", "session_speaker_1"));

        await store.UpsertAsync("s1", "session_speaker_1", "known_a");
        await store.UpsertAsync("s1", "session_speaker_2", "known_b");
        Assert.Equal("known_a", await store.ResolveAsync("s1", "session_speaker_1"));

        // Upsert on the same (session, speaker) key updates rather than duplicates.
        await store.UpsertAsync("s1", "session_speaker_1", "known_c");
        Assert.Equal("known_c", await store.ResolveAsync("s1", "session_speaker_1"));

        var forSession = await store.ListForSessionAsync("s1");
        Assert.Equal(2, forSession.Count);
        Assert.Empty(await store.ListForSessionAsync("other"));
    }

    [Fact]
    public async Task SpeakerNameResolver_ResolvesSessionAliasAndEnrolledIds()
    {
        var speakers = new SqliteKnownSpeakerStore(_db);
        var aliases = new SqliteSpeakerAliasStore(_db);
        var joe = await speakers.CreateAsync("Joe");
        await aliases.UpsertAsync("s1", "session_speaker_1", joe.Id);

        // Zero TTL so every call re-reads and we exercise the DB path, not the cache.
        var resolver = new SqliteSpeakerNameResolver(aliases, speakers, TimeSpan.Zero);

        // Session-local id resolves via the alias table -> display name.
        Assert.Equal("Joe", await resolver.ResolveDisplayNameAsync("s1", "session_speaker_1"));
        // Enrolled id resolves directly against known_speakers.
        Assert.Equal("Joe", await resolver.ResolveDisplayNameAsync("s1", joe.Id));
        // No alias for this session speaker -> null.
        Assert.Null(await resolver.ResolveDisplayNameAsync("s1", "session_speaker_2"));
        // Alias is session-scoped: same label in another session is not resolved.
        Assert.Null(await resolver.ResolveDisplayNameAsync("s2", "session_speaker_1"));
        // Reserved ids never override.
        Assert.Null(await resolver.ResolveDisplayNameAsync("s1", "mic"));
        Assert.Null(await resolver.ResolveDisplayNameAsync("s1", "speaker_unknown"));
    }

    [Fact]
    public async Task SpeakerNameResolver_CachesWithinTtl_ReflectsChangeAfterExpiry()
    {
        var speakers = new SqliteKnownSpeakerStore(_db);
        var aliases = new SqliteSpeakerAliasStore(_db);
        var joe = await speakers.CreateAsync("Joe");
        await aliases.UpsertAsync("s1", "session_speaker_1", joe.Id);

        // TTL must comfortably exceed the intervening RenameAsync DB write, or the cache can expire
        // early on a slow CI runner and the "still cached" assertion below flakes (returns "Joseph").
        var resolver = new SqliteSpeakerNameResolver(aliases, speakers, TimeSpan.FromMilliseconds(500));
        Assert.Equal("Joe", await resolver.ResolveDisplayNameAsync("s1", "session_speaker_1"));

        await speakers.RenameAsync("Joe", "Joseph");
        // Still cached as the old name inside the TTL window.
        Assert.Equal("Joe", await resolver.ResolveDisplayNameAsync("s1", "session_speaker_1"));

        await Task.Delay(700); // > TTL, so the cache expires and the new name is resolved.
        Assert.Equal("Joseph", await resolver.ResolveDisplayNameAsync("s1", "session_speaker_1"));
    }

    [Fact]
    public async Task Rename_UnknownSpeaker_ReturnsFalse()
    {
        // The old behavior silently created an embedding-less speaker (a trap).
        // The new behavior returns false so callers can show guidance instead.
        var store = new SqliteKnownSpeakerStore(_db);
        Assert.False(await store.RenameAsync("Speaker 9", "Martina"));
        Assert.Null(await store.GetByNameAsync("Martina"));
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

    [Fact]
    public async Task ReopenAsync_SetsStatusRecordingAndClearsEndedAt()
    {
        // arrange: create + end a session
        var store = new SqliteSessionStore(_db);
        var id = Guid.NewGuid().ToString("N");
        await store.CreateAsync(new SessionRecord(id, DateTimeOffset.UtcNow, null, "a.txt", "a.jsonl", "recording"));
        await store.EndAsync(id, DateTimeOffset.UtcNow, "stopped");

        // act
        await store.ReopenAsync(id);

        // assert
        var session = await store.GetAsync(id);
        Assert.NotNull(session);
        Assert.Equal("recording", session!.Status);
        Assert.Null(session.EndedAt);
    }

    // ── EventSpeakerOverrideStore ─────────────────────────────────────────────

    [Fact]
    public async Task EventSpeakerOverrides_UpsertResolveAndList()
    {
        var store = new SqliteEventSpeakerOverrideStore(_db);

        Assert.Null(await store.ResolveAsync("s1", "evt1"));

        await store.UpsertAsync("s1", "evt1", "Bob", null);
        await store.UpsertAsync("s1", "evt2", "Carol", "known-carol-id");

        Assert.Equal("Bob", await store.ResolveAsync("s1", "evt1"));
        Assert.Equal("Carol", await store.ResolveAsync("s1", "evt2"));

        // Different session: no resolution.
        Assert.Null(await store.ResolveAsync("s2", "evt1"));

        var list = await store.ListForSessionAsync("s1");
        Assert.Equal(2, list.Count);
        Assert.Contains(list, x => x.EventId == "evt1" && x.DisplayName == "Bob");
        Assert.Contains(list, x => x.EventId == "evt2" && x.DisplayName == "Carol");

        Assert.Empty(await store.ListForSessionAsync("s2"));
    }

    [Fact]
    public async Task EventSpeakerOverrides_Upsert_UpdatesExistingEntry()
    {
        var store = new SqliteEventSpeakerOverrideStore(_db);

        await store.UpsertAsync("s1", "evt1", "Bob", null);
        await store.UpsertAsync("s1", "evt1", "Robert", null); // overwrite same key

        Assert.Equal("Robert", await store.ResolveAsync("s1", "evt1"));

        // Only one row for this (session, event) pair.
        var list = await store.ListForSessionAsync("s1");
        Assert.Single(list);
    }

    // ── SpeakerNameResolver — event override precedence ───────────────────────

    [Fact]
    public async Task SpeakerNameResolver_EventOverride_WinsOverAliasAndDirectId()
    {
        var speakers = new SqliteKnownSpeakerStore(_db);
        var aliases = new SqliteSpeakerAliasStore(_db);
        var overrides = new SqliteEventSpeakerOverrideStore(_db);

        var alice = await speakers.CreateAsync("Alice");
        await aliases.UpsertAsync("s1", "session_speaker_1", alice.Id);

        // Override says "Bob" for this specific event — should win over the alias-resolved "Alice".
        await overrides.UpsertAsync("s1", "evt1", "Bob", null);

        var resolver = new SqliteSpeakerNameResolver(overrides, aliases, speakers, TimeSpan.Zero);

        Assert.Equal("Bob", await resolver.ResolveDisplayNameAsync("s1", "session_speaker_1", "evt1"));
        // Without an eventId, falls back to the alias -> "Alice".
        Assert.Equal("Alice", await resolver.ResolveDisplayNameAsync("s1", "session_speaker_1"));
        // Different event: no override, falls back to alias.
        Assert.Equal("Alice", await resolver.ResolveDisplayNameAsync("s1", "session_speaker_1", "evt2"));
    }

    [Fact]
    public async Task SpeakerNameResolver_NoOverride_FallsBackToAliasChain()
    {
        var speakers = new SqliteKnownSpeakerStore(_db);
        var aliases = new SqliteSpeakerAliasStore(_db);
        var overrides = new SqliteEventSpeakerOverrideStore(_db);

        var joe = await speakers.CreateAsync("Joe");
        await aliases.UpsertAsync("s1", "session_speaker_1", joe.Id);

        var resolver = new SqliteSpeakerNameResolver(overrides, aliases, speakers, TimeSpan.Zero);

        Assert.Equal("Joe", await resolver.ResolveDisplayNameAsync("s1", "session_speaker_1", "evt-no-override"));
        Assert.Equal("Joe", await resolver.ResolveDisplayNameAsync("s1", joe.Id, "evt-no-override"));
        Assert.Null(await resolver.ResolveDisplayNameAsync("s1", "mic", "evt-anything"));
        Assert.Null(await resolver.ResolveDisplayNameAsync("s1", "speaker_unknown", "evt-anything"));
    }

    [Fact]
    public async Task SpeakerNameResolver_WithNullOverrideStore_WorksLikeBeforeForAllCallers()
    {
        var speakers = new SqliteKnownSpeakerStore(_db);
        var aliases = new SqliteSpeakerAliasStore(_db);

        var joe = await speakers.CreateAsync("Joe");
        await aliases.UpsertAsync("s1", "session_speaker_1", joe.Id);

        // Old two-arg ctor — no override store.
        var resolver = new SqliteSpeakerNameResolver(aliases, speakers, TimeSpan.Zero);

        Assert.Equal("Joe", await resolver.ResolveDisplayNameAsync("s1", "session_speaker_1"));
        Assert.Equal("Joe", await resolver.ResolveDisplayNameAsync("s1", "session_speaker_1", "evt1"));
    }
}
