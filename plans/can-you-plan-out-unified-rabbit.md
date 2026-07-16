# Realtime-only brain: skip the claude-cli middleman

## Context

The assistant panel supports three backends via `agent.provider`: `openai` (direct OpenAI Realtime websocket), `claude-cli` (spawns claude.exe per turn), and `hybrid` (claude-cli brain + realtime TTS mouth). The claude-cli spawn-per-turn latency is painful. The user wants to seamlessly choose "just realtime" — the OpenAI Realtime model as the brain — for **both typed chat and voice**, with a **quick toggle in the Agent panel**, keeping **transcript grounding**.

Key finding: the direct path already exists (`provider=openai` → `RealtimeVoiceSession`), but it's crippled:
1. Typed chat is **blocked when voiceMode=off** — `RealtimeVoiceFactory.cs:28` returns an "off" notice and `CanConverse` (`AgentPanelViewModel.cs:340`) disables the chat box.
2. Replies are always generated as **audio** (`output_modalities:["audio"]` hardcoded at `RealtimeVoiceSession.cs:208`) with captions from the audio transcript — wasted latency/cost when nothing is played.
3. Switching brains requires the Settings dropdown; no in-panel toggle.
4. `RealtimeVoiceEventMapper` doesn't map `response.output_text.delta/done` (they fall to `Other` and are dropped) — the one hard blocker for a text-modality session.

So the work is: unlock text-only realtime chat, use text modality where audio is wasted, add the brain toggle, and implement `CancelTurn` on the realtime session.

## Design decisions

- **D1 — Text-only is implicit, no new mode config.** New derived property on `RealtimeVoiceOptions`:
  `public bool TextOnlyOutput => Mode == Off || (!SpeakReplies && Mode != Continuous);`
  - `voiceMode=off` becomes "typed text chat" instead of "blocked": text modality, no mic, no playback. Push-to-talk paths already no-op for `Off`.
  - `SpeakReplies=false` + hybrid/pushToTalk also switch to `["text"]` — nothing is ever played there (`NoOpAgentAudioOutput`), and those modes never receive `speech_started` (server VAD is continuous-only, `ConfigureTurnDetection` returns null), so barge-in/truncate logic is unreachable — safe.
  - **Continuous keeps `["audio"]`** even with SpeakReplies=false: its barge-in depends on audio-delta `_currentItemId` + `conversation.item.truncate` with `audio_end_ms`. Follow-up, not now.
- **D2 — Map text events onto the existing caption channel** (`AudioTranscriptDelta`/`Done` kinds) — zero changes in `HandleServerEventAsync`.
- **D3 — Brain toggle = persist provider + teardown + lazy reconnect.** Two positions: **Realtime** = `openai`; **Claude** = last-used claude flavor (`claude-cli` or `hybrid`), remembered in new config `agent.lastClaudeProvider`. On flip with a live session: stop/dispose `_voice`; the existing lazy-start in `SendTextAsync` / `VoicePushToTalkDown` reconnects on next use (same pattern as the archive-grounding switch).
- **D4 — `CancelTurn` on `RealtimeVoiceSession`** sends `response.cancel`, then un-gate the ✕ Cancel button from `UsesClaudeBrain`.

## Steps

### 1. Config — `src/LocalTranscriber.Shared/AgentConfig.cs`
- Add to `AgentConfig`: `public string LastClaudeProvider { get; set; } = AgentProviders.ClaudeCli;` (Claude side of the brain toggle).
- Update `RealtimeVoiceMode.Off` doc comment: off = typed text chat for the openai provider.
- No changes to `AgentProvider.cs`; `ConfigService.TrySet` is reflection-based, no key registration needed.

### 2. Options + session — `src/LocalTranscriber.Voice/RealtimeVoiceOptions.cs`, `RealtimeVoiceSession.cs`
- Add `TextOnlyOutput` derived property (D1) to `RealtimeVoiceOptions`.
- Ctor (~line 68): choose `NoOpAgentAudioOutput` when `TextOnlyOutput` too, not just `!SpeakReplies` — never open a playback device for a text session.
- `BuildSessionUpdate` (187–230):
  - `["output_modalities"] = _options.TextOnlyOutput ? new[]{"text"} : new[]{"audio"}`.
  - Build the `audio` value as a `Dictionary<string, object?>`; include the `output` (voice/format) block only when not text-only; keep `input` unconditionally (needed for pushToTalk input + transcription; harmless for Off).
