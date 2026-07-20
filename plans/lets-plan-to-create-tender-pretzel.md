# Delete a transcript line (with undo)

## Context

The Meeting screen shows the live/review transcript as a list of turns. Today a user can
**rename** a speaker per line, and that rename can be reverted via an in-memory undo stack
(button + Ctrl+Z). There is **no way to delete a wrong or junk transcript line** (mis-fired
capture, cross-talk, a stray "uh"), and the undo system only knows about renames.

This adds a per-line **Delete** action on the Meeting screen (live and review), and **unifies
the existing undo** so it reverts deletes as well as renames from the same stack / Ctrl+Z.

Decisions (confirmed with the user):
- Delete affordance: **both** a hover `✕` button and a right-click context menu.
- **Immediate delete + undo** (no confirmation dialog) — undo/Ctrl+Z restores the line.
- Allowed on the **Meeting screen live and review**, but **not** the Sessions-detail preview.

Design principle (mirrors the existing per-line rename): the append-only `.txt`/`.jsonl`
transcript files are an **immutable raw log** and are *not* rewritten. Delete removes only the
SQLite `transcript_events` row and the on-screen row; undo re-inserts both. This matches how
per-line speaker renames already work (they write a side-table override and never touch the
flat files), and keeps the writer abstraction (append-only) unchanged.

## Approach

The undo stack is already operation-agnostic — a `Stack<(string Label, Func<Task> UndoAsync)>`
where each entry is an inverse closure (`MainWindowViewModel.cs:89`). Delete just pushes its own
inverse. The only real new plumbing is a **single-event delete** in the store + engine, plus the
UI affordances in the shared row template.

## Changes by layer

### 1. Storage — add single-event delete

`ITranscriptEventStore` only has bulk `DeleteBySessionAsync` today
(`src/LocalTranscriber.Storage/StoreInterfaces.cs:25-39`). `InsertAsync` already exists and is
reused for the undo (re-insert).

- **`StoreInterfaces.cs`** — add `Task DeleteAsync(string eventId, CancellationToken ct = default);`
  to `ITranscriptEventStore`.
- **`SqliteStores.cs`** (`SqliteTranscriptEventStore`, ~148-245) — implement it as
  `DELETE FROM transcript_events WHERE id = $id` (PK is `id`, per `SqliteDatabase.cs:92-106`).
  Mirror the existing per-line delete `SqliteEventSpeakerOverrideStore.DeleteAsync`
  (`SqliteStores.cs:522-530`) for shape. Deleting a non-existent id is a harmless no-op.

### 2. Engine — expose delete + restore

Mirror the null-guarded `OverrideEventSpeakerAsync` / `ClearEventSpeakerOverrideAsync` pair
(`RealTranscriptionEngine.cs:603-624`, interface `ITranscriptionEngine.cs:19-22`).

- **`ITranscriptionEngine.cs`** — add:
  - `Task<bool> DeleteEventAsync(string sessionId, string eventId, CancellationToken ct = default);`
  - `Task<bool> RestoreEventAsync(TranscriptEvent e, CancellationToken ct = default);` (undo path — re-inserts the captured event)
- **`RealTranscriptionEngine.cs`** — implement both: null-guard `_eventStore`, delegate to the new
  `DeleteAsync` / existing `InsertAsync`, return bool.
- **`FakeTranscriptionEngine.cs`** — implement both against its `_eventStore` the same way.

### 3. Row view model — retain the original event for undo

`TranscriptRowViewModel` currently discards the `TranscriptEvent` after copying display fields
(`TranscriptRowViewModel.cs:15-27`). Undo of a delete must re-insert the full event.

