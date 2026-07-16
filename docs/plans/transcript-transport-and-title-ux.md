---
title: Transcript Transport Cleanup + Session Title Save Feedback
version: 1.0
date_created: 2026-07-16
last_updated: 2026-07-16
confidence_level: 9
---
# Implementation Plan: Transcript Transport Cleanup + Session Title Save Feedback

## Goal

**Story Goal**: Simplify the live Transcript column UI so the recording controls are unambiguous and the session-title field gives clear "it saved" feedback.

**Deliverable**: Edits to `MeetingView.xaml`, `MeetingView.xaml.cs`, and `MainWindowViewModel.cs` in `LocalTranscriber.App`.

**Success Definition**: While recording, the user sees one context-aware primary transport button (Start → Pause → Resume) plus a Stop button that only appears when a session is live; the always-on "Following live — scroll up to pause" chip no longer shows while actively following; editing the session title and pressing Enter (or clicking away) shows a brief "Saved ✓" confirmation.

## Why

- The current footer shows four buttons at once (`● Start`, `❚❚ Pause`, `▶`, `■ Stop`) where Start/Resume are redundant and several are disabled depending on state — confusing.
- The "Following live — scroll up to pause" chip is a persistent disclaimer that adds noise; its only useful mode is the "jump back to live" state after the user scrolls up.
- The session-title box saves silently on every keystroke (`UpdateSourceTrigger=PropertyChanged`), but with no Save button and no feedback the user can't tell their rename took effect (they pressed Enter and "nothing happened").

## What

User-visible behavior:
- **Follow chip**: hidden while the transcript is auto-following; shown only as a "Paused — click to follow live" affordance once the user has scrolled up. (Removes the "Following live" disclaimer while keeping the jump-to-live action.)
- **Transport controls**: a single primary button whose label/command/appearance changes by state:
  - Idle/Stopped/Faulted → `● Start` (record-red, `StartCommand`)
  - Recording → `❚❚ Pause` (neutral, `PauseCommand`)
  - Paused → `▶ Resume` (neutral, `ResumeCommand`)
  - Plus a `■ Stop` button visible only while `IsRecording` (Recording or Paused).
- **Title feedback**: pressing Enter in the title box, or the box losing focus, commits and flashes a transient `Saved ✓` next to it for ~1.5s.

### Success Criteria

- [ ] Only one transport button (besides Stop) is visible at any time; its label matches the current state.
- [ ] `■ Stop` is hidden when idle/stopped and visible when recording or paused.
- [ ] The follow chip is not visible while actively following; it appears only after scrolling up and returns the list to live when clicked.
- [ ] Editing the title + Enter (or focus loss) shows `Saved ✓` briefly; the persisted title still round-trips (existing `UpdateSessionTitleAsync` path unchanged).
- [ ] `dotnet build` and `dotnet test` pass; no transcription/engine logic changed.

## All Needed Context

### Documentation & References

```yaml
- file: src/LocalTranscriber.App/Views/MeetingView.xaml
  why: Contains the follow chip (Grid.Row=1 overlay), the transport footer (Grid.Row=3), and the inline session-title TextBox in the live header.
  pattern: DataTrigger-driven Visibility/Text swaps already used throughout (e.g. follow chip text trigger, placeholder TextBlocks).
  gotcha: Follow chip binds to attached property behaviors:AutoScrollBehavior.IsFollowing on TranscriptList; keep that binding for the paused state.

- file: src/LocalTranscriber.App/ViewModels/MainWindowViewModel.cs
  why: Owns Start/Pause/Resume/Stop commands, the TranscriptionSessionState machine (SetState), IsRecording, and the SessionTitle setter that persists via _engine.UpdateSessionTitleAsync.
  pattern: SetState() already calls RaiseCanExecuteChanged on all four commands and OnPropertyChanged for derived bools — add new notifications there.
  gotcha: SessionTitle already saves on every keystroke; do NOT change the persistence path, only add feedback + a public state signal for the primary button.

- file: src/LocalTranscriber.App/Views/MeetingView.xaml.cs
  why: Code-behind for FollowLive_Click (re-follow) and where a title KeyDown/LostFocus handler will live.
  pattern: Simple event handlers referencing Shell (MainWindow) and named controls (TranscriptList).

- file: src/LocalTranscriber.App/Themes/Controls.xaml
  why: Button styles to reuse — PrimaryButtonStyle, RecordButtonStyle, NeutralButtonStyle, DangerButtonStyle, SubtleButtonStyle, TextInputStyle.
  pattern: Reuse existing styles; do not author new ones.
```

