# People

Who is who on GymSpots: names, roles, what they own.

- **Jim Miller** (jim@gymspots.com) — founder / lead engineer. Owns the server, both
  webapps, the v1 AI coach app, and the from-scratch "base app" AI trainer session
  screen (ADR-001). In the v2 rebuild his lane is Bluetooth/audio routing. Note: Jim
  also has a separate consulting day job (Avanade/Accenture, team: Rajeev, Martha,
  Rigo, etc.) — names from that world are NOT GymSpots people.
- **Ricky** — v2 rebuild lane: WebRTC/LiveKit transport + echo cancellation. Key handoff
  risk: if he uses LiveKit rooms, LiveKit's AudioSession conflicts with Jim's
  InCallManager audio ownership.
- **Anuj** — v2 rebuild lane: refactoring folder structure to Expo conventions; may
  relocate `src/voice/`.
- **Ed** — runs real coaching sessions and reports usage/cost (his 6/8 session was the
  cheap baseline in the token-cost analysis; he believed he was on an older app version
  but logs showed 1.0.35). Treat him as a field tester / stakeholder voice on cost and
  session quality.
- **CEO** — set the AI program requirements (April 2026): intake-driven program
  generation, structured plans the tablet coach follows exactly, auto-attach to the
  next booking, iterate via chat. "What the CEO asked for" framing recurs in feature
  docs.
