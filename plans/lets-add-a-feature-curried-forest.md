# Undo speaker name change (button + Ctrl+Z)

## Context

When a user renames a speaker in the Meeting transcript (clicking a speaker name
opens the rename dialog), there is currently **no way to undo it** — no undo/redo
exists anywhere in the app. A mis-click, typo, or wrong scope choice can only be
fixed by renaming again by hand, and for the "name an unknown speaker for all"
case that also silently enrolls a voice. This adds a multi-level Undo for
transcript speaker renames, reachable by a toolbar button in the transcript header
and by `Ctrl+Z`.

Decisions confirmed with the user:
- **Scope:** Meeting transcript renames only (not the Speakers roster screen).
- **Depth:** Multi-level in-memory stack, session-scoped (cleared when the displayed
  transcript is swapped/cleared). Redo is out of scope.
- **Enroll case ("All" on an unknown speaker):** undo restores the visible names and
  removes the session alias so the display fully reverts; the enrolled voice
  embedding is intentionally left in place (documented limitation).

## Reversal per rename type

`ExecuteRenameSpeakerAsync` (`MainWindowViewModel.cs:126-186`) has three branches; each
gets an inverse pushed onto the undo stack right after it succeeds:

| Forward op | Store write | Undo |
|---|---|---|
| `All` + known speaker | `RenameKnownSpeakerAsync(old→new)` | reuse `RenameKnownSpeakerAsync(new→old)` (existing method) |
| `This one` | `OverrideEventSpeakerAsync` (override upsert) | clear the override (new `ClearEventSpeakerOverrideAsync`) + restore row |
| `All` + unknown (enroll) | `NameSessionSpeakerAsync` (enroll + alias upsert) | delete the alias (new `ClearSessionSpeakerAliasAsync`) + restore rows; keep embeddings |

In every case the inverse **also restores the affected in-memory rows'** captured prior
`SpeakerName`, `IsUnknownSpeaker`, and `SpeakerBrush` so the UI reverts immediately
(the store change only matters on reload / for the agent).

## Changes

### 1. Storage — add delete methods (`src/LocalTranscriber.Storage/`)
These are the only two stores lacking a reverse op (the embedding + known-speaker
stores already have `DeleteBySpeakerAsync`/`ForgetAsync`). Both interfaces are
implemented **only** by the Sqlite classes — no fakes to update.

- `StoreInterfaces.cs`
  - `IEventSpeakerOverrideStore.DeleteAsync(sessionId, eventId, ct)`
  - `ISpeakerAliasStore.DeleteAsync(sessionId, sessionSpeakerId, ct)`
- `SqliteStores.cs`
  - `SqliteEventSpeakerOverrideStore.DeleteAsync` → `DELETE FROM event_speaker_overrides WHERE session_id=$sid AND event_id=$eid`
  - `SqliteSpeakerAliasStore.DeleteAsync` → `DELETE FROM speaker_aliases WHERE session_id=$sid AND session_speaker_id=$ssid`

  Follow the existing `UpsertAsync` bodies in the same classes for connection/param style.
  After a delete, `SqliteSpeakerNameResolver` falls through correctly: override→gone
  reverts to alias/known/baked; alias→gone reverts a `session_*` id to the baked
  auto-label. (Resolver has a 5 s TTL cache — harmless; the live UI is driven by the
  in-memory row restore, not the resolver.)

### 2. Engine — expose the two clears (`src/LocalTranscriber.Engine/`)
- `ITranscriptionEngine.cs`: add
  `ClearEventSpeakerOverrideAsync(sessionId, eventId, ct)` and
  `ClearSessionSpeakerAliasAsync(sessionId, sessionSpeakerId, ct)` (mirror the XML-doc
  style of the neighbouring `OverrideEventSpeakerAsync` / `NameSessionSpeakerAsync`).
- `RealTranscriptionEngine.cs` (near `OverrideEventSpeakerAsync`, ~603): implement by
  delegating to `_overrideStore.DeleteAsync` / `_aliasStore.DeleteAsync` with the same
  null-store guards used elsewhere.
- `FakeTranscriptionEngine.cs` (~287-294): add trivial stubs matching the existing
  `Task.FromResult`/`Task.CompletedTask` pattern.

### 3. App — the undo stack, command, button, and hotkey

**`ViewModels/MainWindowViewModel.cs`** (all near the existing rename code, 75-186):
- Add `private readonly Stack<(string Label, Func<Task> UndoAsync)> _undoStack = new();`
- Add `public AsyncRelayCommand UndoRenameCommand { get; }`, wired in the ctor:
  `new AsyncRelayCommand(UndoLastRenameAsync, () => _undoStack.Count > 0);`
