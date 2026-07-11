# Risks

Known risks and constraints. Raise these when relevant.

## Cost

- **Realtime session cost doubled** ($4.63/hr Ed 6/8 → $7.94–$9.56/hr Jim 6/10, June 2026
  report). Root cause: the app swaps the Realtime session's tool set (and instructions)
  via `session.update` at every workout phase transition, invalidating the server-side
  prompt cache — the next response re-bills the whole accumulated conversation at full
  price (audio input $32/M vs cached $0.40/M, an 80x multiplier). Amplifiers: shorter
  sessions amortize fixed costs worse, ~63% larger base prompt, more responses/minute.
  NOT a model change, not a release regression (Ed's "cheap old version" actually ran
  near-identical code — his logs say 1.0.35).
- Any proposal to add tools mid-session, enlarge the system prompt, or increase response
  frequency has direct per-minute cost impact. Ask about cache implications.

## Realtime voice fragility

- History of subtle Realtime/WebRTC bugs (June 2026 fix wave): phantom `response.cancel`
  killing real speech responses, WebRTC disconnects needing diagnostics, tracking-mode AI
  referencing swapped-out exercises, user-audio prefix clipping, stale slot-cache
  validation, rep-logging clarification loops. New realtime features tend to surface
  protocol edge cases; budget for on-device debugging.
- OpenAI Realtime sessions cap at ~60 minutes — long workouts need a reconnect story.
- iOS mid-session Bluetooth detection is a known gap (Android-first stance).

## v2 / base-app rebuild risks

- **LiveKit audio-session ownership conflict**: if Ricky's transport lane adopts LiveKit
  *rooms*, LiveKit wants to own the audio session, colliding with our
  `autoConfigureAudioSession: false` + InCallManager ownership. Must be reconciled at
  handoff.
- Anuj's folder-structure refactor may relocate `src/voice/` — coordinate before merging
  audio-routing work.
- Base app Phase 7 (on-device validation) is blocked on: a real trainer JWT (no in-app
  login yet), a seeded booking owned by the trainer's spot, a server with a real
  OPENAI_API_KEY, and a device dev build.
- Dev-only `/api/v1/ai/spike-token` route (no auth) still exists on the server; it must
  be deleted along with `spike/` once the real transport is validated. Security debt if
  forgotten.

## Dev environment gotchas (recurring time sinks)

- Android package renamed `com.gymspots.aicoach` → `com.gymspots.aiassistant`; stale
  references launch an old binary → "can't find view manager" white screens.
- v2 app has no expo-dev-client; its debug build hardcodes Metro at device port 8081
  (`adb reverse tcp:8081 tcp:8082`, reset by every `expo run:android`).
- Two v2 test suites are timezone-dependent (pass only under America/Los_Angeles) —
  baseline noise, don't attribute to new changes.
- Gatsby `std::bad_alloc` on develop = corrupted `.cache`, fixed by `npm run clean`.

## Platform constraints

- Marketplace payments ride Stripe Connect — owner onboarding friction and webhook
  correctness are business-critical paths.
- Tablet app is landscape-only, owner-operated, in-gym: assume noisy environments,
  Bluetooth headsets, and unattended kiosk-style usage in design discussions.
