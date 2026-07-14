# Prompt for Claude Design — LocalTranscriber UI redesign

Copy everything below the line into Claude Design.

---

Design a complete new desktop UI for **LocalTranscriber**, a privacy-first Windows meeting-transcription app with an optional live AI voice assistant. Below is the full context on what the app is, what it does, the technology it runs on, and the constraints the design must respect.

## 1. What the app is and who it's for

LocalTranscriber is a **local-only Windows desktop app** that records meetings and transcribes them **fully offline** — no cloud, no accounts, no uploads. It captures two audio tracks simultaneously:

- the user's **microphone** (their own voice, always labeled with their name, "Me" by default), and
- **system audio** (loopback — what comes out of the speakers, i.e. the other meeting participants on Teams/Zoom/etc.).

It transcribes both in near-real-time, **identifies and remembers speakers by voice** across sessions, and writes two transcript files continuously: a human-readable `.txt` and a machine-readable `.jsonl`.

The typical user is a single professional (often a developer or engineer) sitting in back-to-back remote meetings who wants a private, trustworthy record of every conversation — plus, optionally, a private AI copilot they can talk to during the meeting.

**Privacy is the product's core identity.** Transcription never touches the cloud. The one optional cloud feature (the AI voice assistant, below) is off by default, opt-in, and clearly bounded. The UI should make this trust posture visible — the user should always be able to tell at a glance what is local and what (if anything) is leaving the machine.

## 2. Complete feature inventory

### A. Recording session (the core loop)
- Pick an output folder, press **Start**. The app records mic + system audio, transcribes in chunks (~10s), and streams lines into a live transcript preview.
- Transcript lines look like: `[10:04:12] Me: Can everyone hear me?` / `[10:04:23] Joe: Let's move deployment to Friday.` / `[10:04:31] possibly Martina: I need to check the test results.`
- Controls: **Start / Stop / Pause / Resume / Clear preview**. Status text (Idle / Recording / Paused / Error) and a session ID are shown.
- Errors (device lost, model missing) surface as a red text line today.

### B. Speaker identity and memory
- Unknown voices in system audio get session labels (Speaker 1, Speaker 2…), kept consistent within a session by voice-embedding similarity.
- The user can **rename** a speaker ("Speaker 2" → "Joe"); the voice embedding is kept, so in future sessions Joe is recognized automatically. Confidence tiers: high similarity → `Joe:`, medium → `possibly Joe:`, low → new session label.
- Speakers screen shows the roster (name, sample count, last-seen date) with **Rename / Forget / Refresh** actions. Enrollment from a WAV sample also exists (CLI).

