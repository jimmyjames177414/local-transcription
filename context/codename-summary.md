# Project Summary — GymSpots

GymSpots is a fitness-space marketplace plus an AI voice coach. Gym owners list private
gym spaces; clients discover them on a map, book sessions, and pay via Stripe Connect.
An in-gym tablet app runs a real-time AI voice coach (OpenAI Realtime API over WebRTC)
that guides the client through the booked workout, logs sets by voice, adapts the plan
mid-session, and remembers the client across sessions.

Four apps, one API:
- **gymspots-server** — Express.js REST API (Node.js, PostgreSQL+PostGIS, Redis, Stripe
  Connect, AWS S3/SES/SNS, AI provider factory for OpenAI + Azure OpenAI).
- **gs-owner-webapp** — Gatsby 5 owner dashboard (port 8001): spot creation wizard,
  scheduling, bookings, waivers via DocuSeal, Stripe onboarding, Google Calendar sync.
- **gs-client-webapp** — Gatsby 5 client app (port 8000): map discovery, booking + payment,
  reviews, intake forms, AI program builder chat.
- **gs-aiassistant-nativeapp** — Expo/React Native tablet app (landscape), the production
  v1 AI voice coach.

Current phase (July 2026): the AI coach is the center of gravity. Shipped this year:
adaptive coach with durable client memory (April), AI-generated workout programs from
intake with auto-attach to the next booking (April, CEO-driven), a long tail of realtime
voice bug fixes (June). Two rebuild efforts are active: **v2-ai-assistant-nativeapp**
(team rebuild with lane ownership: Jim = Bluetooth/audio routing, Ricky = WebRTC/LiveKit
transport, Anuj = folder structure) and Jim's from-scratch **AI trainer session screen**
("base app", branch `feat/ai-trainer-session-screen`) built via an approved ADR:
XState state machine, direct react-native-webrtc to OpenAI, phases 1–6 done and tested
(116/116), on-device validation (Phase 7) pending credentials.

Top business concern: realtime session cost roughly doubled ($4.63/hr → $9.56/hr, June
analysis) — root cause is prompt-cache invalidation from swapping tools via
`session.update` at workout phase transitions, not a model or release change.

Jim Miller (jim@gymspots.com) is founder/lead engineer; he also has a separate day job
(Avanade/Accenture) — GymSpots meetings are about this project, not that one.
