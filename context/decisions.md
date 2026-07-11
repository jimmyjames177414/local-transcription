# Decisions

Standing decisions already made. Flag meeting talk that contradicts these.

## Platform & architecture

- One Express.js API serves all frontends. Server follows strict Controller → Service →
  Model layering. No frontend talks to the database or third-party services directly.
- Every protected route uses the `authorization` middleware (JWT). Resource-level access
  goes through ownership middleware: `spotOwner`, `bookingOwner`, `bookingSpotOwner`.
  Never bypass these; never skip ownership checks on spot/booking CRUD.
- Tokens are sent via the `X-ACCESS-TOKEN` header (not `Authorization: Bearer`). Webapps
  keep tokens in memory (never localStorage); the native app uses Expo SecureStore.
  Refresh-token rotation with a 401 axios interceptor everywhere.
- AI calls go through the provider factory (`services/ai/providerFactory.js`, OpenAI +
  Azure OpenAI). Never call a provider SDK directly from controllers.
- Stripe webhooks use `express.raw()` body parsing and mandatory signature verification.
- No hardcoded API URLs — env vars (`GATSBY_API_BASE_URL`, `API_BASE_URL`).

## Realtime voice coaching

- Durable OpenAI API keys live server-side only. The tablet gets a short-lived ephemeral
  secret from `GET /api/v1/ai/token/:bookingId` (requires a trainer/owner JWT — the
  `bookingSpotOwner` middleware rejects client JWTs with 403; matches the gym-tablet
  deployment where the owner's device coaches the client).
- Realtime protocol: `create_response: false` with explicit `response.create` after
  finalized user turns; tool-call flow is function_call_output → context delta (no
  response.create) → exactly one response.create. VAD threshold raised (0.75 → 0.95)
  while the assistant speaks, restored after — this implements barge-in.
- Model: `gpt-realtime` (OpenAI). ~60-minute session cap accepted.

## AI program generation (April 2026, CEO-driven)

- Programs are generated from the client's intake answers via chat, as structured data
  (workouts → exercises → sets → weekly schedule), persisted server-side.
- **Catalog enforcement**: the AI must pick exercises from a filtered catalog; one silent
  retry on hallucination, then off-catalog items are dropped before persistence. The
  tablet coach must never receive an exercise the platform doesn't know.
- New/rebuilt plans auto-attach their first workout to the user's next eligible booking;
  new bookings pick up the latest plan automatically. No manual attach step.

## Adaptive coach memory (April 2026)

- Client memory is durable and tool-gated: `rememberAboutClient` / `forgetAboutClient`
  write to `client_coach_memory`; exercise swaps/cuts audit to `session_exercise_change`.
- Post-session memory extraction ("Learn from this session") is idempotent per session,
  confidence ≥ 0.7, max 5 facts, deduped.

## v2 rebuild (July 2026)

- **Audio routing = InCallManager, cleanly ported from v1 — NOT LiveKit AudioSession**
  (decided 2026-07-04). LiveKit's RN AudioSession exposes no JS device-change events,
  which the BT-reconnect re-route and near/far-field handling require. `@livekit/react-native`
  is used only for WebRTC primitives with `autoConfigureAudioSession: false`.
- Shell-first architecture review (2026-07-03): workout session stays a dummy page while
  lanes land; the BT verification harness lives on a standalone dev-only screen, never
  wired into the workout UI, and gets deleted after verification.
- Android permissions live in `app.config.ts` (`android/` is gitignored CNG prebuild) —
  never edit the manifest directly.

## Base app / AI trainer session screen (ADR-001, approved 2026-06-30)

- Built from scratch; the v1 app is reference-only, no code reuse.
- State: **XState only** (Zustand documented as fallback in the ADR, never built).
- Transport: direct react-native-webrtc (actually `@livekit/react-native-webrtc` binding)
  to OpenAI Realtime; LiveKit Agents/SFU evaluated and rejected.
- Persistence stubbed for Prompt 2 (results surfaced via callbacks, no server writes).
- Set logging is **voice-only** — no tap fallback.
- Session recording is design-only (record-ready audio layer, recording out of scope).
- Cleanup gate: after on-device validation, delete `spike/` and the dev-only
  `/api/v1/ai/spike-token` route from the server.
