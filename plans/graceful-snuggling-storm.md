# Fix: dead dropdowns, silent push-to-talk, and invisible assistant-start

## Context

The user reported that in the WPF app (post-redesign) **all dropdowns are dead** (clicking
does nothing), **push-to-talk never responds**, and the **"Start" button that connects the
assistant/LLM does nothing visible**. Investigation of the code plus the live log
(`output/logs/localtranscriber-20260715.log`) established three largely independent causes,
plus one user-side hardware factor (mic currently unavailable). This plan fixes the confirmed
code bugs, hardens the push-to-talk path so it can never silently no-op again, and adds the
logging needed to confirm the assistant-start behavior at runtime.

Findings:
- **Dropdowns:** the custom `ComboBoxStyle` control template has no `ToggleButton`, so there is
  no hit-testable element to open the popup. All 7 ComboBoxes use this style → all dead.
- **Push-to-talk:** config has `agent.provider = hybrid` (Claude brain) but
  `agent.realtime.voiceMode = pushToTalk`. `ClaudeCliConversation.OnPushToTalkDownAsync` only
  implements the `hybrid` capture mode and returns immediately for any other mode — no log, no
  state change. Every press was a genuine silent no-op. (Secondarily, the mic is currently down
  at the OS level: `Microphone unavailable (no default recording device)` — a user-side item.)
- **Assistant "Start":** the button is enabled and correctly bound to `StartVoiceCommand`; the
  whole voice/agent path emits almost nothing to the log file, so a failing/degrading hybrid
  connect (WSL `claude` + OpenAI TTS first-connect TLS reset) is invisible. Needs runtime repro
  with added logging.

User decisions: **harden code + fix config** for push-to-talk; **add logging + verify at
runtime** for the assistant-start button.

## Changes

### 1. Fix the ComboBox template (dropdowns) — `src/LocalTranscriber.App/Themes/Controls.xaml`

In `ComboBoxStyle` (lines ~346–400), the template `Grid` currently holds only a `Border` (chrome,
no click handling) and `PART_Popup`. Add the missing interactive parts:

- Wrap the existing chrome `Border` ("Bd") inside a **`ToggleButton`** whose
  `IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"`
  and `ClickMode="Press"`, with a minimal transparent `ControlTemplate` (just a
  `ContentPresenter`) so it doesn't reintroduce default chrome. This is the element that flips
  `IsDropDownOpen`, which `PART_Popup` is already bound to.
- Add a **`PART_EditableTextBox`** `TextBox` (visible only when `IsEditable=True`) so the editable
  ComboBox at `MeetingView.xaml:399` works. Keep the non-editable `ContentPresenter`
  (`SelectionBoxItem`) for the normal case; toggle their visibility on the `IsEditable` trigger,
  matching the standard WPF ComboBox template pattern.
- Preserve the existing visuals (CornerRadius 8, ▾ arrow, accent border on open, hover fill) and
  the existing `ComboBoxItemStyle` / `PART_Popup` (both fine).

No other file changes are needed for the dropdowns — every ComboBox already points at this one
style (`SettingsView.xaml:202,253`; `MeetingView.xaml:399,764,768,787,791`; `SessionsView.xaml:53`).

### 2. Harden push-to-talk for the Claude/hybrid brain — `src/LocalTranscriber.Voice/ClaudeCliConversation.cs`

The Claude brain has no audio-streaming provider; its only capture path is local Whisper. The
`Mode != RealtimeVoiceMode.Hybrid` early-returns at lines 129 and 142 are what make PTT silently
dead when the mode is `pushToTalk`/`continuous`.

- Remove those two mode guards so push-to-talk **always** performs local-Whisper capture
  (start recorder on down, transcribe + send text on up) regardless of the `voiceMode` string —
  since that is the only capture this backend supports.
- Add `AppLog.Info("claude-cli", ...)` at capture start and at transcribe/send so a press is
  always visible in the log. (There is already `AppLog.Warn` for STT failure at line 172.)

This makes the behavior correct even if config drifts again, satisfying "harden code."

### 3. Fix config — `output/config.json`

Set `agent.realtime.voiceMode` from `"pushToTalk"` to `"hybrid"` — the intended mode for a
Claude-brain provider (see `AgentPanelViewModel.cs:93-95,731`, which treat `hybrid` as the
Claude-brain capture mode). Belt-and-suspenders with change #2.

### 4. Add voice/agent-start logging — `src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs`

The path currently only sets `StatusText` (not shown prominently) and logs nothing. Add
`AppLog.Info`/`AppLog.Warn` (via `LocalTranscriber.Shared` `AppLog`) at:
- `StartVoiceAsync` (line 543): entry (provider + mode), `resolution.Session is null` branch
  (line 579, log `resolution.Notice`), success (line 594 after `StartAsync`), and the `catch`
  (line 606).
- `VoicePushToTalkDown`/`Up` and `StartVoiceThenTalkAsync` (lines 694–733): log down/up and the
  "released before connect" no-op branch (line 716).

This turns the currently-invisible assistant-start into something the log runner can capture.

## Verification

1. **Build:** `dotnet build` (VS Code terminal / PowerShell). Must be clean.
2. **Dropdowns:** `./scripts/run-with-logs.ps1 -Target app`, open Settings and the Meeting view,
   click each dropdown — the popup must open and selection must apply. Test the editable combo in
   MeetingView (line 399) too.
3. **Push-to-talk:** with `voiceMode=hybrid` and a working mic, hold the 🎙 Hold button / Space and
   speak; confirm the new `claude-cli` capture + transcribe INFO lines appear via
   `./scripts/tail-logs.ps1 -Target app` and that a user turn is sent.
4. **Assistant "Start":** click Start-to-connect; read the new `StartVoiceAsync` log lines to
   confirm whether it connects, degrades to captions-only (TTS TLS reset), or fails — then fix the
   specific failure that surfaces (out of scope until reproduced).
5. **Regression:** `dotnet test` (baseline 133 green) — the ComboBox change is XAML-only; the
   `ClaudeCliConversation` guard removal may touch existing voice tests, so review those.

## User-side (not code)

- **Mic:** log shows `Microphone unavailable (no default recording device)`. The user needs a
  working default recording device (or select the Voice mic in the devices panel) before
  push-to-talk can capture anything. Once dropdowns are fixed, the device dropdown becomes usable
  again for this.

## Out of scope

- Root-causing the assistant-start failure itself (step 4) — deferred until the added logging
  reproduces it.
- The recurring `SystemAudio capture stalled` reconnect warnings (pre-existing, benign).
