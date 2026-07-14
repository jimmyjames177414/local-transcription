# Experimental Meeting Join Mode — Analysis

Status: **documentation only, deliberately not implemented**. Config scaffold exists (`agent.meetingParticipant`, disabled) and the UI marks the mode experimental/unavailable.

## Options compared

### 1. Private sidecar only (what LocalTranscriber does today)
- Feasibility: done. Reliability: high. Compliance risk: none — nothing enters the meeting.
- UX: user reads/hears private suggestions and speaks for themselves.
- **Recommendation: this is the right default. Everything below adds risk for marginal value.**

### 2. Virtual microphone output (agent voice routed into the mic the meeting app uses)
- Feasibility: medium — needs a virtual audio device (VB-Cable etc.) and careful routing so the agent's voice mixes with (or replaces) the user's mic.
- Reliability: fragile; device juggling confuses meeting apps and users.
- Compliance risk: HIGH — an AI speaking through what appears to be the user's voice channel without per-meeting disclosure is impersonation-adjacent. Workplace policy problems are near-certain.
- Verdict: do not build without explicit per-meeting user action and disclosure.

### 3. Separate meeting account for the agent (agent joins as its own participant)
- Feasibility: medium — needs an account, a headless client or browser session, and audio bridging.
- Reliability: medium; sign-in flows and CAPTCHAs break automation.
- Compliance: better than (2) — the agent is visibly a participant — but many orgs restrict bot accounts.
- Verdict: acceptable in principle, expensive to build, platform-fragile.

### 4. Official platform bot/app integration (Teams bots, Zoom Apps, Meet add-ons)
- Feasibility: high effort, but the only path platforms actually support (real APIs for joining, speaking, captions).
- Reliability: highest of the join options. Compliance: cleanest — visible bot identity, consent surfaces, org admin control.
- Verdict: **if join mode is ever built, build it this way**, per platform, starting with Teams (this user's environment).

### 5. Browser automation (puppeteer-style joining of the web client)
- Feasibility: technically possible. Reliability: awful — DOM churn breaks it weekly.
- Compliance: violates most platforms' terms of service.
- Verdict: rejected.

## Safety rules (non-negotiable, already enforced by config design)
- No hidden meeting join; `requiresExplicitUserAction` stays true.
- No hidden audio injection; nothing routes to any virtual mic by default.
- No impersonation of the user.
- Explicit enablement + visible warning before any future implementation.