- Instructions: when `TextOnlyOutput`, swap the "speak naturally" phrasing in `VoiceSystemPrompt` for "reply in concise text" (keep the rest).
- Implement `CancelTurn` (D4): guard to `Thinking`/`Speaking`, `_audio.Stop()`, send `{type:"response.cancel"}` via `RunGuardedAsync`, clear `_currentItemId`, `SetState(Ready)`. In `HandleServerEventAsync`'s `Error` case, filter the benign "no active response" race (skip raising `ErrorOccurred` for it).
- Grounding: **no changes** — initial silent item, tail loop, and grounding loop are all `conversation.item.create`/`input_text`, modality-independent.

### 3. Event mapper — `src/LocalTranscriber.Voice/RealtimeVoiceEventMapper.cs`
Add to the switch:
- `"response.output_text.delta"` → `AudioTranscriptDelta` with `delta` string.
- `"response.output_text.done"` → `AudioTranscriptDone` with `text` string.

### 4. Factory — `src/LocalTranscriber.Voice/RealtimeVoiceFactory.cs`
- Delete the `mode == Off` early return (lines 28–31). Off flows through: `Enabled` gate (self-healed by the VM at `AgentPanelViewModel.cs:567-571`), consent gate untouched (PTT/continuous only), key gate unchanged.
- `AgentConversationFactory` needs no change.

