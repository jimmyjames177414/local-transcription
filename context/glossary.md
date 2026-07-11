# Glossary

Project-specific terms, codenames, and acronyms.

## Domain terms

- **Spot** — a listed private gym space (the marketplace unit). Owners create spots;
  clients book them.
- **Booking** — a paid reservation of a spot for a time slot; the anchor entity for
  AI coaching sessions (tokens, plans, and sessions are all keyed by booking).
- **Intake** — the client questionnaire (goal, experience, days/week, limitations);
  seeds the AI program builder.
- **Blueprint** — the workout attached to a booking; what the tablet coach follows.
- **AI Program page** (`/app/program`) — chat-left / plan-right builder in the client
  webapp; generates structured programs from intake.
- **Pre-brief** — server-composed personalized greeting block (name, memory, recent
  notes) injected into the Realtime prompt before the AI connects.
- **Tracking mode** — coach mode where the AI follows and logs the client's existing
  workout rather than driving a generated program; source of several June 2026 bugs.
- **Exploratory set** — a set used to find a working weight; had a target-persistence
  bug fix in June 2026.
- **"Learn from this session"** — post-workout button running LLM memory extraction
  over the transcript (idempotent, ≤5 facts, confidence ≥ 0.7).
- **Waiver** — liability document clients sign, handled via DocuSeal in the owner app.

## Apps & codenames

- **v1 / gs-aiassistant-nativeapp** — the production Expo tablet AI coach.
- **v2 / v2-ai-assistant-nativeapp** — team rebuild (package
  `com.gymspots.aiassistant.v2`, scheme `gstrainer-v2://`); lanes: Jim audio, Ricky
  transport, Anuj structure.
- **Base app / base-gs-aiassistant-nativeapp** — Jim's from-scratch AI trainer session
  screen, ADR-driven, on branch `feat/ai-trainer-session-screen`.
- **ADR-001** — the approved architecture decision record for the base app session
  screen (XState, direct WebRTC, OpenAI, persistence stubbed).
- **Prompt 1 / Prompt 2** — the multi-prompt workflow for the base app: Prompt 1 = ADR,
  Prompt 2 = implementation (phases 0–7).
- **Spike / spike-token** — the Phase 0 WebRTC proof; `GET /api/v1/ai/spike-token` is a
  dev-only unauthenticated token route slated for deletion after Phase 7.
- **gs-ai-personaltrainer-prototype**, **gs-owner-aiassistant-nativeapp** — earlier
  prototypes in the workspace; reference only.

## Technical terms

- **Realtime API** — OpenAI's speech-to-speech API; the coach connects via WebRTC using
  model `gpt-realtime` (~60-min cap).
- **Ephemeral token / clientSecret** — short-lived Realtime credential minted by the
  server at `GET /api/v1/ai/token/:bookingId`; requires a trainer/owner JWT.
- **X-ACCESS-TOKEN** — the header carrying the JWT (not Authorization: Bearer).
- **bookingSpotOwner / spotOwner / bookingOwner** — server ownership middleware.
- **VAD** — voice activity detection; threshold raised 0.75→0.95 while the AI speaks to
  enable barge-in (client interrupting the coach).
- **Barge-in** — user speech cancels the assistant's in-flight audio response.
- **session.update** — Realtime message that rewrites instructions/tools; swapping it
  mid-session invalidates the prompt cache (the cost-doubling root cause).
- **Provider factory** — `services/ai/providerFactory.js`, abstracts OpenAI vs Azure.
- **PostGIS** — PostgreSQL geospatial extension powering map/proximity spot search.
- **Stripe Connect** — marketplace payments: owner onboarding + client checkout +
  webhooks at `/api/v1/webhook/stripe`.
- **Contentful** — headless CMS for marketing pages on both Gatsby webapps.
- **CNG / prebuild** — Expo Continuous Native Generation; `android/` is gitignored, so
  native config (permissions etc.) must go in `app.config.ts`.
- **expo-dev-client** — Expo's custom dev launcher; v2 lacks it, hence the Metro
  port-8081 reverse-tether gotcha.
- **InCallManager** — the native audio-routing library (speaker/Bluetooth/wired) chosen
  over LiveKit AudioSession for v2.
- **Runbook** — `./runbook/*.sh` scripts to start/stop/tail the local stack
  (`debug.sh web|coach|all`, `ai-debug.sh` for coach-focused logs).
- **client_coach_memory** — table of durable per-client facts the adaptive coach reads
  and writes via tools (rememberAboutClient / forgetAboutClient).
