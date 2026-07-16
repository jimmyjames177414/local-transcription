# Realtime context-gaps — TODO

Fixing contextual gaps in the realtime voice implementation (`src/LocalTranscriber.Voice`).
Scope: **context quality only** (robustness/cost issues are noted at the bottom, not planned).
Confirmed: hybrid using the workspace repo/CLAUDE.md as project context is **intentional** —
do NOT wire `context/*.md` packs into the Claude brain.

## P1 — Fix the hybrid meeting feed (highest value, low effort)
Files: `ClaudeCliConversation.cs`, `src/LocalTranscriber.Shared/AgentConfig.cs`

- [ ] **G1 — lossy read.** `ReadNewTranscriptEventsAsync` (`ClaudeCliConversation.cs:552-585`)
      reads only the last `MaxTranscriptEvents` (=10) physical lines *then* dedups, so a burst
      of >10 utterances between turns is never read. Replace with the incremental
      `TranscriptEventTailer` (byte-offset + checkpoint + dedup) already used by the OpenAI path,
      or at minimum read from a persisted offset instead of the file tail.
- [ ] **G2 — no timestamps/source.** `BuildPromptAsync:540-549` renders `{DisplayName}: {text}`.
      Extract `RealtimeVoiceSession.RenderLines` (`:640-649`) into a shared helper and use it in
      both providers so the brain also gets `[HH:mm:ss] Name (user mic|meeting audio): text`.
- [ ] **Config.** Raise `ClaudeCliAgentConfig.MaxTranscriptEvents` default (currently 10) and/or
      add a window-minutes bound mirroring `RollingWindowMinutes`.
- [ ] **Tests.** JSONL with >10 utterances between two `SendUserTextAsync` calls → prompt contains
      all of them, formatted with time + source; regression test that a burst > cap isn't dropped;
      assert both providers render identical lines from the same `TranscriptEvent[]`.

## P2 — Long-meeting running summary (G3)
Files: `ClaudeCliConversation.cs`, optionally `RollingTranscriptWindow.cs` (`MeetingRunningSummary:67-95`, dead code)

- [ ] Maintain a rolling summary of content aged out of the window; prepend as `[Meeting so far] …`.
      Cheapest mechanism (brain self-folds older lines, or reuse `MeetingRunningSummary`) — decide
      during implementation, don't over-build.

## P3 — OpenAI-path context freshness (only matters if switched to `openai`)
File: `RealtimeVoiceSession.cs`

- [ ] **G5 — frozen context.** `ComposeContextAsync` (`:619-638`) runs once at connect. Re-run
      periodically (piggyback `GroundingLoopAsync`) with a query from the recent window and
      re-inject the refreshed pack.
- [ ] **G6 — unbounded server context.** `GroundingLoopAsync`/`InjectPendingGroundingAsync`
      (`:554-588`) append forever. Cap server-side context via `conversation.item.delete` for
      aged items or periodic session-context reset.

## P4 — Speaker identity (G4) — investigate first
- [ ] Confirm what identity/role data `Speaker`/the speaker DB exposes beyond `DisplayName`.
      If a role/description exists, include it once per newly-seen speaker; if only a label, skip.

## Verification
- [ ] `dotnet build` && `dotnet test` (baseline 133 green).
- [ ] Manual (hybrid): `./scripts/run-with-logs.ps1 -Target app`, speak several utterances quickly,
      then address the assistant; with `LT_REALTIME_DEBUG=1` confirm the brain prompt includes the
      full recent window with timestamps + source.

---

## Appendix — robustness/cost issues found (NOT in scope, listed so they aren't lost)
- [ ] Malformed audio delta kills the session (HIGH) — `AgentAudioOutput.cs:146`
      `Convert.FromBase64String` no try/catch → `ReceiveLoopAsync:419-423` faults permanently.
- [ ] Silent mid-session disconnect (MED) — `ReceiveLoopAsync:403-406` breaks with no error/reconnect.
- [ ] Silent audio-device drop (MED/HIGH) — `WasapiCaptureServiceBase.cs:109-113` ignores
      `StoppedEventArgs.Exception` (the Bluetooth issue).
- [ ] No cost/timeout guards (MED) — `ResponseTimeout` unused; no `max_output_tokens`; continuous
      echo/feedback runaway risk.
- [ ] Auth 401 retried as transient (MED) — `ConnectWithRetryAsync:164`.
- [ ] Leak on failed StartAsync (MED) — `AgentPanelViewModel.StartVoiceAsync` nulls `_voice` without
      `DisposeAsync`.
- [ ] Minor: `_groundedIds` unbounded; grounding-loop send failure can make `StopAsync` throw;
      `AudioTranscriptDone` mapped but unhandled.
