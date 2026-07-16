# Realtime implementation review — contextual gaps

## Context

Goal: review the realtime voice implementation and find **contextual gaps the LLM
might have** — i.e. cases where the model is asked to help in a live meeting without
the information it needs to be relevant. Scope confirmed with the user: focus on
**context quality**, not low-level robustness (crash/cost issues are listed in an
appendix but not planned). Also confirmed: the current `hybrid` provider using the
**workspace repo/CLAUDE.md as project context is intentional** — this plan does NOT
wire the `context/*.md` packs into the Claude brain.

The realtime code lives in `src/LocalTranscriber.Voice` (not `.Agent`). Two provider
paths assemble context very differently:

| Provider | Class | Project context | Live transcript to the LLM |
|---|---|---|---|
| `hybrid` *(active)* | `ClaudeCliConversation` + `RealtimeSpeaker` | workspace CLAUDE.md (by design) | last **10 lines**, `Name: text`, **no timestamp/source**, lossy read |
| `openai` | `RealtimeVoiceSession` | `context/*.md` packs, composed **once at connect** | 80 events / 5 min, `[time] Name (source): text`, re-injected every 15s |

The important asymmetry: the path you actually run (`hybrid`) has the **weakest
meeting-context feed**, and the path with the rich feed (`openai`) freezes its project
context at connect time.

---

## Contextual gaps (findings)

### Active path — `hybrid` / Claude-CLI brain

- **G1 — Lossy transcript read (highest impact).** `ClaudeCliConversation.ReadNewTranscriptEventsAsync`
  (`ClaudeCliConversation.cs:552-585`) reads only the **last `MaxTranscriptEvents` (=10) physical lines**
  of the JSONL file, *then* runs dedup. If more than 10 utterances land between two user
  turns, the earliest are **never read** — the brain silently never sees them. 10 lines is
  also often <1 minute of a busy meeting. There is already a correct byte-offset
  incremental tailer (`TranscriptEventTailer` in `LocalTranscriber.Agent`, used by the
  OpenAI path via `_tailerFactory`) that this path bypasses.
- **G2 — No timestamps, no source.** `BuildPromptAsync:540-549` renders `{DisplayName}: {text}`.
  The brain can't reason about timing ("what was said 5 min ago") or distinguish the
  **user's mic** from **meeting audio** (who's in the room vs the user). The OpenAI path
  already renders both (`RealtimeVoiceSession.RenderLines:645-646`) — the format just
  isn't shared.
- **G3 — No running summary for long meetings.** Once content scrolls past the small
  window it is gone; nothing preserves decisions/action-items from earlier in the call.
  `RollingTranscriptWindow.MeetingRunningSummary` (`RollingTranscriptWindow.cs:67-95`)
  exists for exactly this but is **dead code** — never populated by any provider.

### `openai` realtime path (inactive today, but part of "the realtime implementation")

- **G5 — Project context frozen at connect.** `ComposeContextAsync` (`RealtimeVoiceSession.cs:619-638`)
  runs **once**, with a retrieval query = the last 20 transcript lines *at connect time*.
  As the meeting drifts to new topics, newly-relevant `context/*.md` chunks are never
  retrieved or injected. The model's project context is static for the whole session.
- **G6 — Server-side conversation grows unbounded.** `GroundingLoopAsync` /
  `InjectPendingGroundingAsync` (`RealtimeVoiceSession.cs:554-588`) append a new
  `conversation.item` every 15s for the entire meeting and never delete. The *local*
  window is bounded, but the *server* context is not — over a long call this both costs
  more per turn and eventually risks truncation degrading the original instructions.

### Both paths

- **G4 — Speaker identity/role not surfaced.** Only `Speaker.DisplayName` reaches the LLM.
  The app has SQLite speaker memory (sherpa-onnx speaker ID). The model is told *that*
  someone spoke, not *who they are / their role* — so it can't tailor relevance to the
  participants. (Investigate what identity data is actually available before committing
  to a format; may be low-value if only a label exists.)

---

## Recommended changes

Ordered by value for the **active hybrid setup**. Keep changes minimal (KISS/YAGNI);
reuse existing utilities.

### P1 — Fix the hybrid meeting feed (G1 + G2) — highest value, low effort
- **Files:** `src/LocalTranscriber.Voice/ClaudeCliConversation.cs`
  (`BuildPromptAsync:527-550`, `ReadNewTranscriptEventsAsync:552-585`),
  `src/LocalTranscriber.Shared/AgentConfig.cs` (`ClaudeCliAgentConfig.MaxTranscriptEvents`).
- **G1:** Replace the last-N-lines read with the existing incremental
  `TranscriptEventTailer` (byte-offset + checkpoint + dedup) already used by the OpenAI
  path — so every utterance since the last turn is captured, not just the last 10. If a
  full tailer is too invasive, at minimum raise the read cap well above 10 and read from
  a persisted offset rather than the file tail.
