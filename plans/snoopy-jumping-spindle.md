# Fix: meetings look "truncated after a rename" — really an un-finalized session row

## Context

**Reported symptom:** "The transcription doesn't save after changing a name. After a meeting
the recording only had the first portion when I changed the first name of Speaker 1 — it only
showed Speaker 1's changed name and Speaker 2."

**What actually happened (verified against the user's own data — `output/`):**

- The meeting `session-20260716-073722` is **fully saved and intact**:
  - `session-20260716-073722.jsonl` / `.txt`: **340 events**, 07:37:22 → 08:01:35 (24 min), all
    speakers (Joe, Lokesh, Rajeev, Speaker 1–4).
  - SQLite `transcript_events`: **also 340 rows** — identical. **No data was lost.**
- The `sessions` row is stuck at **`status='recording'`, `ended_at=NULL`**. Today's log
  (`output/logs/localtranscriber-20260716.log`) has **no "Session … stopped" line** — so
  `RealTranscriptionEngine.StopAsync()` never reached its finalize step
  (`_sessionStore.EndAsync`, `RealTranscriptionEngine.cs:441-445`). The app was terminated before
  graceful shutdown completed (force-close / crash / OS shutdown / interrupted 120s drain in
  `StopAsync`).
- The renames themselves all **succeeded** (log: "named as 'Lokesh'/'Rajeev'/'Joe' … alias written").
  The rename is a red herring — it just happened during this meeting.

**Why it *looks* like "only the first portion was saved":** the stuck `status='recording'` /
null `ended_at` corrupts every summary signal in the Sessions list:

1. **Duration shows "1m"** for a 24-minute meeting. `SessionListItemViewModel.FormatDuration`
   (`SessionsViewModel.cs:58-67`) computes `end = EndedAt ?? StartedAt` → span 0 → `Max(1, …)` = "1m".
2. **Detail preview shows only the first 30 rows** by design —
   `events.Take(PreviewRowCount)` (`SessionsViewModel.cs:353`, `PreviewRowCount = 30`).
3. **Minutes never exported / badge hidden** — `s.Status != "recording"` gate
   (`SessionsViewModel.cs:38`); `TryExportMinutesAsync` runs only inside the stop path that never ran.

Root cause: **a session that isn't shut down gracefully is left permanently in `recording`
status with no `ended_at`, and there is no recovery path.** Sessions are created with
`status="recording"` (`RealTranscriptionEngine.cs:152-154`) and only flipped to `stopped`/`faulted`
at the very end of `StopAsync`. Nothing reconciles a session the app abandoned.

**Intended outcome:** an interrupted meeting is auto-recovered on next launch and displays with the
correct duration and full transcript, so it never looks truncated. No transcription logic changes;
capture and file writes are already correct.

## Approach

### 1. Startup reconciliation of orphaned "recording" sessions (primary fix — self-healing)

On app start, finalize any session still in `status='recording'` that the engine is not actually
running (i.e., all of them at startup — the process just launched). For each:
- Backfill `ended_at` = MAX(`timestamp`) of that session's `transcript_events` (fallback to
  `started_at` when the session has no events).
- Set `status = 'interrupted'` (**decided**).

This immediately repairs the user's existing stuck session on next launch and covers every future
crash/force-close.

**`'interrupted'` UI handling:** confirm nothing special-cases only `"stopped"`. The two current
status checks both behave correctly for `"interrupted"`: the minutes-badge gate is
`s.Status != "recording"` (`SessionsViewModel.cs:38`) → badge shows; `LoadCommand` canExecute keys
off the live engine (`_isRecording()`), not the row status → load works. Optionally show a small
"interrupted" chip on the row so the user knows the meeting ended abnormally (nice-to-have).

**Files:**
- `src/LocalTranscriber.Storage/StoreInterfaces.cs` + `SqliteStores.cs` — add a small store method,
  e.g. `ITranscriptEventStore.GetLastTimestampAsync(sessionId)` (single `SELECT MAX(timestamp)`),
  and reuse the existing `ISessionStore.EndAsync(id, endedAt, status)` (`SqliteStores.cs:30-39`) to
  write the fix — no new session-store method strictly required.
