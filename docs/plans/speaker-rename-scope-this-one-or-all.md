---
title: Speaker rename scope — "just this line" vs "all of the same name"
version: 1.0
date_created: 2026-07-16
last_updated: 2026-07-16
confidence_level: 8
---
# Implementation Plan: Ask "just this one or all?" when renaming an auto-applied speaker

## Goal

**Story Goal**: When the user clicks a speaker name in the live transcript to rename it, the app
**always** asks whether the correction should apply to **just this one line** or to **every line
with that name** — the scope choice is a permanent part of the rename dialog, shown every time (mic
rows excepted, since they aren't renameable). Picking "just this line" fixes a single misattribution
without renaming the person globally, re-enrolling their voice, or touching any other line.

**Deliverable**: A per-event speaker override (new SQLite table + store), an engine API to write
it, read-time resolution that honors it, a scope choice in the rename dialog, and the
`MainWindowViewModel` rename flow branching on that choice. CLI/MCP stay global-only (no event
context) and are documented as such.

**Success Definition**: The diarizer mislabels one turn as "Alice" (really it was Bob). The user
clicks that line, types "Bob", chooses **Just this line** → only that turn becomes "Bob"; the real
Alice's other lines, her enrollment, and future sessions are untouched. Reloading the session in
review still shows the corrected line.

## Why

- Today renaming a **known/auto-applied** speaker calls `RenameKnownSpeakerAsync`, which renames the
  enrolled speaker **globally** (all sessions, all lines) — catastrophic when the intent was to fix
  one bad voice-match. See [MainWindowViewModel.cs L124-171](../../src/LocalTranscriber.App/ViewModels/MainWindowViewModel.cs)
  and [RealTranscriptionEngine.RenameKnownSpeakerAsync](../../src/LocalTranscriber.Engine/RealTranscriptionEngine.cs).
- Voice matching is probabilistic; a single wrong segment is a normal occurrence, and the only
  current remedy over-applies the fix.
- The transcript is append-only by design; names already resolve at read time by `speakerId`
  ([SqliteSpeakerNameResolver.cs](../../src/LocalTranscriber.Storage/SqliteSpeakerNameResolver.cs)),
  so a per-event override slots into the same mechanism without rewriting any transcript files.

## What

When the user renames a **non-mic** speaker row to `NewName`, offer two scopes:

1. **All of the same name** (existing behavior, kept):
   - Known speaker → `RenameKnownSpeakerAsync(oldName, NewName)` (global enrollment rename).
   - Unknown session speaker → `NameSessionSpeakerAsync(label, NewName)` (enroll voice + alias).
   - Every in-memory row bearing that identity updates.
2. **Just this line** (new):
   - Write a per-event override `(session_id, event_id) → NewName`.
   - **No** enrollment, **no** global rename, **no** session alias.
   - Only the one in-memory row updates; read-time resolver honors the override on reload.

The choice is shown **every time** the rename dialog opens for any non-mic speaker (both voice-matched
knowns and diarizer "Speaker N" labels) — it is a fixed part of the dialog, not conditionally revealed.
Mic rows remain non-renameable and never open the dialog.

### Success Criteria

- [ ] Every rename dialog for a non-mic speaker shows the scope choice (Just this line / All … lines).
- [ ] "Just this line" corrects only the clicked row; other rows with the same name are untouched;
      the global enrollment name and future sessions are unchanged.
- [ ] "All of the same name" preserves today's behavior exactly (global rename / enroll + alias).
- [ ] A "just this line" correction survives session reload/review (resolver returns the override).
- [ ] "Just this line" to a brand-new name creates **no** empty `known_speakers` row (no trap).
- [ ] "Just this line" to an existing known name does **not** re-enroll that segment's voice.
- [ ] `.jsonl` and `transcript_events` rows are never rewritten (read-time resolution only).
- [ ] All existing tests green; new tests for the override store, resolver precedence, engine API.

## All Needed Context

### Documentation & References

```yaml
- file: src/LocalTranscriber.App/ViewModels/MainWindowViewModel.cs
  why: ExecuteRenameSpeakerAsync (L124-171) is the live rename entry point; branch on scope here
  pattern: ShowSpeakerRenameDialog Func; per-row mutation loop; _sessionSpeakerNames suggestions
  gotcha: currently renames ALL rows for knowns (by name) and unknowns (by SpeakerId) — no per-event path

- file: src/LocalTranscriber.App/ViewModels/TranscriptRowViewModel.cs
  why: rows carry SpeakerId but NOT the event id — add EventId (from e.Id) for the override call
  pattern: mutable SpeakerName/IsUnknownSpeaker/SpeakerBrush for live updates

- file: src/LocalTranscriber.App/Views/Dialogs/SpeakerRenameDialog.xaml(.cs)
  why: dialog returns only a name today; extend to return name + scope; show scope only when relevant
  pattern: chips for prior names; Save_Click sets NewName + DialogResult

- file: src/LocalTranscriber.App/MainWindow.xaml.cs
  why: L58 wires ShowSpeakerRenameDialog; update to the new request/result DTOs

- file: src/LocalTranscriber.Shared/ISpeakerNameResolver.cs
  why: add optional eventId param so override lookup happens before alias/known resolution
  gotcha: keep a default so existing callers compile unchanged

- file: src/LocalTranscriber.Storage/SqliteSpeakerNameResolver.cs
  why: check the override store first when eventId is supplied; extend the TTL cache key with eventId

- file: src/LocalTranscriber.Storage/SqliteDatabase.cs
  why: add event_speaker_overrides table (near speaker_aliases, L135)

- file: src/LocalTranscriber.Storage/StoreInterfaces.cs + SqliteStores.cs
  why: new IEventSpeakerOverrideStore + Sqlite impl, mirroring SqliteSpeakerAliasStore (L418)

- file: src/LocalTranscriber.Engine/RealTranscriptionEngine.cs + ITranscriptionEngine.cs + FakeTranscriptionEngine.cs
  why: add OverrideEventSpeakerAsync so App/CLI/MCP go through the shared engine; inject the store
  pattern: RenameKnownSpeakerAsync / NameSessionSpeakerAsync null-safe returns (bool)

- file: src/LocalTranscriber.Engine/EngineFactory.cs
  why: construct SqliteEventSpeakerOverrideStore and pass it into the engine (like aliasStore)

- file: src/LocalTranscriber.App/ViewModels/SessionsViewModel.cs
  why: review path (ResolveNamesAsync L415-427) must pass e.Id and construct the resolver with the
       override store so a "just this line" fix shows on reload
```

### Known Gotchas

```text
# CRITICAL: single shared Engine rule — the override write is an Engine API (OverrideEventSpeakerAsync),
#   consumed by the App now; CLI/MCP rename stay GLOBAL because they have no event id to target.
# CRITICAL: "just this line" to a NEW name must store a plain display-name string — do NOT create a
#   known_speakers row (that re-introduces the embedding-less-speaker trap).
# CRITICAL: "just this line" must NOT enroll the segment's voice — the segment is a suspected
#   misattribution; enrolling would poison the target's model.
# The resolver has a TTL cache — include eventId in the cache key so per-event overrides don't
#   collide with the (sessionId, speakerId) entries.
# Precedence at read time: event override > session alias > direct known-speaker id > baked name.
# Live events always carry e.Id (Guid); persisted events keep it as transcript_events.id PK.
# Mic rows: CanRename == false — no scope prompt, unchanged.
# The scope choice is ALWAYS shown for non-mic rows (per product decision). Even when only one row
#   bears the name, the scopes differ: "All" enrolls the voice / renames the enrollment globally,
#   "Just this line" does neither — so the choice is always meaningful.
# OccurrenceCount is used only to label the "All" option (e.g. Every "Alice" line (7)); it never
#   gates whether the choice appears.
```

## Implementation Blueprint

### Implementation Tasks (ordered by dependencies)

```yaml
Task 1: CREATE event_speaker_overrides storage
  - MODIFY src/LocalTranscriber.Storage/SqliteDatabase.cs: add table
      event_speaker_overrides (session_id TEXT NOT NULL, event_id TEXT NOT NULL,
        display_name TEXT NOT NULL, known_speaker_id TEXT, created_at TEXT NOT NULL,
        PRIMARY KEY (session_id, event_id))
  - MODIFY StoreInterfaces.cs: IEventSpeakerOverrideStore
      UpsertAsync(sessionId, eventId, displayName, knownSpeakerId?, ct)
      ResolveAsync(sessionId, eventId, ct) -> string? displayName
      ListForSessionAsync(sessionId, ct) -> IReadOnlyList<(string EventId, string DisplayName)>
  - MODIFY SqliteStores.cs: SqliteEventSpeakerOverrideStore (copy SqliteSpeakerAliasStore pattern,
      ON CONFLICT (session_id, event_id) DO UPDATE)

Task 2: EXTEND read-time resolver with an event override
  - MODIFY src/LocalTranscriber.Shared/ISpeakerNameResolver.cs:
      ResolveDisplayNameAsync(string sessionId, string speakerId, string? eventId = null, CancellationToken ct = default)
  - MODIFY src/LocalTranscriber.Storage/SqliteSpeakerNameResolver.cs:
      ctor takes an IEventSpeakerOverrideStore; when eventId != null, check ResolveAsync FIRST and
      return the override; cache key becomes $"{sessionId}|{speakerId}|{eventId}"
  - PRESERVE: alias/known resolution and mic/unknown null-outs unchanged when no override

Task 3: ENGINE API — OverrideEventSpeakerAsync (single shared engine)
  - MODIFY ITranscriptionEngine.cs: Task<bool> OverrideEventSpeakerAsync(string sessionId,
      string eventId, string newName, CancellationToken ct = default)
  - MODIFY RealTranscriptionEngine.cs: inject IEventSpeakerOverrideStore? _overrideStore; method
      returns false when store/sessionId null, else Upsert (resolve knownSpeakerId via
      _speakerStore.GetByNameAsync if the name matches an existing speaker — optional metadata) and return true
  - MODIFY FakeTranscriptionEngine.cs: return Task.FromResult(false)
  - MODIFY EngineFactory.cs: build SqliteEventSpeakerOverrideStore(db) and pass it in

Task 4: DIALOG — return name + scope (scope always visible)
  - CREATE (App layer) RenameScope enum { All, ThisOne } and small DTOs:
      SpeakerRenameRequest(string CurrentName, IReadOnlyList<string> Suggestions, int OccurrenceCount)
      and SpeakerRenameResult(string NewName, RenameScope Scope)
  - MODIFY SpeakerRenameDialog.xaml: add two scope radio options ALWAYS shown (fixed part of the
      dialog), labelled "Just this line" and "Every \"{name}\" line ({OccurrenceCount})"; default = All
  - MODIFY SpeakerRenameDialog.xaml.cs: expose Result (NewName + Scope); chips imply Scope = All
  - MODIFY MainWindow.xaml.cs L58: ShowSpeakerRenameDialog now returns SpeakerRenameResult?

Task 5: ROW carries the event id
  - MODIFY TranscriptRowViewModel.cs: add public string EventId { get; } = e.Id;

Task 6: RENAME FLOW branches on scope
  - MODIFY MainWindowViewModel.cs ExecuteRenameSpeakerAsync + ShowSpeakerRenameDialog signature:
      * compute occurrenceCount (wasUnknown ? rows with SpeakerId==id : rows with SpeakerName==oldName)
        purely to label the "All" option; the scope choice is shown regardless of the count
      * build SpeakerRenameRequest; bail on null/blank/unchanged
      * Scope == All  -> existing branch (NameSessionSpeakerAsync / RenameKnownSpeakerAsync) + mutate
                         all matching rows (current loop)
      * Scope == ThisOne -> await _engine.OverrideEventSpeakerAsync(_sessionId, row.EventId, trimmed);
                            on success mutate ONLY this row (SpeakerName/IsUnknownSpeaker=false/brush)
      * add trimmed to _sessionSpeakerNames in both branches
  - GOTCHA: _sessionId must be in scope here (already used for title updates)

Task 7: REVIEW path honors overrides
  - MODIFY SessionsViewModel.cs:
      * default resolver ctor now includes new SqliteEventSpeakerOverrideStore(db)
      * ResolveNamesAsync passes e.Id: ResolveDisplayNameAsync(sessionId, e.Speaker.SpeakerId, e.Id)

Task 8: TESTS
  - Storage.Tests: override store Upsert/Resolve/ListForSession; ON CONFLICT updates in place
  - Storage.Tests: resolver precedence — override wins over alias and over direct known id;
      returns alias/known result when no override; null for mic/unknown
  - Engine.Tests: OverrideEventSpeakerAsync writes the store (fake override store records call);
      returns false when store or sessionId is null; FakeTranscriptionEngine returns false
  - App (optional, if VM-testable): ThisOne mutates one row; All mutates all matching rows
```

### Implementation Patterns & Key Details

```csharp
// Task 2 — resolver: event override first, then existing chain
public async Task<string?> ResolveDisplayNameAsync(
    string sessionId, string speakerId, string? eventId = null, CancellationToken ct = default)
{
    if (string.IsNullOrEmpty(speakerId) || speakerId is "mic" or "speaker_unknown") return null;

    string cacheKey = $"{sessionId}|{speakerId}|{eventId}";
    // ... TTL cache check on cacheKey ...

    if (eventId is not null)
    {
        var overridden = await _overrides.ResolveAsync(sessionId, eventId, ct).ConfigureAwait(false);
        if (overridden is not null) { Cache(cacheKey, overridden); return overridden; }
    }
    // ... existing alias -> known_speaker resolution unchanged ...
}

// Task 3 — engine write (per-event correction; no enrollment, no global rename)
public async Task<bool> OverrideEventSpeakerAsync(
    string sessionId, string eventId, string newName, CancellationToken ct = default)
{
    if (_overrideStore is null || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(eventId))
        return false;
    var known = _speakerStore is null ? null : await _speakerStore.GetByNameAsync(newName, ct).ConfigureAwait(false);
    await _overrideStore.UpsertAsync(sessionId, eventId, newName.Trim(), known?.Id, ct).ConfigureAwait(false);
    return true; // read-time resolver applies it wherever this event is displayed
}

// Task 6 — App branch (only the "just this line" arm shown; All arm = today's loop)
if (result.Scope == RenameScope.ThisOne)
{
    if (!await _engine.OverrideEventSpeakerAsync(_sessionId, row.EventId, trimmed)) return;
    row.SpeakerName = trimmed;
    row.IsUnknownSpeaker = false;
    row.SpeakerBrush = Services.SpeakerPalette.GetBrushForName(trimmed);
}
```

## E2E Validation Plan
- [ ] Live session, diarizer labels one turn "Alice" that is really Bob: click it, type "Bob",
      choose **Just this line** → only that row shows Bob; Alice's other rows and enrollment unchanged.
- [ ] Choose **All** on a genuinely mislabeled identity → global rename works exactly as before
      (all rows + future sessions).
- [ ] Reload the session in Sessions/review after a "just this line" fix → the corrected line still
      shows the override name; all other lines resolve normally.
- [ ] "Just this line" to a brand-new name → `known_speakers` gains no empty row; no future-session
      recognition of that segment.
- [ ] Rename to an existing known name with **Just this line** → that speaker's voice model is not
      changed (no new embedding rows).
- [ ] Old `.jsonl` / `transcript_events` unchanged on disk.
- [ ] CLI/MCP `rename_speaker` still performs the global rename (documented as "all").
- [ ] Fake engine: OverrideEventSpeakerAsync returns false, nothing throws.

### Anti-Patterns to Avoid

- ❌ Don't create a `known_speakers` row for a "just this line" new name (empty-speaker trap).
- ❌ Don't enroll the segment's voice on "just this line" — it's a suspected misattribution.
- ❌ Don't rewrite `.jsonl` or `transcript_events` rows — resolve at read time only.
- ❌ Don't write the override from the App directly against SQLite — go through the shared Engine API.
- ❌ Don't drop the eventId from the resolver cache key — per-event and per-speaker entries must not collide.
- ❌ Don't remove or alter the existing "All" behavior — it stays the default.
- ❌ Don't fabricate an event id for live rows — use e.Id already present on every TranscriptEvent.

## Confidence Score

**8/10** — The read-time resolution architecture, alias table, and rename flow already exist and are
directly extensible; the main risks are (a) threading `eventId` through the resolver without breaking
the agent/review callers and (b) the WPF dialog scope UI. Both are contained and covered by the tasks.

## Notes / Out of Scope

- **CLI/MCP per-event override**: could add `override_event_speaker(session_id, event_id, name)` later;
  omitted now because those surfaces operate on global names and lack a natural event target.
- **Agent retroactive resolution**: `MeetingAgent` does not currently call the resolver, so overrides
  won't reach agent prompts until that wiring exists (tracked separately in speaker-identity-two-cuts).
- **Merge semantics** when renaming to an existing name under "All" (two `known_speakers` sharing a
  name) is a pre-existing behavior and is not changed here.