- Add a `public TranscriptEvent Event { get; }` (set from the ctor's `e`). No other change; the
  undo closure re-inserts `row.Event` and re-adds the *same* row VM object, preserving any
  resolved override name/brush exactly.

### 4. MainWindowViewModel — delete command + unify undo

In `MainWindowViewModel.cs`:

- **New command**, constructed next to `RenameSpeakerCommand` (line 64):
  `DeleteLineCommand = new AsyncRelayCommand<TranscriptRowViewModel>(ExecuteDeleteLineAsync);`
- **`ExecuteDeleteLineAsync(row)`**:
  1. `int index = Transcript.IndexOf(row); if (index < 0) return;`
  2. `var e = row.Event; string sid = _sessionId;`
  3. `if (!await _engine.DeleteEventAsync(sid, row.EventId)) return;`
  4. `Transcript.RemoveAt(index);` (CollectionChanged already refreshes `ShowIdlePanel` +
     `CopyTranscriptCommand`, lines 68-72)
  5. If a review search is active, re-run the existing match recompute (see `StepMatch`/match
     logic) so highlight/counts stay correct.
  6. `PushUndo($"delete → \"{snippet}\"", async () => { await _engine.RestoreEventAsync(e); Transcript.Insert(index, row); /* recompute matches if reviewing */ });`
     where `snippet` is the first ~24 chars of `row.Text`.
- **Unify the undo tooltip/label** (currently hardcodes "rename"):
  - Change `UndoTooltip` (line 98-100) to `$"Undo {label} (Ctrl+Z)"`.
  - Update the three rename `PushUndo` labels (lines 223, 268, 277) from `oldName` to
    `$"rename → {oldName}"` so the tooltip still reads "Undo rename → X" and delete reads
    "Undo delete → …".
- **Rename the command for clarity** (undo is now multi-op): `UndoRenameCommand` → `UndoCommand`,
  `UndoLastRenameAsync` → `UndoLastAsync`. Low-risk; update the three binding sites in step 5.
  The stack, `PushUndo`, `ClearUndoHistory`, and the Ctrl+Z gate are already operation-agnostic —
  no logic change.

### 5. UI — delete affordances in the shared row template

The `TranscriptRowTemplate` lives in `src/LocalTranscriber.App/Themes/Controls.xaml:8-53` and is
shared by the Meeting list, review, **and** the Sessions-detail preview. Gate the delete UI so it
appears only where allowed.

- **Marker for "editable" lists.** Set `Tag="editable"` on the Meeting/review `ListBox`
  (`MeetingView.xaml` `TranscriptList`, ~297-322). Do **not** set it on the Sessions-detail
  preview list. In the template, gate both affordances on the ancestor ListBox's `Tag` via a
  `MultiDataTrigger` (Tag == "editable" — and for the hover button, also `IsMouseOver` on `RowBd`).
- **Hover `✕` button.** Add a 3rd `Auto` column to the row `Grid` (Controls.xaml:10-14) with a
  small transparent `✕` button, `Collapsed` by default, shown on row hover (MultiDataTrigger
  above). Wire it exactly like the existing rename hyperlink pattern:
  `Command="{Binding DataContext.Session.DeleteLineCommand, RelativeSource={RelativeSource AncestorType=ListBox}}"`,
  `CommandParameter="{Binding}"`.
- **Context menu.** Add a `ContextMenu` on `RowBd` with a "Delete line" `MenuItem`. Because a
  ContextMenu is outside the visual tree, reach the command via `PlacementTarget`: set
  `RowBd.Tag` to the ListBox `DataContext` (`RelativeSource AncestorType=ListBox`), then
  `Command="{Binding PlacementTarget.Tag.Session.DeleteLineCommand, RelativeSource={RelativeSource AncestorType=ContextMenu}}"`
  and `CommandParameter="{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource AncestorType=ContextMenu}}"`.
  Gate the ContextMenu so it is only attached when the ListBox marker is "editable".

> Note: the Sessions-detail preview list's `DataContext` (SessionsViewModel) has no `Session`
> property, so even absent gating the command would resolve to null; the marker keeps the UI from
> *appearing* there at all.

### 6. Tests

In `tests/LocalTranscriber.Storage.Tests/SqliteStoreTests.cs`, add
`TranscriptEvents_Delete_RemovesOnlyTargetLine` modeled on
`EventSpeakerOverrides_Delete_RemovesOnlyTargetAndUndoesRename` (lines 375-393) and
`Sessions_DeleteRemovesOnlyTargetRows` (110-129): insert two events, delete one, assert the
sibling survives, assert deleting a missing id is a no-op, and assert a re-`InsertAsync`
(the undo path) round-trips the row back.

## Considerations / out of scope

- **Flat files unchanged.** `.txt`/`.jsonl` keep the deleted line (append-only raw log), matching
  rename behavior. If the user later wants files to reflect edits, that is a separate feature
  (regenerate from `transcript_events` on a stopped session).
- **Orphaned references.** `agent_suggestions.related_transcript_event_id`
  (`SqliteDatabase.cs:118`) may reference a deleted event id, leaving a dangling ref. Harmless for
  display; not cleaned up here — flag only.
- Undo remains **in-memory, session-scoped** (cleared on transcript swap, `ClearUndoHistory`), so a
  delete is permanent after the transcript is reloaded/closed — same lifetime as rename undo today.

## Verification

1. `dotnet build` and `dotnet test` (new store test + existing 133 green).
2. Run the app: `./scripts/run-app.ps1` (or F5). Start a session / load a fake transcript.
3. Live: hover a line → `✕` appears → click → line disappears. Right-click a line → **Delete line**.
   Press **Ctrl+Z** (and the **↶ Undo** button) → line returns at its original position; tooltip
   reads "Undo delete → …".
4. Do a rename, then a delete, then Ctrl+Z twice → both revert in order (shared stack); rename
   tooltip still reads "Undo rename → …".
5. Open a past session from **Sessions**, enter its transcript (review) → delete works there too;
   confirm the **Sessions-detail preview** list shows **no** delete affordance.
6. Confirm `.txt`/`.jsonl` in `output/` still contain the deleted line (immutable log, expected).
