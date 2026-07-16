# Bug Report: Log Analysis (2026-07-16)

## Context

Reviewed all app logs (`output/logs/` + `tail-logs/`) from 2026-07-10 through today to identify
recurring errors. No crashes or null-reference exceptions found. Three real bugs surface repeatedly
in the voice subsystem.

---

## Bug 1 — Barge-in sends `response.cancel` with no active response

**File:** `src/LocalTranscriber.Voice/RealtimeVoiceSession.cs:498`  
**Symptom:** `[WARN] voice: Realtime error: Cancellation failed: no active response found`  
**Seen:** 2026-07-10 (repeated within a session)

**Root cause:** `OnUserSpeechStartedAsync` guards with:
```csharp
if (_currentItemId is null && !_audio.IsPlaying)
    return;
```
The `&&` is wrong. If the response already completed (`_currentItemId == null`) but a few ms of
buffered audio are still playing (`_audio.IsPlaying == true`), the guard passes and `response.cancel`
is sent — which the server rejects because the response is already done.

**Fix:** Guard on `_currentItemId` only. If there's no tracked response, stop audio and return:
```csharp
if (_currentItemId is null)
{
    _audio.Stop();
    return;
}
```

---

## Bug 2 — `input_audio_buffer.commit` fires with 0 ms of audio (mic unavailable)

**File:** `src/LocalTranscriber.Voice/RealtimeVoiceSession.cs:295-314` (PushToTalk path)  
**Symptom (repeating pattern across July 10, 14):**
```
[WARN] voice: Realtime error: Error committing input audio buffer: buffer too small.
               Expected at least 100ms of audio, but buffer only has 0.00ms of audio.
[WARN] voice: Voice turn failed: No microphone found.
```
**Root cause:** The existing guard checks `framesSent == 0 || bytesSent < MinAudioBytes`. When
`MicStreamPump.StartAsync` throws "No microphone found" mid-way, `_micStreamPump` is left non-null
(it's assigned before `StartAsync` is called in `StartMicStreamingAsync`). On the subsequent
PushToTalk-up, `TakeCounters()` on the broken pump can return stale non-zero counts from the
object initialisation, causing the guard to pass and the commit to fire against an empty
server-side buffer.

**Fix options (pick one):**
- Null out `_micStreamPump` in `StartMicStreamingAsync` if `StartAsync` throws, so `TakeCounters()`
  falls to the `?? (0, 0)` default.
- Add a `bool _pumpStarted` flag set only after successful `StartAsync`.

---

## Bug 3 — `response.create` races against an in-flight response (downstream of Bug 1)

**File:** `src/LocalTranscriber.Voice/RealtimeVoiceSession.cs:490`, `:313`, `:368`  
**Symptom:** `Conversation already has an active response in progress: resp_…`  
**Seen:** 2026-07-14 (multiple times per session)

**Root cause:** After Bug 1 causes a silent `response.cancel` failure, the original response is
still running server-side. When the next `response.create` fires (from function-call output,
typed text, or PushToTalk-up), the server rejects it.

**Fix:** Bug 1's fix removes the spurious cancel. Additionally, a client-side guard tracking
`_isResponseInFlight` (set on `response.create`, cleared on `response.done`) would prevent double
creates — but this is secondary to fixing Bug 1.

---

## Observation — SystemAudio capture stalls every ~30 s during idle

**Symptom:** Repeated "SystemAudio capture stalled — no audio for 30s; reconnecting" / reconnected
pairs when no system audio is playing. Usually self-heals. Once failed hard with
`0x8007001F` (device not functioning).

**Assessment:** WASAPI loopback goes silent when the device is idle and Windows power-manages
the audio device. The reconnect loop already handles this correctly. The one hard failure was
a driver-level transient. No code change recommended unless hard failures become frequent.

---

## No issues found on

- July 13 log: clean (no errors at all).
- `Unknown parameter: 'session.input_audio_transcription'` (July 14 early entries): already fixed
  in current code — transcription config now lives under `audio.input` dict, not session root.

---

## Verification

After fixing Bugs 1 and 2:

1. Start a voice session in PushToTalk mode with **no mic connected**.
2. Hold the talk button, release — should see "Voice turn failed: No microphone found" and
   **no** "buffer too small" server error.
3. In Continuous mode, let the model speak, then barge in mid-reply — should see no
   "Cancellation failed" warning.
