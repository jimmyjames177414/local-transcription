---
title: Continue a Saved Session (Resume Recording from the Home Screen)
version: 1.1
date_created: 2026-07-16
last_updated: 2026-07-16
confidence_level: 9
---
# Implementation Plan: Continue a Saved Session

## Goal

**Story Goal**: From the Sessions screen, pick a saved (stopped) session and *continue* it —
new recording appends to the **same** session (same id, same `.txt`/`.jsonl` files, same DB row),
rather than starting a brand-new session.

**Deliverable**: A "Continue" action on the Sessions screen + review banner that loads the session
into the Meeting (home) screen in a continuable state; pressing **Start** resumes capture and appends
to the original session. Backed by a new engine "continue existing session" path.

**Success Definition**: Load a stopped session, press Start, speak; the new turns append below the
existing transcript, are written to the original `.txt`/`.jsonl` files, persist to the same session id
in SQLite, and the session still appears as one entry (not a duplicate) on the Sessions screen.

## Why

- Meetings get interrupted (breaks, dropped calls, "let's reconvene after lunch"). Today the only
  option is a second disconnected session; users want one continuous transcript.
- Complements the existing read-only **Load session** (review) flow — same entry point, new intent.
- Reuses the shared `Engine` and existing append-mode writers; low structural risk.

## What

- Sessions screen gains a **Continue** button next to **Load session** (enabled only when a
  non-recording session is selected).
- The Meeting/home screen shows the loaded transcript in a *continuable* (not read-only) state with a
  banner: "Continuing saved session — press Start to append." Pressing **Start** resumes into the same
  session.
- The review banner also gets a **Continue recording** button (secondary entry from review mode).
- New audio appends to the original files and session id; the session's `ended_at` is cleared while
  recording and re-stamped on Stop.

### Success Criteria

- [ ] Continuing a stopped session appends events to the original `.txt` and `.jsonl` files (no truncation).
- [ ] The continued turns persist under the original session id; SQLite shows one session, not two.
- [ ] While continuing, session status is `recording` and `ended_at` is null; Stop re-stamps `ended_at`.
- [ ] Globally-enrolled speakers are still recognised across the continuation.
- [ ] Starting a *fresh* recording is unchanged (still creates a new session).
- [ ] Transcription stays fully offline; agent remains opt-in/off by default.

## All Needed Context

### Documentation & References

```yaml
- file: src/LocalTranscriber.Engine/RealTranscriptionEngine.cs
  why: StartAsync (session create) and StopAsync (EndAsync) are the create/reopen seams
  pattern: session lifecycle + writer construction (both writers open FileMode.Append)
  gotcha: StartAsync calls _sessionStore.CreateAsync — a second call for an existing id throws (PK)

- file: src/LocalTranscriber.Engine/EngineFactory.cs
  why: CreateSessionOptions builds fresh paths/id; add a continuation variant reusing existing ones
  pattern: option mapping from AppConfig

- file: src/LocalTranscriber.App/ViewModels/MainWindowViewModel.cs
  why: LoadArchive (read-only review) + StartAsync (clears transcript, fresh options) are the models
  pattern: LoadArchive population, review-mode flags, StartAsync flow
  gotcha: StartAsync currently Transcript.Clear() + SpeakerPalette.Reset() + CloseReview()

- file: src/LocalTranscriber.App/ViewModels/SessionsViewModel.cs
  why: LoadCommand/LoadRequested is the exact pattern to mirror for Continue
  pattern: AsyncRelayCommand + event raised to the shell with (SessionRecord, events)

- file: src/LocalTranscriber.App/MainWindow.xaml.cs
  why: wires SessionsPanel.LoadRequested -> Session.LoadArchive; add the Continue wiring here

- file: src/LocalTranscriber.Storage/SqliteStores.cs
  why: SqliteSessionStore.EndAsync is the template for a new ReopenAsync (status + ended_at)

- file: src/LocalTranscriber.Storage/StoreInterfaces.cs
  why: ISessionStore — add ReopenAsync to the contract
```