### C. AI voice assistant (optional, opt-in, the "Agent")
- A real-time **voice conversation** with an OpenAI Realtime model during a meeting. The AI is grounded in (a) the live transcript, injected silently every ~15s, and (b) local "context packs" — markdown files describing the user's project, people, decisions, risks, glossary.
- The user talks to it; it replies in a natural voice **privately** (through the user's chosen output device, e.g. headphones — never into the meeting). Live **captions** of the conversation are shown.
- Three voice modes with different privacy levels — this distinction MUST be legible in the UI:
  - **hybrid** (safe default): hold-to-talk → speech transcribed **locally** by whisper → only **text** is sent. No audio ever leaves the machine.
  - **pushToTalk**: hold a button → mic audio streams while held. Requires explicit consent (`sendAudio=true`).
  - **continuous**: mic streams continuously with server-side voice detection and barge-in (the AI stops talking when you speak). Requires the same consent; headphones strongly recommended.
  - Meeting/system audio is **never** sent in any mode — only the user's own mic, only in the two streaming modes.
- Agent controls today: enable toggle, voice-mode picker, output-voice picker (e.g. "marin"), Start/Stop voice, a **Hold to talk** press-and-hold button, mic/output device pickers with a refresh, a "Speak replies" toggle (off = captions only), context/output folder fields, captions area, status line. There's a Bluetooth caveat (headset mic + playback can conflict) currently shown as an italic gray tip.

### D. Settings
- Transcript folder; toggles for mic capture, system capture, and "capture speech only" (drop sound-effect annotations like "(engine revving)"); default mic speaker name; whisper model path; speaker model path; speaker match threshold (0–1); chunk seconds; Save button; audio-device list with refresh.

### E. Other front-ends (context, not part of this redesign)
- A **CLI** (`localtranscriber start/stop/pause/status/tail/speakers/config/agent talk…`) and an **MCP stdio server** expose the same engine to terminals and AI tools. The WPF app, CLI, and MCP all call one shared Engine — the redesign only concerns the WPF desktop app, but the app is one of three equal front-ends, not the whole product.

## 3. Technology stack (design must be implementable in this)

- **C# / .NET 8, WPF (XAML)** — this is a native Windows desktop app, NOT a web app. The design will be implemented in XAML with MVVM data binding. Anything you propose must be realistic in WPF: custom control templates, styles, rounded corners, subtle animations, acrylic/mica-like effects are all fine; web-only patterns are not.
- **MVVM already in place**: `SessionViewModel`, `AgentPanelViewModel`, `SpeakerPanelViewModel`, `SettingsViewModel` expose all the state listed above (status text, preview text, captions, device lists, commands). The redesign should re-skin and re-structure the views; the underlying properties/commands can stay.
- Audio: **NAudio / WASAPI** (mic + loopback capture). Transcription: **whisper.cpp** (local). Diarization/speaker ID: **sherpa-onnx** (local). Storage: **SQLite** (speaker memory, session metadata) + flat transcript files. Voice assistant: **OpenAI Realtime API** over websocket (the one cloud touchpoint).
- Target: **Windows 11**, single window, currently 820×560 (min 640×420), resizable. Fluent/WinUI-adjacent aesthetics are welcome but it's classic WPF, so effects must be hand-built or approximated.
- No third-party UI framework is currently used (no MahApps/ModernWpf) — you may assume plain WPF plus hand-rolled styles, or recommend a lightweight library if it clearly pays for itself.

## 4. Current UI (what you're replacing)

A plain, default-styled WPF `TabControl` with four tabs — functional but visually bare, zero styling, developer-grade:

1. **Session** — title, status text, output-folder row, five flat buttons, a Consolas read-only TextBox as transcript preview, red error line.
2. **Agent** — a dense strip of checkboxes/combos/buttons (enable, voice mode, voice, start/stop voice, hold-to-talk), device pickers with an italic Bluetooth tip, context/output folder row, Consolas captions box, status line, and a dark-yellow italic privacy note about the three modes.
3. **Speakers** — gray note, plain ListBox of speakers, rename textbox + Rename/Forget/Refresh buttons.
4. **Settings** — a vertical stack of labeled TextBoxes and CheckBoxes with a Save button and a device ListBox.

Known pain points to solve:
- No visual hierarchy; everything is the same weight. Recording state is easy to miss.
- The transcript is a raw text dump — no speaker colors, no visual turn structure, no autoscroll affordance, no way to spot "possibly" (uncertain) speaker labels.
- Privacy-critical distinctions (local vs. cloud, which voice mode sends what) live in italic footnote text instead of first-class UI.
- Agent controls are a flat strip with no state feedback (connected? listening? speaking? reconnecting?).
- Hold-to-talk is a plain button with no press/recording feedback.
- Settings is an undifferentiated wall of fields mixing everyday options with advanced model paths.
- No dark mode, no iconography, no branding.

## 5. What to design

Produce a complete UI design for the desktop app:

1. **Visual identity**: color system (light + dark), typography, iconography, and a look that communicates "private, local, trustworthy" — calm and professional, not flashy. The transcript is the hero.
2. **App shell / navigation**: rethink the four-tab layout. Consider whether Session + Agent belong together in one "live meeting" view (they're used simultaneously during a meeting), with Speakers and Settings secondary.
3. **Live transcript view**: speaker-attributed turns (distinct colors or avatars per speaker), timestamps, visual treatment for uncertain "possibly X" labels, source distinction (Me/mic vs. remote/system audio), autoscroll with pause-on-scroll-up, and a clear empty state.
4. **Recording state system**: unmistakable Idle / Recording / Paused / Error states — think status color, pulsing indicator, elapsed time, and a persistent header so state is visible from any screen.
5. **Agent / voice assistant surface**: connection + activity states (off, connecting, listening, thinking, speaking, reconnecting), live captions styled as a conversation, a satisfying hold-to-talk interaction (pressed/recording feedback), and device pickers that don't dominate the layout. Include the "Speak replies" and Bluetooth guidance in a sane place.
6. **Privacy UX as a first-class element**: a persistent, glanceable indicator of what's leaving the machine (e.g. "100% local" vs. "voice: text-only to OpenAI" vs. "voice: mic streaming"). The consent step for the streaming modes should be an explicit, well-designed moment, not a config flag. Design the mode picker so hybrid/pushToTalk/continuous trade-offs are self-explanatory.
7. **Speakers view**: roster cards with name, sample count, last seen, inline rename, and a considered "Forget" (destructive) treatment.
8. **Settings**: grouped (General / Audio / Models / Advanced), with sensible controls (folder pickers, sliders for thresholds) and save feedback.
9. **States & edge cases**: empty states (first run, no models downloaded, no speakers yet), error states (device lost, API key missing, websocket reconnecting), long-session behavior (hours of transcript), and window resizing down to ~640×420.

Deliver screen mockups for each main view (light and dark), the component states listed above, and a short rationale for the layout/navigation choices — enough specification (spacing, colors as tokens, type scale) that a developer can translate it into WPF XAML styles and templates.
