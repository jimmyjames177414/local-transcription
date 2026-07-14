# Bluetooth mic "disappears" after one voice exchange — diagnosis + hardening

## Context

Starting an AI voice session lets the user say **one** thing, the AI replies, and then the
Bluetooth headset microphone "disappears" — every subsequent turn fails and no mic is available
for the rest of the app's lifetime. This plan captures the confirmed root cause (with log/online
evidence) and a focused set of code-hardening changes so the app survives Bluetooth profile
switches instead of dying after the first turn.

## Root cause (confirmed)

**Bluetooth A2DP ↔ HFP/HSP profile switching tears down the headset's mic endpoint, and the app
has no recovery.**

A Bluetooth headset can either be in **A2DP** (high-quality stereo *output only*, no mic) or
**HFP/HSP** (mono, bidirectional — the *only* profile that exposes a microphone). It cannot do
high-quality output and mic capture at the same time. In this app one voice turn does both:

1. **User turn** — the session opens the headset's mic (`WasapiCapture`), forcing Windows to
   switch the headset from A2DP to HFP. This works: the user says one thing.
   - Hybrid mode: `MicrophonePushToTalkRecorder` (via `RealtimeVoiceSession.OnPushToTalkDownAsync`,
     `src/LocalTranscriber.Voice/RealtimeVoiceSession.cs:191`).
   - pushToTalk/continuous: `ResamplingAgentMicStream` →
     `MicrophoneCaptureService` → `new WasapiCapture(device)`
     (`src/LocalTranscriber.Audio/WasapiCaptureServiceBase.cs:160`).
2. **AI reply** — the reply is **audio** (`output_modalities = ["audio"]`,
   `RealtimeVoiceSession.cs:158`) and is played through `NAudioAgentAudioOutput` using a bare
   `new WaveOutEvent()` on the **default playback device**
   (`src/LocalTranscriber.Audio/AgentAudioOutput.cs:70`) — which is the same Bluetooth headset.
   Windows now wants A2DP for good output while the mic wanted HFP → the profile **flaps**.
3. On the user's headset/driver the flap **drops the HFP mic endpoint entirely**. The recording
   device vanishes from Windows.
4. **Next turn** finds no capture endpoint. `WasapiCapture` throws `COMException`, surfaced as
   `"No microphone found…"` (`WasapiCaptureServiceBase.cs:165`). From then on the engine logs
   `"Microphone unavailable (no default recording device); skipping mic capture."`
   (`src/LocalTranscriber.Engine/RealTranscriptionEngine.cs:145`) for every session — the mic is
   gone until the headset is re-connected.

The app has **no `IMMNotificationClient` / device-change handling anywhere** (repo-wide grep is
empty), so it never notices the endpoint returning and never recovers. `OutputAudioDeviceId`
exists in config and options (`AgentConfig.cs:65`, `RealtimeVoiceOptions.cs:36`, passed through in
`RealtimeVoiceFactory.cs:56`) but is **dead** — `NAudioAgentAudioOutput` never receives it, so the
user cannot route the reply away from the headset to break the flap.

### Log evidence (`output/logs/localtranscriber-20260710.log`)

```
17:42:07 [WARN] voice: Voice turn failed: No microphone found. Connect a microphone ...
17:48:26 [WARN] engine: Microphone unavailable (no default recording device); skipping mic capture.
   ... repeats for every subsequent session ...
```

The failing message comes from `RunGuardedAsync` (`RealtimeVoiceSession.cs:582`), which wraps the
**push-to-talk / hybrid** path — i.e. the real session was *not* continuous mode, and the mic is
physically gone at the OS layer (a real `COMException`), not merely being ignored by the server.

### Rejected alternative

A second investigation proposed a Realtime-protocol bug (continuous mode's `create_response:true`
firing only once, so later turns are dropped server-side). **Rejected:** the failing code path is
the push-to-talk/hybrid handler, not continuous mode, and the logs show an OS-level
`No microphone found` COMException — the device is genuinely gone, not silently ignored. The
existing in-code comment (`RealtimeVoiceSession.cs:593`) and the device-picker doc comment
(`AgentPanelViewModel.cs:62`) already name the Bluetooth profile switch as the culprit.