### Verified Facts (de-risking checks already run against the code)

```text
# Event append is collision-free + correctly ordered:
#   - RealTranscriptionEngine builds each event with Id = Guid.NewGuid() and InsertAsync writes it
#     against session_id (SqliteStores InsertAsync). Re-using an existing session id only adds rows.
#   - ListBySessionAsync sorts "ORDER BY timestamp", so continued turns (later timestamps) render
#     after the original ones. No primary-key collision, no reordering.
# Both engines create the session identically: RealTranscriptionEngine.StartAsync and
#   FakeTranscriptionEngine.StartAsync each call _sessionStore.CreateAsync(new SessionRecord(...,
#   "recording")). The continue branch is the SAME one-line swap in both — trivial parity.
# Writers confirmed append-only: PlainTextTranscriptWriter + JsonlTranscriptWriter both open
#   FileStream with FileMode.Append. Reusing the session's paths appends with zero writer changes.
# The review banner (MeetingView.xaml, bound to Session.IsReviewing) is an independent 3-column
#   grid. The continue flow uses a SEPARATE banner (Session.IsContinuing) and never touches review
#   mode — no risk of regressing the existing archive-review UX.
# The App drives the engine in-process (EngineIpcServer is only an external control pipe), so the
#   new TranscriptionSessionOptions field needs no IPC/serialization work for the UI path.
```

### Known Gotchas & Library Quirks

```text
# Writers already append: PlainTextTranscriptWriter & JsonlTranscriptWriter open FileMode.Append,
#   so appending to an existing session's files "just works" once we reuse the paths.
# CreateAsync is a raw INSERT — re-inserting an existing id throws. The continue path must skip
#   CreateAsync and instead reopen (status='recording', ended_at=NULL).
# The WPF App calls the engine in-process (EngineIpcServer is only an external control pipe), so
#   adding a field to TranscriptionSessionOptions needs no IPC serialization changes for the UI path.
# Session-local unknown-speaker clustering (SessionSpeakerRegistry) restarts on continue: new unknown
#   speakers may reuse labels like "Speaker 1". Globally-enrolled speakers ARE still recognised. This
#   is an accepted limitation for v1 (document it; do not attempt to rehydrate the registry).
# The Elapsed clock reflects the new segment only, not cumulative meeting time. Accepted for v1.
# StartAsync currently clears the transcript; the continue path must NOT clear it (rows are pre-loaded).
```

## Implementation Blueprint

### Implementation Tasks (ordered by dependencies)