### 5. ViewModel — `src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs`
- Remove `CanConverse` (line 340); commands become `StartVoiceCommand: () => _voice is null`, `SendTextCommand: () => AgentEnabled`. Prune `RaiseCanExecuteChanged` calls that only served it.
- Remove the forced `off → hybrid` switch in `EnableAssistantCommand` (lines 90–97) — off is now first-class.
- `CanCancelTurn => IsThinking;` (line 231) — drop the `UsesClaudeBrain` gate; `CancelTurnCommand` already calls `_voice?.CancelTurn()`.
- Brain toggle:
  - `_lastClaudeProvider` init from `config.Agent.LastClaudeProvider` (or current `Provider` if it's already a claude flavor).
  - `public bool BrainIsRealtime { get => !UsesClaudeBrain; set => flip via SwitchBrainAsync(value); }`
  - `SwitchBrainAsync(bool realtime)`: capture `wasLive = _voice is not null`, stop/dispose the session (existing teardown), set `SelectedProvider = realtime ? "openai" : _lastClaudeProvider`, status "Brain switched — reconnects on your next message." if it was live.
  - In the `SelectedProvider` setter (line 342): when the value is a claude flavor, update `_lastClaudeProvider` and persist `agent.lastClaudeProvider`; always raise `OnPropertyChanged(nameof(BrainIsRealtime))` — keeps Settings dropdown and panel toggle in sync. (Settings dropdown keeps its current no-teardown behavior; teardown lives in `SwitchBrainAsync` only.)
- `ModeBadgeText` (line 487): `_ => "text chat"` instead of `"off"`.
- Add `public bool ShowTalkButton => SelectedVoiceMode != "off";`, raised from the voice-mode setter — hides the Hold button in text-only mode.

### 6. XAML — `src/LocalTranscriber.App/Views/MeetingView.xaml`, `SettingsView.xaml`
- **Header segmented toggle** (after the mode badge Border, ~line 469): two `RadioButton`s styled as a pill segment — "⚡ Realtime" bound to `AgentPanel.BrainIsRealtime` (TwoWay), "🧠 Claude" bound via the **existing** `InverseBool` converter (`MeetingView.xaml:12`, `InverseBooleanConverter` in `Converters/AppConverters.cs:67`). Add a small `SegmentToggleStyle` to view resources. Tooltips: "fastest replies" / "deeper, workspace-aware, slower".
- **Hold button** (~line 785): wrap visibility with `AgentPanel.ShowTalkButton` + `BoolToVisibility`. Input placeholder: "Type a message…" when voiceMode=off (DataTrigger).
- **SettingsView.xaml:205** hint: mention the in-panel Realtime/Claude toggle.

### 7. Tests — `tests/LocalTranscriber.Voice.Tests/` (reuse `FakeRealtimeTransport` harness)
- Update `Factory_OffAndConsentGates` (~line 345): off now expects the key notice (no secrets), plus a companion test that off + enabled + key returns a live session.
- Add:
  - `Off_SessionUpdate_UsesTextModality_NoVoice` — session.update has `"output_modalities":["text"]`, no `"voice":`.
  - `Off_SendUserText_ItemThenResponseCreate` — mirrors the hybrid typed-chat test.
  - `Off_PushToTalk_NoOps` — no `input_audio_buffer.*` sends, no recorder.
  - `OutputTextDelta_RaisesAssistantText` — `response.output_text.delta` → `AssistantTextAvailable`.
  - `Hybrid_SpeakRepliesFalse_UsesTextModality` / `Continuous_SpeakRepliesFalse_KeepsAudioModality` (locks D1).
  - `CancelTurn_WhenThinking_SendsResponseCancel_AndReturnsReady` / `CancelTurn_WhenReady_SendsNothing`.
  - `Off_Grounding_InitialTranscriptStillSeeded` — clones the existing grounding test with `Mode=Off`.
- Optional: `ConfigServiceTests` round-trip for `agent.lastClaudeProvider`.

## Edge cases

- **Mid-session brain flip**: teardown unsubscribes all six events and disposes; an in-flight streaming bubble just stops growing (finalized only on `ResponseCompleted`) — acceptable. Flipping while Claude is Thinking kills its child process (its existing cancel path).
- **Consent unchanged**: `SendAudio` gate only applies to pushToTalk/continuous; text-only and hybrid never prompt. Claude full-agent consent untouched.
- **Missing OpenAI key**: text-only start hits the same key gate/notice; the key-warning banner already keys off `UsesClaudeBrain` and stays correct.
- **Privacy posture unchanged**: text-only sends typed text + transcript grounding text only — same as hybrid; no audio leaves the machine.

## Verification

1. `dotnet test` — all suites green (currently 133+).
2. Manual via `./scripts/run-with-logs.ps1 -Target app` + `./scripts/tail-logs.ps1` (set `LT_REALTIME_DEBUG=1` for wire logs):
   - provider=openai, voiceMode=off: type in chat → session connects, streamed text reply, pill never shows Speaking, no audio; wire log shows `"output_modalities":["text"]` outbound and `response.output_text.delta` inbound.
   - Start recording, speak, ask "what was just said" → confirms grounding in text-only mode.
   - voiceMode=hybrid + SpeakReplies on → hold-to-talk + spoken replies (audio modality); SpeakReplies off → captions only, text modality in session.update.
   - pushToTalk/continuous smoke: unchanged, consent dialog still appears without `sendAudio`.
   - Toggle: mid-conversation flip Realtime→Claude → status message, next reply from Claude (log shows provider); flip back; restart app → Claude side remembers last flavor via `agent.lastClaudeProvider`.
   - Cancel: long question on realtime, ✕ while Thinking → reply stops, pill → Ready, no error toast.

## Critical files

- `src/LocalTranscriber.Voice/RealtimeVoiceSession.cs`
- `src/LocalTranscriber.Voice/RealtimeVoiceOptions.cs`
- `src/LocalTranscriber.Voice/RealtimeVoiceFactory.cs`
- `src/LocalTranscriber.Voice/RealtimeVoiceEventMapper.cs`
- `src/LocalTranscriber.Shared/AgentConfig.cs`
- `src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs`
- `src/LocalTranscriber.App/Views/MeetingView.xaml` (+ `SettingsView.xaml` hint)
- `tests/LocalTranscriber.Voice.Tests/RealtimeVoiceSessionTests.cs`
