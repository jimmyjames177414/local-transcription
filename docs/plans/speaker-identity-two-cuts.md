---
title: Speaker identity — live naming with read-time resolution (two cuts)
version: 2.0
date_created: 2026-07-09
last_updated: 2026-07-09
confidence_level: 8
---
# Implementation Plan: Speaker identity overhaul — Cut 1 (live naming + read-time names), Cut 2 (provisional speakers, decision-gated)

> Supersedes v1.0 of `live-session-speaker-naming.md`. Root-cause analysis: speaker identity is
> resolved once at write time and denormalized as a display-name string into the .jsonl, the
> SQLite `transcript_events.speaker_name` column, and agent prompts. Renames propagate nowhere.
> `speakerId` IS stored everywhere — the fix is to use it at read time.

## Goal

**Story Goal**: During a session, unnamed voices ("Speaker 1"…) appear in the Speakers tab; the
user names them there; the name is enrolled from embeddings already captured this session, and the
new name applies **retroactively and immediately** everywhere that matters — the UI transcript,
the agent's rolling window, and all future sessions.

**Deliverable (Cut 1)**: Engine session-speaker API + enrollment-on-naming; a speaker-alias table
mapping session speaker IDs to known speakers; read-time name resolution in the transcript tailer,
agent prompts, and UI; Speakers tab session list; CLI/MCP parity.

**Success Definition**: Name "Speaker 1" as "Alice" at minute 20 of a live meeting → the agent's
next analysis window says "Alice" for lines from minute 0 onward, the UI shows "Alice", and the
next session recognizes Alice's voice with no extra steps.

## Why

- The agent literally cannot know who is talking today unless the user pre-enrolls voices from WAV
  samples. Session embeddings that would serve perfectly are discarded on stop.
- Rename is name-string-keyed and creates embedding-less speakers (silent trap, can never match).
- Renames don't reach already-written lines: the tailer reads the baked `speakerName` string
  ([TranscriptEventTailer.cs L221](../../src/LocalTranscriber.Agent/TranscriptEventTailer.cs)).

## What

### Cut 1 — in scope now
1. **Session speaker exposure + naming** — registry snapshot through the shared Engine; naming
   enrolls all captured embeddings (explicit user action = consent moment, per PRIVACY.md ethos).
2. **Speaker aliases** — naming records `(session_id, session_speaker_id) → known_speaker_id` so
   lines already written with `session_speaker_1` can be resolved later.
3. **Read-time name resolution** — tailer/agent and UI resolve display names via
   `speakerId` (+ alias table) instead of trusting the baked string. The `.jsonl` stays append-only;
   it becomes an archival artifact, not the source of truth for names.

### Cut 2 — decision-gated, NOT in this implementation
Provisional speaker persistence (every detected voice gets a SQLite row + embeddings immediately,
auto-expiring after N days unless named; config-gated, off by default). **Requires an explicit
privacy-stance decision**: it means storing voice biometrics of people who never consented.
Documented here so Cut 1's schema (alias table, ID-keyed reads) is forward-compatible with it.

### Success Criteria (Cut 1)

- [ ] Speakers tab shows a "This session" list of unnamed session speakers while a session runs.
- [ ] Naming persists a KnownSpeaker + session embeddings + an alias row.
- [ ] Agent prompts use the new name for **past and future** lines of the current session
      (verify via a suggestion referencing minute-0 content by name).
- [ ] UI transcript shows the new name after rename (at minimum on next lines; re-render of past
      lines if the Session tab keeps events in memory).
- [ ] Next session voice-matches the named speaker automatically.
- [ ] Renaming an enrolled speaker (e.g. Alice → Alice B.) propagates to agent/UI reads without
      touching transcript files.
- [ ] `.jsonl` files remain append-only and self-contained (names-as-written preserved).
- [ ] All existing tests green; new tests for registry snapshot, naming/enrollment, alias
      resolution, tailer resolution.