```yaml
Task 1: MODIFY src/LocalTranscriber.Engine/TranscriptionSessionOptions.cs
  - ADD: bool ContinueExisting { get; init; } = false;
  - PLACEMENT: near SessionId; XML-doc "Resume an existing session — append instead of creating."

Task 2: MODIFY src/LocalTranscriber.Storage/StoreInterfaces.cs + SqliteStores.cs
  - ADD to ISessionStore: Task ReopenAsync(string sessionId, CancellationToken ct = default);
  - IMPLEMENT in SqliteSessionStore: UPDATE sessions SET status='recording', ended_at=NULL WHERE id=$id
  - FOLLOW pattern: existing EndAsync (single UPDATE, parameterised)

Task 3: MODIFY src/LocalTranscriber.Engine/RealTranscriptionEngine.cs
  - IN StartAsync: when options.ContinueExisting -> await _sessionStore.ReopenAsync(id) INSTEAD of CreateAsync
  - PRESERVE: everything else (writers already append; _startedAt in-memory may be "now" — do not
    overwrite DB StartedAt)
  - GUARD: if ContinueExisting and _sessionStore is null, just proceed (fake/store-less path)

Task 4: MODIFY src/LocalTranscriber.Engine/FakeTranscriptionEngine.cs
  - MIRROR Task 3: honour ContinueExisting (ReopenAsync instead of CreateAsync) so tests + fake
    provider match real behaviour

Task 5: MODIFY src/LocalTranscriber.Engine/EngineFactory.cs
  - ADD: CreateContinuationOptions(AppConfig config, SessionRecord existing)
  - BODY: build like CreateSessionOptions but override SessionId=existing.Id,
    OutputTextPath=existing.OutputTextPath, OutputJsonlPath=existing.OutputJsonlPath,
    ContinueExisting=true. Do NOT create new file names.

Task 6: MODIFY src/LocalTranscriber.App/ViewModels/SessionsViewModel.cs
  - ADD: AsyncRelayCommand ContinueCommand; event Action<SessionRecord, IReadOnlyList<TranscriptEvent>>? ContinueRequested;
  - CanExecute: Selected is not null && !_isRecording() && Selected.Session.Status != "recording"
  - BODY: load events (same as LoadSelectedAsync) then ContinueRequested?.Invoke(session, events)
  - WIRE: RaiseCanExecuteChanged for ContinueCommand alongside LoadCommand in the Selected setter
    and OnRecordingStateChanged()

Task 7: MODIFY src/LocalTranscriber.App/ViewModels/MainWindowViewModel.cs
  - ADD state: string? _continueSessionId; string? _continueTextPath; string? _continueJsonlPath;
    bool IsContinuing { get; } (=> _continueSessionId is not null)
  - ADD: void LoadForContinuation(SessionRecord record, IReadOnlyList<TranscriptEvent> events)
      * guard: return if IsRecording
      * populate Transcript from events (reuse LoadArchive's row build), SpeakerPalette.Reset()
      * set SessionId=record.Id, SessionTitle=record.Title ?? "", CurrentJsonlPath=record.OutputJsonlPath
      * set _continueSessionId/_continueTextPath/_continueJsonlPath from record
      * DO NOT set IsReviewing; set a lightweight banner via a new ContinueBannerText property
      * SelectedScreenIndex = Meeting; seed _sessionSpeakerNames from loaded rows' names
  - MODIFY StartAsync: if _continueSessionId is not null ->
      * options = EngineFactory.CreateContinuationOptions(_config, <record-from-fields>)
        (store the SessionRecord itself in a field, or rebuild options from the 3 saved values)
      * SKIP Transcript.Clear()/SpeakerPalette.Reset() (keep loaded rows)
      * after successful start: clear _continueSessionId/paths + ContinueBannerText
    else: existing fresh-session behaviour unchanged
  - ENSURE CloseReview()/leaving continuation clears the continue fields + banner

Task 8: MODIFY src/LocalTranscriber.App/MainWindow.xaml.cs
  - WIRE: SessionsPanel.ContinueRequested += (record, events) => _ = OnContinueSessionRequestedAsync(record, events);
  - IMPLEMENT OnContinueSessionRequestedAsync: StopVoiceIfRunningAsync(); Session.LoadForContinuation(...);
    _notesService.StartSession(record.Id); Notes.Reload();  (mirror OnLoadSessionRequestedAsync)

Task 9: MODIFY src/LocalTranscriber.App/Views/SessionsView.xaml
  - ADD "Continue" button next to "Load session", bound to SessionsPanel.ContinueCommand,
    tooltip "Resume this session — new recording appends to it"

Task 10: MODIFY src/LocalTranscriber.App/Views/MeetingView.xaml
  - ADD a NEW banner (Grid.Row=0 sibling of the review banner) bound to Session.IsContinuing,
    showing ContinueBannerText plus a "● Continue recording" button -> Session.StartCommand and a
    "✕ Cancel" button that clears the continuation (reset continue fields, Transcript.Clear()).
  - DO NOT modify the existing review banner (bound to IsReviewing) — keep review mode untouched.

Task 11: CREATE/EXTEND tests
  - tests/LocalTranscriber.Storage.Tests: ReopenAsync sets status='recording' + ended_at NULL.
  - tests/LocalTranscriber.Engine.Tests: continue path appends events under same id to same files;
    StartAsync with ContinueExisting does NOT throw on an existing session; Stop re-stamps ended_at.
  - FOLLOW existing fixtures (temp DB + temp folder) in those test projects.
```