### Known Gotchas of our codebase & Library Quirks

```text
# The primary transport button must swap Command by state. Binding a Command via DataTrigger is
#   brittle in WPF; prefer a single ToggleTransportCommand in the VM that dispatches to
#   Start/Pause/Resume based on _state, plus a bindable state signal for label/appearance triggers.
# IsRecording is (Recording OR Paused). Do NOT reuse it to distinguish Pause vs Resume — expose
#   the raw state (or IsPaused) for the label triggers.
# SessionTitle persistence already fires on PropertyChanged; the "Saved" flash should trigger off
#   that same setter (only when a real session id exists) so keystroke-save and Enter agree.
# No engine/transcription logic changes. This is App-project (WPF) only.
```

## Implementation Blueprint

### Implementation Tasks (ordered by dependencies)

```yaml
Task 1: MODIFY src/LocalTranscriber.App/ViewModels/MainWindowViewModel.cs — transport state + toggle
  - ADD: public enum-backed signal for the primary button. Simplest: expose
      public bool IsIdleTransport => _state is NotStarted or Stopped or Faulted;
      public bool IsPausedTransport => _state == Paused;
      public bool IsActiveRecording => _state == Recording;
    (Recording-vs-Paused label distinction; IsRecording stays as-is for Stop visibility.)
  - ADD: ToggleTransportCommand (AsyncRelayCommand) that dispatches:
      idle/stopped/faulted -> StartAsync; Recording -> PauseAsync; Paused -> ResumeAsync.
    CanExecute: always true unless in a transitional state (Starting/Stopping).
  - UPDATE SetState(): after existing RaiseCanExecuteChanged calls, add
      ToggleTransportCommand.RaiseCanExecuteChanged();
      OnPropertyChanged(nameof(IsIdleTransport));
      OnPropertyChanged(nameof(IsPausedTransport));
      OnPropertyChanged(nameof(IsActiveRecording));
  - FOLLOW pattern: existing command construction in the constructor and SetState notifications.

Task 2: MODIFY src/LocalTranscriber.App/ViewModels/MainWindowViewModel.cs — title save feedback
  - ADD: private bool _titleSavedFlash; public bool TitleSavedFlash { get; private set; } with SetProperty.
  - ADD: private DispatcherTimer? _titleFlashTimer; FlashTitleSaved() sets TitleSavedFlash=true,
    (re)starts a ~1.5s one-shot timer that clears it.
  - UPDATE SessionTitle setter: after the existing UpdateSessionTitleAsync call (inside the
    `if (!string.IsNullOrEmpty(_sessionId))` block), call FlashTitleSaved().
  - GOTCHA: Only flash when a session id exists (matches when persistence actually runs).
  - FOLLOW pattern: existing DispatcherTimer usage (_healthTimer / _elapsedTimer) for lifecycle.

Task 3: MODIFY src/LocalTranscriber.App/Views/MeetingView.xaml — transport footer
  - REPLACE the four-button StackPanel in the live footer (Grid.Row=3, IsReviewing-false grid)
    with: one primary Button (Content/Command/Style driven by IsIdleTransport / IsActiveRecording /
    IsPausedTransport via Style triggers, bound Command="{Binding Session.ToggleTransportCommand}")
    and one Stop Button (DangerButtonStyle, Command=StopCommand,
    Visibility="{Binding Session.IsRecording, Converter={StaticResource BoolToVisibility}}").
  - Primary button appearance via Style.Triggers on the three bool props:
      idle -> "● Start" + Brush.Rec.Gradient (record look);
      recording -> "❚❚ Pause" + neutral;
      paused -> "▶ Resume" + neutral.
  - PRESERVE the SourcesSummary TextBlock in the same grid (Column 2).

Task 4: MODIFY src/LocalTranscriber.App/Views/MeetingView.xaml — follow chip + title flash
  - Follow chip Button: change Visibility to show ONLY when not following. Bind Visibility to the
    attached prop AutoScrollBehavior.IsFollowing on TranscriptList inverted AND Session.IsRecording.
    Simplest: keep the recording-gated Button but add a Style/DataTrigger that Collapses it while
    IsFollowing=True; drop the "Following live — scroll up to pause" text branch (keep only
    "Paused — click to follow live").
  - Session-title TextBox: add KeyDown="SessionTitle_KeyDown" and LostFocus commit; add an adjacent
    "Saved ✓" TextBlock whose Visibility binds to Session.TitleSavedFlash (BoolToVisibility),
    small/hint styling, placed in the live header grid next to the title box.

Task 5: MODIFY src/LocalTranscriber.App/Views/MeetingView.xaml.cs — title Enter handler
  - ADD: SessionTitle_KeyDown — on Key.Enter, set e.Handled=true and move focus off the box
    (e.g. Keyboard.ClearFocus() or focus TranscriptList) so the binding commits and the flash
    (already fired on the PropertyChanged setter) reads as an explicit confirmation.
  - FOLLOW pattern: existing ChatInput_KeyDown handler.

Task 6: BUILD + TEST
  - RUN: dotnet build; dotnet test.
  - No new tests required (VM logic is trivial UI state); verify existing suites stay green.
```