- Add `public string UndoTooltip` (getter): `"Undo rename → {last old name} (Ctrl+Z)"`
  when non-empty, else `"Nothing to undo"`.
- Add a private `RaiseUndoChanged()` that calls `UndoRenameCommand.RaiseCanExecuteChanged()`
  and `OnPropertyChanged(nameof(UndoTooltip))`.
- In `ExecuteRenameSpeakerAsync`, after each branch succeeds, capture the affected rows'
  prior (`SpeakerName`, `IsUnknownSpeaker`, `SpeakerBrush`) and push a closure that
  performs the matching inverse from the table above, then `RaiseUndoChanged()`.
  - Capture the `sessionId` used (local) inside the closure so `This one` undo targets
    the same event. For the enroll case, the alias key is `row.SpeakerId` (the
    `session_*` id the resolver aliases on).
  - Track overridden event-ids in a VM-local `HashSet<string>` so a repeated `This one`
    on the same line undoes to its *previous* override value (re-upsert) rather than
    clearing; otherwise clear. (Minor fidelity nicety; the in-memory restore is always
    exact.)
- Add `UndoLastRenameAsync`: pop, `await entry.UndoAsync()`, `RaiseUndoChanged()`.
- Add `private void ClearUndoHistory()` (clear stack + `RaiseUndoChanged()`) and call it
  everywhere the displayed transcript is swapped/reset: `LoadArchive` (304),
  `LoadForContinuation` (331), `CancelContinuation` (357), `CloseReview` (373), and the
  start-of-session reset (~742/757). **Not** on live-append collection changes.

**`Views/MeetingView.xaml`** — add an "Undo" button in the transcript live header next
to the existing Copy button (~157-173), same style, `Command="{Binding Session.UndoRenameCommand}"`,
`ToolTip="{Binding Session.UndoTooltip}"`. It disables automatically via `CanExecute`
when the stack is empty. (Optionally mirror into the review-mode header at ~180-219,
since rename works in review too — include it there for parity.)

**`MainWindow.xaml.cs`** — in `OnPreviewKeyDown` (196), add a branch **before** the Space
handler, mirroring its focus guard:
```csharp
if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control
    && Keyboard.FocusedElement is not TextBoxBase
    && Session.SelectedScreenIndex == (int)AppScreen.Meeting)
{
    if (Session.UndoRenameCommand.CanExecute(null)) Session.UndoRenameCommand.Execute(null);
    e.Handled = true;
    return;
}
```
The `TextBoxBase` guard preserves native text undo in the session-title / chat / rename
fields; gating on the Meeting screen keeps `Ctrl+Z` scoped to where the transcript lives.
(Use the code-behind handler rather than `Window.InputBindings`, which would hijack
`Ctrl+Z` from those text boxes — consistent with the existing Space hotkey pattern.)

## Files to touch
- `src/LocalTranscriber.Storage/StoreInterfaces.cs`, `SqliteStores.cs`
- `src/LocalTranscriber.Engine/ITranscriptionEngine.cs`, `RealTranscriptionEngine.cs`, `FakeTranscriptionEngine.cs`
- `src/LocalTranscriber.App/ViewModels/MainWindowViewModel.cs`
- `src/LocalTranscriber.App/Views/MeetingView.xaml`
- `src/LocalTranscriber.App/MainWindow.xaml.cs`

## Verification
- **Build:** `dotnet build`.
- **Unit tests (Storage.Tests):** add round-trip tests for the two new deletes —
  upsert override → `ResolveAsync` returns name → `DeleteAsync` → `ResolveAsync` returns
  null; same for the alias store. `dotnet test`. (There is no App test project; VM undo
  itself is verified manually below — do not add one for this.)
- **Manual (run-app):** `./scripts/run-app.ps1`, start or open a session with ≥2 speakers.
  1. Rename a known speaker with **All** → click **Undo** → name reverts on every row;
     confirm persisted by reopening the session (Sessions screen).
  2. Rename one line with **This one** → `Ctrl+Z` → that row reverts; reopen to confirm
     the override is gone.
  3. Name an **unknown** speaker with **All** (enrolls) → **Undo** → labels revert and,
     on reopen, the auto-label is back (voice embedding intentionally remains).
  4. Do several renames, then `Ctrl+Z` repeatedly → they unwind in reverse order; the
     button/tooltip disable when the stack empties.
  5. With focus in the session-title or chat box, `Ctrl+Z` still does normal text undo
     (does not trigger speaker undo).