## Online corroboration

This is a well-known, hardware/OS-level Bluetooth limitation, independent of this app:

- A2DP (stereo, no mic) vs HFP/HSP (mono, mic) is a fundamental Bluetooth constraint; opening the
  mic forces the low-quality profile and, on many drivers, drops the device.
  ([Windows Forum](https://windowsforum.com/threads/windows-10-bluetooth-headset-audio-a2dp-vs-hfp-fixes-tradeoffs.401263/),
  [ittrip](https://en.ittrip.xyz/windows11/win11-bt-mode-switch))
- Microsoft Q&A and mictest.pro document the Bluetooth mic endpoint becoming unavailable /
  "disconnected" when the profile switches or an app grabs the headset mic.
  ([Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/5876737/bluetooth-mic-is-disconnected),
  [mictest.pro](https://www.mictest.pro/resolving-microphone-detection-problems/))
- "No audio after Bluetooth profile switch from A2DP to HSF/HFP and back — only reconnect fixes
  it." ([bluez #1142](https://github.com/bluez/bluez/issues/1142)) — same failure mode on Linux.
- Recommended fix everywhere: give the voice app a **separate mic** (USB/built-in) so the headset
  stays in A2DP; rapid flapping is caused by an app repeatedly opening/closing the headset mic.
  ([gist](https://gist.github.com/OndraZizka/2724d353f695dacd73a50883dfdf0fc6))
- NAudio itself has open issues about `WasapiCapture` throwing/becoming unusable when a device is
  removed mid-capture, with no built-in recovery. ([NAudio #772](https://github.com/naudio/NAudio/issues/772))

## The fix (code hardening)

Goal: never silently die after one turn. Let the user keep the headset in A2DP (route reply
output off the headset), and recover automatically when the capture endpoint comes back.

### 1. Wire the dead `OutputAudioDeviceId` through to playback

- `src/LocalTranscriber.Audio/AgentAudioOutput.cs`: give `NAudioAgentAudioOutput` an optional
  `string? outputDeviceId` constructor param. When non-null, resolve the `MMDevice` via
  `MMDeviceEnumerator.GetDevice(id)` and play through `WasapiOut(device, AudioClientShareMode.Shared, …)`
  instead of `new WaveOutEvent()`; null keeps default-device behaviour. `WasapiOut` implements
  `IWavePosition.GetPosition()`, so `PlayedMilliseconds`/`Flush` barge-in math is preserved.
  Fall back to the default device (and log a warning) if the id no longer resolves.
- `src/LocalTranscriber.Voice/RealtimeVoiceSession.cs:68`: pass
  `_options.OutputAudioDeviceId` into the `NAudioAgentAudioOutput` default factory.
- This lets the user set output to laptop speakers / a USB DAC so the reply never pulls the
  headset back to A2DP — breaking the flap at the source.

### 2. Detect the lost capture endpoint and recover (`IMMNotificationClient`)

- Add a small `AudioEndpointWatcher : IMMNotificationClient` in
  `src/LocalTranscriber.Audio` that wraps `MMDeviceEnumerator.RegisterEndpointNotificationCallback`
  and raises events on `OnDefaultDeviceChanged` / `OnDeviceStateChanged` for `DataFlow.Capture`.
  (Mirror the existing enumerator usage in `AudioDeviceService.cs` / `WasapiCaptureServiceBase.cs`.)
- In `RealtimeVoiceSession`, subscribe while a mic-streaming mode is active. On "capture endpoint
  returned," if the session is in a mic mode and streaming has stopped due to a prior failure,
  re-run `StartMicStreamingAsync` (guarded, idempotent — it already no-ops when `_micStream` is
  non-null, `RealtimeVoiceSession.cs:278`). This turns "dead for the session" into
  "recovers when Windows re-exposes the mic."
- Also make `MicSendPumpAsync` / capture failures surface a state transition (not just a silent
  `AppLog.Warn`) so the UI reflects "mic lost — retrying / pick another mic."

### 3. Text-only reply fallback (no TTS playback)

- Add a config flag (e.g. `RealtimeAgentConfig.SpeakReplies`, default true) + option on
  `RealtimeVoiceOptions`. When false, `RealtimeVoiceSession` uses `NoOpAgentAudioOutput`
  (already exists, `AgentAudioOutput.cs:33`) and relies on the existing caption stream
  (`AssistantTextAvailable`, driven by `AudioTranscriptDelta`, `RealtimeVoiceSession.cs:377`).
  With no audio played back, a Bluetooth mic never gets yanked to A2DP by playback → the flap
  can't happen even when the headset is the mic. This is the safest mode for headset users.

### 4. UI guidance + output picker

- `src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs`: add an **output device** picker
  (`OutputDevices` collection + `SelectedOutputDeviceId`, persisting
  `c.Agent.Realtime.OutputAudioDeviceId`), populated via the existing
  `AudioDeviceService.ListOutputDevices()` (`AudioDeviceService.cs:9`). Add a "Speak replies"
  toggle bound to the flag from step 3. Update `MainWindow.xaml` accordingly.
- Keep the existing helpful `FriendlyError` text (`RealtimeVoiceSession.cs:593`); optionally point
  it at the new "Speak replies off" and output-device options.

## Files to modify

- `src/LocalTranscriber.Audio/AgentAudioOutput.cs` — output device selection (WasapiOut).
- `src/LocalTranscriber.Audio/` — new `AudioEndpointWatcher` (IMMNotificationClient).
- `src/LocalTranscriber.Voice/RealtimeVoiceSession.cs` — pass output device, subscribe to
  endpoint watcher + recover, honor Speak-replies flag, surface mic-lost state.
- `src/LocalTranscriber.Voice/RealtimeVoiceOptions.cs` — add `SpeakReplies` (and any new option).
- `src/LocalTranscriber.Voice/RealtimeVoiceFactory.cs` — map new config → options.
- `src/LocalTranscriber.Shared/AgentConfig.cs` — add `SpeakReplies` to `RealtimeAgentConfig`.
- `src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs` + `MainWindow.xaml` — output picker,
  Speak-replies toggle.
- Tests under `tests/` mirroring existing voice/audio tests.

## Verification

- **Unit/integration:** `dotnet test`. Add tests: `NAudioAgentAudioOutput` honors a supplied
  device id (and falls back on an unknown id); `RealtimeVoiceSession` uses `NoOpAgentAudioOutput`
  when `SpeakReplies=false`; endpoint-watcher recovery re-invokes `StartMicStreamingAsync`
  (via injected fakes, following the existing `Func<IAgentMicStream>` seam at
  `RealtimeVoiceSession.cs:73`).
- **Manual, real hardware (the only way to prove the BT fix):** with the Bluetooth headset as the
  Voice mic, `./scripts/run-app.ps1`, start a voice session, do **three** back-to-back turns:
  1. Baseline (repro): confirm turn 2 fails today.
  2. Set output device to laptop speakers → confirm multiple turns now work (headset stays A2DP).
  3. Toggle "Speak replies" off with headset as mic → confirm multiple turns work with captions.
  Watch with `./scripts/tail-logs.ps1 -Follow -Timeout 5`; success = no
  `No microphone found` / `no default recording device` after turn 1.
- **Recommended user workaround (works today, no build):** in the Agent tab set **Voice mic** to a
  USB/built-in mic and leave the headset as default output — the headset stays in A2DP and the mic
  is never opened, so the flap never happens.

## Notes / risks

- The underlying A2DP/HFP limitation is a Windows/Bluetooth constraint we cannot remove; the fixes
  make the app *avoid* and *survive* it, they don't change Bluetooth.
- `WasapiOut` vs `WaveOutEvent`: keep the default path on the current class if a null id must
  behave exactly as before; only switch to `WasapiOut` when an explicit id is chosen (lower risk).
- Recovery is best-effort — some drivers require a physical reconnect (per bluez #1142); in that
  case the UI should say so rather than appear hung.