### Implementation Patterns & Key Details

```csharp
// Task 1 — single toggle that dispatches by state (avoids Command-swapping in XAML)
public AsyncRelayCommand ToggleTransportCommand { get; }
// in ctor:
ToggleTransportCommand = new AsyncRelayCommand(ToggleTransportAsync,
    () => _state is not (TranscriptionSessionState.Starting or TranscriptionSessionState.Stopping));
private Task ToggleTransportAsync() => _state switch
{
    TranscriptionSessionState.Recording => PauseAsync(),
    TranscriptionSessionState.Paused    => ResumeAsync(),
    _                                    => StartAsync(),
};

// Task 2 — transient "Saved" flash (reuse DispatcherTimer lifecycle pattern)
private void FlashTitleSaved()
{
    TitleSavedFlash = true;
    _titleFlashTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
    _titleFlashTimer.Tick -= OnTitleFlashTick;
    _titleFlashTimer.Tick += OnTitleFlashTick;
    _titleFlashTimer.Stop();
    _titleFlashTimer.Start();
}
private void OnTitleFlashTick(object? s, EventArgs e) { _titleFlashTimer!.Stop(); TitleSavedFlash = false; }
```

```xml
<!-- Task 3 — primary transport button appearance via triggers (label/brush by state) -->
<Button Command="{Binding Session.ToggleTransportCommand}" Padding="14,6" Margin="0,0,8,0">
  <Button.Style>
    <Style TargetType="Button" BasedOn="{StaticResource NeutralButtonStyle}">
      <Setter Property="Content" Value="▶ Resume" />
      <Style.Triggers>
        <DataTrigger Binding="{Binding Session.IsActiveRecording}" Value="True">
          <Setter Property="Content" Value="❚❚ Pause" />
        </DataTrigger>
        <DataTrigger Binding="{Binding Session.IsIdleTransport}" Value="True">
          <Setter Property="Content" Value="● Start" />
          <Setter Property="Style" Value="{StaticResource RecordButtonStyle}" />
        </DataTrigger>
      </Style.Triggers>
    </Style>
  </Button.Style>
</Button>
<!-- NOTE: a trigger cannot set the Style property of its own target; instead apply the record
     look with Background/Foreground setters, OR keep two Buttons (one idle "Start", one active
     "Pause/Resume") toggled by Visibility. Prefer the two-visibility-swapped-buttons approach if
     the single-button restyle proves awkward — still one visible at a time. -->
```

## E2E Validation Plan
- [ ] Start a session: footer shows `● Start` (red) when idle; after Start it shows `❚❚ Pause` + `■ Stop`.
- [ ] Click Pause → button becomes `▶ Resume`, Stop still visible; click Resume → back to `❚❚ Pause`.
- [ ] Stop → Stop button disappears, primary returns to `● Start`.
- [ ] While recording and auto-following, no follow chip is shown; scroll up → "Paused — click to follow live" appears; click it → returns to live and chip hides again.
- [ ] Type a new session title, press Enter → `Saved ✓` flashes ~1.5s and disappears; reopen the session later and confirm the title persisted.
- [ ] Click away from the title box (focus loss) → title commits (no crash, flash acceptable).

### Anti-Patterns to Avoid

- ❌ Don't swap a Button's `Style` property from inside its own Style trigger (invalid) — use Background/Foreground setters or two visibility-swapped buttons.
- ❌ Don't change `UpdateSessionTitleAsync` or any engine/persistence logic — feedback only.
- ❌ Don't remove the jump-to-live capability; only remove the always-on "Following live" disclaimer text.
- ❌ Don't add new button styles when existing ones (Record/Neutral/Danger) cover it.
- ❌ Don't reuse `IsRecording` to distinguish Pause vs Resume — it's true for both.