- A recovery routine (new `SessionRecoveryService` in `LocalTranscriber.Storage`, or a private
  helper) that lists sessions (`ISessionStore.ListAsync`), filters `status=="recording"`, and calls
  `EndAsync` for each.
- `src/LocalTranscriber.App/App.xaml.cs` `OnStartup` — run recovery once after the host is built,
  before `MainWindow.Show()` (it already resolves stores via DI; reuse `AddTranscriptionCore`'s
  registrations). Keep it fast and swallow/log failures so startup never breaks.

### 2. Make list/summary duration robust to a null `ended_at` (belt-and-suspenders)

So even the currently-live session (or any not-yet-recovered row) never shows a misleading "1m":
- In the summary path, when `ended_at` is null, derive the end from the session's last event
  timestamp. `SessionSummary` already carries an event count; extend the summary/query in
  `SqliteSessionStore.ListSummariesAsync` (`SqliteStores.cs:84-120`) to also surface
  `MAX(timestamp)` per session, and have `FormatDuration` prefer it when `EndedAt` is null.
- Low-risk, purely display; recovery (#1) makes it moot for stopped sessions but it protects the
  live row and is honest.

### 3. Retroactive speaker names in saved-session review (decided: yes)

Today `LoadArchive` / the preview render the raw baked-in `speaker_name`
(`MainWindowViewModel.cs:264-288`, `SessionsViewModel.cs:352-356`) with **no alias resolution**, so
early "Speaker 1" lines stay "Speaker 1" in review even though the user later named that voice
"Lokesh" (`speaker_aliases` has the mapping). Resolve names at load time so renamed voices show
their final name retroactively on every line.

**Implementation:** reuse the existing `SqliteSpeakerNameResolver` (`ISpeakerNameResolver`,
`SqliteSpeakerNameResolver.cs`) — the same resolver the agent path already uses. When loading an
archive's events, replace `Speaker.DisplayName` with `ResolveDisplayNameAsync(sessionId, speakerId)`
when it returns non-null (keep the stored name otherwise). Apply it once where events are loaded for
review so both the preview and the full review benefit:
- `SessionsViewModel.LoadSelectedAsync` / `LoadDetailAsync` (`SessionsViewModel.cs:352, 387`), or
- centrally in `MainWindowViewModel.LoadArchive` (`MainWindowViewModel.cs:264-276`).

Prefer resolving in the Sessions VM right after `ListBySessionAsync` so the resolved events flow to
both the preview rows and `LoadArchive`. Inject the resolver (build a `SqliteSpeakerNameResolver`
from the existing alias + known-speaker stores, or register it in DI) — mirror the wiring in
`docs/plans/speaker-identity-two-cuts.md`. Note: `speaker_id` must be the session speaker id
(e.g. `session_speaker_1`) for the alias lookup; verify the stored `speaker_id` column matches what
`speaker_aliases.session_speaker_id` holds.

## Verification

- **Repro the stuck state:** start a recording, let a few events land, kill the process (Task
  Manager) instead of clicking Stop. Confirm the `sessions` row is `recording`/null `ended_at`.
- **Recovery:** relaunch the app → open Sessions. The interrupted meeting now shows the correct
  duration and, on Load, the full transcript. Confirm the DB row is finalized:
  `SELECT status, ended_at FROM sessions WHERE id=…` is non-null.
- **Existing session:** after the fix, launching the app once repairs
  `a428a7aa16424c53980fec115af318a1` → duration ~24m, status finalized.
- **Unit tests** (`tests/LocalTranscriber.Storage.Tests`): recovery finalizes a `recording` row to
  `interrupted` and backfills `ended_at` from the last event; a row with no events falls back to
  `started_at`; a `stopped` row is left untouched. Duration-from-last-event when `ended_at` is null.
- **Retro rename:** load an archive whose early rows were captured as "Speaker 1" before a rename;
  assert those rows render as the final name once an alias exists (extend the resolver tests in
  `SqliteStoreTests.cs`).
- `dotnet test` green; run the app (`./scripts/run-app.ps1`) and eyeball the Sessions list.

## Notes / non-goals

- No changes to capture, transcription, or the append-only file writers — those are already correct
  and lost nothing.
- The 30-row preview cap is intentional; "Load" shows the full transcript. Not changed.