- **G2:** Reuse the OpenAI path's line format. Extract `RealtimeVoiceSession.RenderLines`
  (`:640-649`) into a shared helper (e.g. in `LocalTranscriber.Shared`) and call it from
  both providers so the brain also gets `[HH:mm:ss] Name (user mic|meeting audio): text`.
- **Config:** Raise `MaxTranscriptEvents` default (currently 10) and/or add a
  window-minutes bound mirroring `RollingWindowMinutes`.

### P2 — Long-meeting running summary (G3)
- **Files:** `src/LocalTranscriber.Voice/ClaudeCliConversation.cs`,
  optionally `src/LocalTranscriber.Agent/RollingTranscriptWindow.cs` (`MeetingRunningSummary`).
- Maintain a rolling summary of content that has aged out of the window and prepend it
  (e.g. `[Meeting so far] …`). Simplest implementation: the Claude brain is already an
  LLM — periodically ask it (or a cheap turn) to fold older lines into the summary, or
  reuse the existing (currently unused) `MeetingRunningSummary` structure. Decide the
  cheapest mechanism during implementation; don't over-build.

### P3 — OpenAI-path context freshness (G5 + G6) — only matters if you switch to `openai`
- **File:** `src/LocalTranscriber.Voice/RealtimeVoiceSession.cs`.
- **G5:** Re-run `ComposeContextAsync` periodically (e.g. piggyback on `GroundingLoopAsync`
  every N intervals) with a query built from the *recent* window, and re-inject the
  refreshed pack as a grounding item — so later meeting topics pull in relevant chunks.
- **G6:** Cap server-side context: either send `conversation.item.delete` for aged items
  or periodically reset the session context, keeping the injected footprint bounded.

### P4 — Speaker identity (G4) — investigate first
- Confirm what identity/role data `Speaker` / the speaker DB actually exposes beyond
  `DisplayName`. If a role/description exists, include it once per newly-seen speaker in
  the rendered transcript. If only a label exists, skip — low value.

---

## Verification

- **Build/test:** `dotnet build` and `dotnet test` (baseline is 133 tests green).
- **G1/G2 (unit):** feed a JSONL with >10 utterances between two `SendUserTextAsync`
  calls; assert the built prompt contains all of them with `[time] Name (source):`
  formatting. Add a regression test that a burst larger than the cap is not silently
  dropped.
- **G2 shared render:** assert both providers produce identical line formatting from the
  same `TranscriptEvent[]`.
- **End-to-end (manual, hybrid):** `./scripts/run-with-logs.ps1 -Target app`, start a
  session with the mic, speak several utterances quickly, then address the assistant;
  confirm via `LT_REALTIME_DEBUG=1` / logs that the prompt sent to the brain includes the
  full recent window with timestamps and source labels.
- **G5/G6 (if implemented):** with `provider=openai` and `LT_REALTIME_DEBUG=1`, verify a
  refreshed context pack is re-injected mid-session and that aged grounding items are
  bounded.

---

## Appendix — correctness/robustness issues found (NOT planned per scope)

Surfaced during review; listed so they aren't lost. Per "context gaps only," these are
not part of this plan.

- **Malformed audio delta kills the whole session (HIGH).** `AgentAudioOutput.EnqueueBase64`
  (`AgentAudioOutput.cs:146`) calls `Convert.FromBase64String` with no try/catch; the
  `FormatException` propagates to `ReceiveLoopAsync` (`RealtimeVoiceSession.cs:419-423`),
  which sets `Faulted` and breaks the loop permanently. One bad frame ends the call.
- **Silent mid-session disconnect (MEDIUM).** `ReceiveLoopAsync:403-406` `break`s on socket
  close without setting `Faulted` or raising an error; no mid-session reconnect exists
  (retry is connect-time only). Zombie session until the next send throws.
- **Silent audio-device drop (MEDIUM/HIGH).** The known Bluetooth issue —
  `WasapiCaptureServiceBase.OnRecordingStopped` (`:109-113`) ignores `StoppedEventArgs.Exception`;
  in continuous mode the assistant goes silent with no signal.
- **No cost/timeout guards (MEDIUM).** `ResponseTimeout` (declared, `RealtimeVoiceOptions.cs:52`)
  is unused; no `max_output_tokens`, no session-duration cap; continuous mode has an
  echo/feedback runaway risk.
- **Auth failures retried as transient (MEDIUM).** `ConnectWithRetryAsync:164` retries a
  401 three times with backoff before surfacing a confusing message.
- **Leak on failed StartAsync (MEDIUM).** `AgentPanelViewModel.StartVoiceAsync` sets
  `_voice = null` on failure without `DisposeAsync`, leaking the `WaveOut` handle/CTS.
- Minor: `_groundedIds` grows unbounded; grounding-loop send failure can make `StopAsync`
  throw; `AudioTranscriptDone` mapped but unhandled.
