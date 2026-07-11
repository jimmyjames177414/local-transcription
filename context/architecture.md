# Architecture

Technical notes on the GymSpots system.

## System shape

Three frontends + one tablet app, all over HTTPS to a single Express.js API (port 8080
dev): gs-client-webapp (Gatsby, port 8000), gs-owner-webapp (Gatsby, port 8001),
gs-aiassistant-nativeapp (Expo/React Native, landscape tablet). The server talks to
PostgreSQL+PostGIS, Redis (cache), and external services: Stripe, AWS S3/SES/SNS,
OpenAI/Azure OpenAI, Google (Maps/Calendar), Contentful. Request flow: app → Express →
Controller → Service → Model/DB. CORS restricts origins to the two webapp URLs.

## Server (gymspots-server)

Express.js REST API under `/api/v1/*`: user (JWT auth + bcrypt), spots (PostGIS
proximity queries), bookings (lifecycle + cron cleanup of expired ones), ai (provider
factory, Realtime token minting), webhook (Stripe, raw-body + signature verification),
plus waivers, calendar sync, S3 image upload, SES email. Redis caching. Jest+Supertest
tests.

## Realtime voice pipeline (the interesting part)

Tablet fetches an ephemeral secret from `GET /api/v1/ai/token/:bookingId` (trainer JWT,
`X-ACCESS-TOKEN` header, `bookingSpotOwner` middleware), then opens a direct WebRTC
connection to OpenAI Realtime (`gpt-realtime`). Function tools drive exercise tracking:
set logging, navigation, rest-timer control, plus adaptive-coach tools (remember/forget
client facts, exercise notes, suggest/swap/cut exercises). Protocol discipline:
`create_response: false`, explicit `response.create` per finalized user turn; tool
results are sent as function_call_output + a context delta, then one response.create.
VAD threshold toggles 0.75↔0.95 to implement barge-in. Transcripts are batched (5 s
flush) to `workout_session_transcript`.

## Adaptive coach data model (migration V18, April 2026)

`client_coach_memory` (durable facts: injury/preference/goal/equipment/other),
`exercise_alternative` (~55-row swap catalog), `workout_session_transcript`
(append-only, deduped), `workout_session.extractor_run_at` (idempotency for post-session
memory extraction), `session_exercise_change` (audit for swaps/cuts).

## Base app (AI trainer session screen) internals

Branch `feat/ai-trainer-session-screen`. Layers: `src/domain` (pure types/helpers/rest
math) → `src/machine/sessionMachine.ts` (XState; phase lives in state NODES, so
consumers must derive phase from `snapshot.value` via `toSessionState`, never trust
`context.phase`) → `src/voice/transport` (WebRTC + token client + renewal backoff;
public surface is the index barrel only) → `src/voice/ai` (system prompt, state-summary
and event deltas, tool defs, pure tool bridge) → `src/screen` (SessionProvider wiring,
components: progress rail, active-set panel, SVG rest-timer ring, connection overlay) →
`src/integration` (persistence/recording stubs). 116 pure tests run via Node 22
strip-types, no build step. Phases 1–5 committed (7aef7a5); Phase 7 = on-device
validation, pending credentials.

## Cross-cutting conventions

- JWT auth everywhere; refresh rotation with 401 axios interceptors in all apps.
- Ownership middleware on every spot/booking route.
- AI provider factory abstracts OpenAI vs Azure behind one interface.
- Gatsby webapps render Contentful-driven marketing pages via `{Page.slug}.js`.
- React context providers pattern in all apps (AuthContext, SessionDataContext,
  VoiceConnectionContext).
- Local dev via runbook scripts: `./runbook/debug.sh web|coach|all`,
  `./runbook/ai-debug.sh` for filtered WebRTC/cost/audio logs.
