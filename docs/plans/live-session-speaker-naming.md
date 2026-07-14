---
title: Live session speaker naming and in-session enrollment
version: 1.0
date_created: 2026-07-09
last_updated: 2026-07-09
confidence_level: 8
superseded_by: speaker-identity-two-cuts.md
---
> **SUPERSEDED** by [speaker-identity-two-cuts.md](speaker-identity-two-cuts.md) — root-cause
> analysis showed write-time name denormalization is the deeper issue; the v2 plan adds
> read-time name resolution and retroactive rename propagation.

# Implementation Plan: Name session speakers live, enroll from session audio

## Goal

**Story Goal**: During a running session, unnamed voices ("Speaker 1", "Speaker 2") appear in the
Speakers tab as they are detected; the user names them there; the name is enrolled using voice
embeddings already captured during the session, and the rest of the session (and all future
sessions) labels that voice with the real name.

**Deliverable**: Engine API exposing session speakers + their embeddings; a "name session speaker"
enrollment path; Speakers tab section listing live session speakers with a name box; CLI/MCP parity.

**Success Definition**: Start a session, someone talks on system audio → "Speaker 1" appears in
the Speakers tab → type "Alice" → within a few chunks new transcript lines say "Alice", and the
next session recognizes Alice immediately.

## Why

- Today the only enrollment path is an offline WAV sample (`speakers enroll`) — high friction, and
  session voice data that would serve perfectly is silently discarded at session end.
- The transcript (and therefore the meeting agent) never learns who is talking unless the user
  does the offline enrollment dance.
- Bug: renaming an unknown speaker today creates a `KnownSpeaker` with **zero embeddings**
  ([SqliteStores.cs RenameAsync](../../src/LocalTranscriber.Storage/SqliteStores.cs)) — looks
  enrolled, never matches. This plan removes that trap.

## What

- `SessionSpeakerRegistry` already stores up to 10 embeddings per unknown session speaker
  (in-memory, discarded on stop). Expose them and persist on naming.
- Because `ResolveSpeakerAsync` calls `_recognition.MatchAsync` (SQLite-backed) on every chunk,
  persisting embeddings mid-session makes the engine pick up the new name automatically for
  subsequent chunks — no engine loop changes needed.
- Past `.jsonl` lines keep the old label (append-only transcript history is by design; out of scope).

### Success Criteria

- [ ] Speakers tab shows a "This session" list of unnamed session speakers while a session runs.
- [ ] Naming a session speaker persists a KnownSpeaker + its session embeddings in SQLite.
- [ ] Subsequent transcript lines in the same session use the new name (verify in .jsonl).
- [ ] Next session recognizes the voice without any enrollment step.
- [ ] `RenameAsync` no longer silently creates embedding-less speakers (either wired to session
      embeddings or the trap path documented/limited).
- [ ] CLI and MCP can list and name session speakers (single Engine rule — all front-ends share).

## All Needed Context

### Documentation & References

```yaml
- file: src/LocalTranscriber.Engine/SessionSpeakerRegistry.cs
  why: Holds Label -> List<float[]> embeddings per session speaker; the data source for enrollment
  pattern: lock-protected list; ResolveLabel adds embeddings (cap 10)
  gotcha: embeddings are raw float[]; StoredEmbedding needs blob via CosineSimilarity.ToBlob + Dimensions + ModelName

- file: src/LocalTranscriber.Engine/RealTranscriptionEngine.cs
  why: _registry created per session (line ~106); ResolveSpeakerAsync (~line 338) checks _recognition.MatchAsync FIRST,
       so once embeddings hit SQLite the name takes over automatically
  pattern: session lifecycle; expose registry snapshot via new engine method
  gotcha: registry is per-session and null when no session; engine is behind ITranscriptionEngine used by App/CLI/MCP

- file: src/LocalTranscriber.Speakers/SpeakerRecognitionService.cs
  why: EnrollAsync(name, embedding, sessionId) — reuse for persisting each registry embedding
  pattern: GetByNameAsync ?? CreateAsync, then AddAsync embedding + MarkSeenAsync

- file: src/LocalTranscriber.App/ViewModels/SpeakerManagementViewModel.cs
  why: existing Speakers tab VM — add session-speakers section here
  pattern: ObservableCollection + AsyncRelayCommand + StatusText

- file: src/LocalTranscriber.Storage/SqliteStores.cs
  why: RenameAsync unknown-name path creates empty KnownSpeaker (the trap) — revisit
```

### Known Gotchas

```text
# CRITICAL: registry embeddings lack ModelName/Dimensions today (List<float[]>) — registry must also
#   record the SpeakerEmbedding metadata (or store SpeakerEmbedding instead of float[]).
# CRITICAL: single shared Engine rule — session-speaker listing/naming must be an Engine API, then
#   surfaced by App, CLI, MCP; do not implement in the App against the registry directly.
# The engine's mic channel is labeled from config (defaultMicSpeakerName), not the registry — only
#   systemAudio speakers appear in the session list.
# Naming mid-session does NOT rewrite already-written .jsonl lines (append-only by design).
# UI VM currently constructs its own SqliteKnownSpeakerStore; the session list needs access to the
#   running engine instance (App already holds it for the Session tab).
```