### Implementation Patterns & Key Details

```csharp
// Task 2 — SqliteSessionStore.ReopenAsync (mirror EndAsync)
public async Task ReopenAsync(string sessionId, CancellationToken cancellationToken = default)
{
    using var connection = _db.OpenConnection();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = "UPDATE sessions SET status = 'recording', ended_at = NULL WHERE id = $id";
    cmd.Parameters.AddWithValue("$id", sessionId);
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
}

// Task 3 — RealTranscriptionEngine.StartAsync (session row seam only)
if (_sessionStore is not null)
{
    if (options.ContinueExisting)
    {
        await _sessionStore.ReopenAsync(options.SessionId, cancellationToken).ConfigureAwait(false);
    }
    else
    {
        await _sessionStore.CreateAsync(new SessionRecord(
            options.SessionId, _startedAt.Value, null,
            options.OutputTextPath, options.OutputJsonlPath, "recording"), cancellationToken).ConfigureAwait(false);
    }
}

// Task 7 — StartAsync branch (do NOT clear transcript when continuing)
var options = _continueSessionId is not null
    ? EngineFactory.CreateContinuationOptions(_config, _continueRecord!)   // reuse id + paths
    : EngineFactory.CreateSessionOptions(_config, folder);
if (_continueSessionId is null) { Transcript.Clear(); SpeakerPalette.Reset(); }
// CRITICAL: fresh sessions keep today's behaviour; continue keeps loaded rows and appends.
```

## E2E Validation Plan

- [ ] Record a short session, Stop. Note the transcript file line count and session id.
- [ ] Sessions screen → select it → **Continue**. Meeting screen shows the loaded rows + continue banner.
- [ ] Press **Start**, speak two lines, **Stop**. New lines appear below the old ones (no gap/clear).
- [ ] Confirm `.txt`/`.jsonl` grew (old lines intact, new lines appended) — no truncation.
- [ ] Sessions screen still lists ONE entry for that session; opening it shows old + new turns.
- [ ] Verify a previously-enrolled speaker is recognised in the continued segment.
- [ ] Start a brand-new recording (no Continue) — confirms fresh-session path is unchanged.
- [ ] Continue button is disabled while a live recording is in progress.

### Anti-Patterns to Avoid

- ❌ Don't call CreateAsync for a continued session (PK violation) — reopen instead.
- ❌ Don't clear the transcript or reset the palette on the continue Start path.
- ❌ Don't generate new file names for a continuation — reuse the session's existing paths.
- ❌ Don't route transcription through the cloud or flip the agent on by default.
- ❌ Don't duplicate session-lifecycle logic in the App — go through the shared Engine + EngineFactory.
- ❌ Don't try to rehydrate SessionSpeakerRegistry state in v1 — document the limitation instead.

## Resolved Decisions (baked into the tasks above)

1. **Entry points**: Sessions-screen **Continue** button (primary) + a dedicated meeting-screen
   continuation banner. The read-only **Load session** review flow and its banner are left untouched.
   A review-banner "Continue recording" affordance is deferred (avoids converting review->continue and
   keeps the change surface small).
2. **Elapsed clock**: shows the new segment only (simplest). Cumulative time is deferred — it would
   need the prior duration folded in and does not affect transcript ordering or persistence.
3. **CLI/MCP parity**: the engine capability (`ContinueExisting`) lives in the shared engine, so
   parity is available; no CLI/MCP verb is added in this plan. A `continue <sessionId>` CLI verb can
   follow later without engine changes.
```