## All Needed Context

### Documentation & References

```yaml
- file: src/LocalTranscriber.Engine/SessionSpeakerRegistry.cs
  why: In-memory Label -> List<float[]> embeddings (cap 10/speaker) — the enrollment data source
  pattern: lock-protected; ResolveLabel assigns "Speaker N" labels; must also retain SpeakerEmbedding metadata
  gotcha: engine emits speakerId "session_speaker_1" style ids derived from the label (RealTranscriptionEngine L365)

- file: src/LocalTranscriber.Engine/RealTranscriptionEngine.cs
  why: _registry per session (L106); ResolveSpeakerAsync checks _recognition.MatchAsync FIRST (L338+),
       so post-enrollment chunks adopt the new name automatically — no loop changes
  pattern: expose ListSessionSpeakers/NameSessionSpeaker on the engine; ITranscriptionEngine is the shared surface

- file: src/LocalTranscriber.Speakers/SpeakerRecognitionService.cs
  why: EnrollAsync(name, embedding, sessionId) — reuse per embedding
  pattern: GetByNameAsync ?? CreateAsync; AddAsync embedding; MarkSeenAsync

- file: src/LocalTranscriber.Agent/TranscriptEventTailer.cs
  why: L196-236 parses jsonl and bakes speakerName into SpeakerLabel — inject a resolver here
  pattern: keep parsing as fallback; resolver overrides DisplayName when it knows the id
  gotcha: tailer can run cross-process from the writer; resolver must read SQLite fresh (or with short TTL cache)

- file: src/LocalTranscriber.Storage/SqliteDatabase.cs
  why: schema creation — add speaker_aliases table here (L90 area, transcript_events pattern)

- file: src/LocalTranscriber.Storage/SqliteStores.cs
  why: SqliteKnownSpeakerStore pattern for the new alias store; RenameAsync trap (L181-189) to fix
  gotcha: RenameAsync currently creates an embedding-less speaker when 'from' is unknown — remove
          that branch in favor of the alias/naming flow (UI warns instead)

- file: src/LocalTranscriber.App/ViewModels/SpeakerManagementViewModel.cs
  why: Speakers tab VM — add "This session" section; needs the shared engine instance injected
  pattern: ObservableCollection + AsyncRelayCommand + StatusText

- docfile: docs/PRIVACY.md
  section: guarantees table — Cut 1 must not violate "speaker memory is user-controlled";
           naming IS the explicit consent action. Cut 2 would change this table (hence gated).
```

### Known Gotchas

```text
# CRITICAL: single shared Engine rule — session listing/naming is an Engine API consumed by App/CLI/MCP.
# CRITICAL: registry must store SpeakerEmbedding (with Dimensions/ModelName), not raw float[] —
#   StoredEmbedding needs both, via CosineSimilarity.ToBlob.
# The alias table is required because enrolled KnownSpeaker gets a NEW guid id; already-written lines
#   carry "session_speaker_N" ids. Without the alias, retroactive resolution is impossible.
# Tailer runs where the agent runs; MeetingAgent already re-reads its window every interval, so
#   resolving names when BUILDING the request (window snapshot -> provider) is the simplest hook —
#   no need to mutate tailed events in place.
# Mic channel: speakerId "mic", labeled from config — exclude from the session list.
# Fake engine / missing speaker models: no registry embeddings — session list empty, naming fails
#   gracefully with a status message, never throws.
# Agent project must not gain a hard Storage dependency in provider code paths — inject the resolver
#   as an interface (Func/interface defined in Agent or Shared, implemented in Storage).
```

## Implementation Blueprint

### Implementation Tasks (ordered by dependencies) — Cut 1