## Implementation Blueprint

### Implementation Tasks (ordered by dependencies)

```yaml
Task 1: MODIFY src/LocalTranscriber.Engine/SessionSpeakerRegistry.cs
  - IMPLEMENT: store SpeakerEmbedding (not raw float[]) per label; add
    IReadOnlyList<SessionSpeakerInfo> Snapshot() returning label + embedding count + embeddings
  - NAMING: SessionSpeakerInfo(string Label, IReadOnlyList<SpeakerEmbedding> Embeddings)
  - PRESERVE: ResolveLabel/FallbackLabel behavior and thresholds unchanged

Task 2: MODIFY src/LocalTranscriber.Engine/ITranscriptionEngine.cs + RealTranscriptionEngine.cs (+FakeTranscriptionEngine)
  - IMPLEMENT: Task<IReadOnlyList<SessionSpeakerInfo>> ListSessionSpeakersAsync(); and
    Task<bool> NameSessionSpeakerAsync(string sessionLabel, string newName) which enrolls every
    registry embedding for that label via ISpeakerRecognitionService.EnrollAsync (sessionId set)
  - GOTCHA: recognition service may be null (fake engine / no models) — return false with no throw
  - PRESERVE: after enrollment no registry mutation needed; MatchAsync takes over on later chunks

Task 3: MODIFY src/LocalTranscriber.App/ViewModels/SpeakerManagementViewModel.cs + Speakers tab XAML
  - IMPLEMENT: "This session" ObservableCollection<SessionSpeakerItem> refreshed on a timer (or on
    transcript event) while a session runs; per-item name TextBox + "Name" button calling the engine
  - FOLLOW pattern: existing RenameCommand/StatusText wiring
  - DEPENDENCY: VM needs the shared engine instance (same one the Session tab uses) — inject it

Task 4: MODIFY src/LocalTranscriber.Cli (speakers command) + src/LocalTranscriber.Mcp
  - IMPLEMENT: `speakers session-list` and `speakers name --from "Speaker 1" --to "Alice"` against the
    running session (CLI start process / cross-process caveat: expose only where an engine is in-process,
    else print guidance); MCP tools session_list_speakers / name_session_speaker
  - PRESERVE: existing rename-speaker/enroll commands unchanged

Task 5: MODIFY src/LocalTranscriber.Storage/SqliteStores.cs (RenameAsync)
  - IMPLEMENT: keep create-on-rename behavior but log/annotate; OR (preferred) leave storage as-is and
    make UI rename path warn when target has zero embeddings ("name saved; voice not enrolled — use
    the session list or speakers enroll")
  - SCOPE: minimal — do not change storage schema

Task 6: CREATE tests
  - tests/LocalTranscriber.Engine.Tests: registry Snapshot; NameSessionSpeakerAsync enrolls all
    embeddings (fake recognition service records calls); null-recognition returns false
  - tests/LocalTranscriber.Speakers.Tests: enrollment from multiple embeddings then MatchAsync succeeds
  - NAMING: {Method}_{Scenario}_{Expectation}
```

### Implementation Patterns & Key Details

```csharp
// Task 2 — enrollment from session embeddings (engine)
public async Task<bool> NameSessionSpeakerAsync(string sessionLabel, string newName, CancellationToken ct = default)
{
    var registry = _registry;                    // per-session; null when stopped
    if (registry is null || _recognition is null) return false;

    var info = registry.Snapshot().FirstOrDefault(s => s.Label == sessionLabel);
    if (info is null || info.Embeddings.Count == 0) return false;

    foreach (var embedding in info.Embeddings)   // persist ALL samples for robust matching
        await _recognition.EnrollAsync(newName, embedding, _options?.SessionId, ct);

    return true;                                 // next chunks: MatchAsync picks up the name from SQLite
}
```

## E2E Validation Plan
- [ ] Live session with system audio (e.g. a YouTube video of one talker): Speaker 1 appears in the
      Speakers tab session list; name it; confirm later .jsonl lines carry the new name.
- [ ] Stop, start a new session with the same audio source: voice recognized as the name immediately.
- [ ] Fake engine / missing speaker models: session list empty, naming returns a friendly status, no crash.
- [ ] Agent tab: after naming, suggestions reference the real speaker name (agent reads the .jsonl).
- [ ] Existing offline `speakers enroll` flow still works.

### Anti-Patterns to Avoid

- ❌ Don't implement session-speaker access in the App against the registry directly — go through the shared Engine API (UI/CLI/MCP rule).
- ❌ Don't rewrite historical .jsonl lines — append-only by design.
- ❌ Don't drop the offline `speakers enroll` path — it's still the clean-sample option.
- ❌ Don't enroll on every chunk automatically — naming is an explicit user action (privacy: speaker memory is user-controlled).
- ❌ Don't store raw audio for enrollment — embeddings only, consistent with privacy rules.