```yaml
Task 1: MODIFY src/LocalTranscriber.Engine/SessionSpeakerRegistry.cs
  - IMPLEMENT: store SpeakerEmbedding per label; add Snapshot() ->
    IReadOnlyList<SessionSpeakerInfo>(Label, SessionSpeakerId, IReadOnlyList<SpeakerEmbedding>)
  - SessionSpeakerId must match what the engine emits ("session_speaker_1" derivation) — factor the
    label->id derivation into one shared helper used by both registry and engine
  - PRESERVE: thresholds, cap-10 behavior, FallbackLabel

Task 2: CREATE speaker_aliases storage
  - MODIFY src/LocalTranscriber.Storage/SqliteDatabase.cs: table speaker_aliases
    (session_id TEXT, session_speaker_id TEXT, known_speaker_id TEXT, created_at TEXT,
     PRIMARY KEY (session_id, session_speaker_id))
  - CREATE alias store (ISpeakerAliasStore in StoreInterfaces.cs + Sqlite impl in SqliteStores.cs):
    UpsertAsync, ResolveAsync(sessionId, sessionSpeakerId) -> knownSpeakerId?,
    ListForSessionAsync(sessionId)
  - FOLLOW pattern: SqliteKnownSpeakerStore (connection/cmd usage)

Task 3: MODIFY ITranscriptionEngine + RealTranscriptionEngine (+ FakeTranscriptionEngine stub)
  - IMPLEMENT: ListSessionSpeakersAsync() -> label + sample count (no embeddings over the API);
    NameSessionSpeakerAsync(sessionLabel, newName): enroll all registry embeddings via
    _recognition.EnrollAsync, then alias-upsert (sessionId, sessionSpeakerId -> knownSpeaker.Id)
  - GOTCHA: null recognition/registry -> return false; concurrent naming of same label -> idempotent
    (GetByNameAsync ?? CreateAsync already is)
  - PRESERVE: no changes to the transcription loop; MatchAsync picks the name up on later chunks

Task 4: READ-TIME name resolution
  - CREATE ISpeakerNameResolver (Shared): string? ResolveDisplayName(string sessionId, string speakerId)
  - CREATE Storage impl: alias lookup -> known_speakers.display_name; direct known_speaker id
    lookup for already-known ids; small TTL cache (e.g. 5s) to avoid per-line queries
  - MODIFY src/LocalTranscriber.Agent/MeetingAgent.cs: when building AgentProviderRequest, map the
    window events through the resolver (replace Speaker.DisplayName when resolver returns a name);
    same in AgentOneShot
  - MODIFY App Session tab display path: resolve names when rendering transcript lines; refresh
    displayed names after a rename/naming action
  - DO NOT: rewrite .jsonl or transcript_events rows

Task 5: MODIFY src/LocalTranscriber.Storage/SqliteStores.cs RenameAsync
  - REMOVE the create-embedding-less-speaker branch (the trap); return false when 'from' unknown
  - UI/CLI callers show guidance: "Unknown speaker — use the session list to name a live speaker,
    or speakers enroll for a WAV sample."

Task 6: MODIFY src/LocalTranscriber.App (Speakers tab)
  - IMPLEMENT: "This session" ObservableCollection refreshed on transcript-event arrival or 5s timer
    while a session runs; per-item TextBox + Name button -> engine.NameSessionSpeakerAsync;
    StatusText feedback; list hides when no session
  - DEPENDENCY: inject the shared engine instance (same object the Session tab drives)

Task 7: CLI + MCP parity
  - CLI (src/LocalTranscriber.Cli SpeakerCommands): `speakers session-list`, `speakers name --from
    "Speaker 1" --to "Alice"` — in-process sessions only (registry lives in the engine process);
    cross-process invocation prints guidance
  - MCP (LocalTranscriberTools): session_list_speakers, name_session_speaker (works when MCP hosts
    the engine session); log via _logger like rename_speaker

Task 8: TESTS
  - Engine.Tests: registry Snapshot metadata; NameSessionSpeakerAsync enrolls all embeddings +
    writes alias (fake recognition + fake alias store record calls); null-safety paths
  - Storage.Tests: alias store CRUD; RenameAsync unknown-name now returns false
  - Agent.Tests: MeetingAgent window resolution — resolver renames retroactively in provider request;
    tailer/one-shot path unchanged when resolver returns null
  - Speakers.Tests: multi-embedding enrollment then MatchAsync confident match
```

### Implementation Patterns & Key Details

```csharp
// Task 3 — naming = enrollment + alias (engine)
public async Task<bool> NameSessionSpeakerAsync(string sessionLabel, string newName, CancellationToken ct = default)
{
    var registry = _registry; var options = _options;
    if (registry is null || _recognition is null || options is null) return false;

    var info = registry.Snapshot().FirstOrDefault(s => s.Label == sessionLabel);
    if (info is null || info.Embeddings.Count == 0) return false;

    foreach (var embedding in info.Embeddings)
        await _recognition.EnrollAsync(newName, embedding, options.SessionId, ct);

    var speaker = await _speakerStore.GetByNameAsync(newName, ct);          // just created/updated
    if (speaker is not null && _aliasStore is not null)
        await _aliasStore.UpsertAsync(options.SessionId, info.SessionSpeakerId, speaker.Id, ct);
    return true; // later chunks: MatchAsync resolves to newName from SQLite automatically
}

// Task 4 — retroactive names in agent prompts (MeetingAgent.RunAnalysisAsync)
var window = (_window?.Snapshot() ?? Array.Empty<TranscriptEvent>())
    .Select(e => _nameResolver?.ResolveDisplayName(e.SessionId, e.Speaker.Id) is string name
        ? e with { Speaker = e.Speaker with { DisplayName = name, IsKnown = true } }
        : e)
    .ToArray();
// GOTCHA: resolver is optional (null in tests/offline minimal setups) — behavior identical when null.
```

## E2E Validation Plan
- [ ] Live session, system audio with one talker: "Speaker 1" appears in Speakers tab; name to "Alice";
      within one suggestion interval, ask the agent "who has been talking?" → answer says Alice
      (including content from before the naming).
- [ ] UI transcript lines show Alice going forward (and retroactively if re-rendered).
- [ ] Stop; start new session with same voice: lines labeled Alice from the first chunk.
- [ ] Rename Alice → "Alice B." in the Speakers tab: agent's next window uses Alice B. without restart.
- [ ] Old .jsonl files unchanged on disk (append-only preserved).
- [ ] rename-speaker with unknown 'from' name: friendly error, no empty speaker row created.
- [ ] Fake engine: session list empty, naming returns friendly status, nothing throws.

### Anti-Patterns to Avoid

- ❌ Don't rewrite .jsonl or transcript_events rows — read-time resolution only.
- ❌ Don't auto-enroll voices without the explicit naming action (Cut 2 territory; privacy decision pending).
- ❌ Don't give the Agent project a hard Storage reference in provider paths — resolver behind an interface.
- ❌ Don't implement session-speaker access App-side against the registry — shared Engine API only.
- ❌ Don't remove the offline `speakers enroll` WAV flow.
- ❌ Don't per-line-query SQLite in the tailer/agent hot path — TTL cache in the resolver.

---

## Cut 2 outline (future, decision required first)

**Decision needed**: is persisting embeddings of *unnamed* voices acceptable? (Biometrics of
non-consenting speakers; PRIVACY.md guarantees table would need a new row.)

If yes: provisional `known_speakers` rows (flag `provisional=1`) created on first detection;
embeddings persisted immediately; auto-purge after `speakerMemory.provisionalRetentionDays`
(default e.g. 14) unless named; config gate `speakerMemory.rememberUnnamedVoices` default false;
Speakers tab shows provisional speakers greyed with expiry date; cross-session "Speaker 1 is the
same person as last Tuesday" continuity. Cut 1's alias table and ID-keyed reads already support this.
