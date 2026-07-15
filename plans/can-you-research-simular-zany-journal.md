# Healthy Refactoring Plan — LocalTranscriber

## Context

**Why this exists:** The ask was to research comparable open-source apps on GitHub, judge whether
LocalTranscriber needs a "healthy refactoring," and steal any better architectural patterns. This
plan is the answer: a measured, prioritized refactor — **not** a rewrite.

**Honest verdict:** The architecture is fundamentally sound and does **not** need a teardown. It
already matches the patterns the strongest projects in this space use:

| Best-practice pattern | Where LocalTranscriber already does it |
|---|---|
| STT via managed bindings + factory/processor lifetime | `WhisperCppTranscriptionService` (Whisper.net, lazy `WhisperFactory` cache) — same as Whisper.net's own design |
| Provider abstraction over interchangeable backends | `IRealtimeVoiceConversation` with 3 backends (OpenAI / claude-cli / hybrid) — genuinely well done |
| Channels for producer/consumer decoupling | `RealTranscriptionEngine` uses `Channel<AudioWindow>` + `Channel<TranscriptEvent>` |
| VAD gate in front of inference | Silero VAD pre-filter in `WhisperCppTranscriptionService`; peak-level silence gate in engine |
| Flat human-readable files + DB only for structured data | `.txt`/`.jsonl` writers + SQLite for speaker memory (mirrors Hyprnote/Buzz) |
| Two-model diarization (segmentation + embedding) | sherpa-onnx `ISpeakerDiarizationService` + `ISpeakerEmbeddingService` |
| Clean dependency direction + interface seams | `Shared ← Audio/AI/Speakers/Storage ← Engine ← front-ends`; pervasive interfaces; strong native disposal |

Reference projects reviewed: Meetily, Hyprnote, Buzz, Vibe, Const-me/Whisper, Whisper.net, WhisperX,
sherpa-onnx. The gaps they expose are concentrated, not systemic.

**Chosen scope: "quick wins + structural."** Full clean-architecture re-layering was rejected as a
YAGNI violation — the dependency direction is already correct, so a formal
Domain/Application/Infrastructure split would be high-churn file-moving with real regression risk
against 133 green tests and little practical payoff for a single-developer app. The two genuinely
valuable "full-tier" ideas — word-level speaker alignment and subprocess crash-isolation — are
feature/quality efforts, not refactors, and are listed as **separate follow-ups**, not part of this
work.

Prioritized to the user's stated priorities: Accuracy/correctness, Quality infra, Maintainability,
Consistency/DI.

---

## Phase A — Quick wins (low risk, high leverage)

### A1. Quality infrastructure (Quality infra)
- Add `Directory.Build.props` at repo root enabling `Nullable`, `TreatWarningsAsErrors`,
  `EnableNETAnalyzers`, `AnalysisLevel=latest` for all projects (nullable/implicit-usings are
  currently set per-csproj; centralize + tighten).
- Add `Directory.Packages.props` for central package management; move all `PackageReference`
  versions there. Pin the two pre-release deps deliberately: `System.CommandLine`
  (beta4.22272.1) and `ModelContextProtocol` (preview.1) with a comment noting they're pre-release.
- Add `.github/workflows/ci.yml`: `dotnet build` + `dotnet test` on push/PR (windows-latest, .NET 8).
  Native whisper/sherpa inference can't run in CI — tests already isolate that behind fakes, so the
  suite runs clean. This is the single biggest quality gap (no CI exists today).
- Delete the two leftover scaffold files `tests/*/UnitTest1.cs` (Engine.Tests, Storage.Tests).

### A2. Remove dead diagnostic weight (Maintainability)
- `src/LocalTranscriber.AI/WhisperCppTranscriptionService.cs`: remove the `TranscribeDebug`
  double-run branch (~lines 60–146, self-labeled "TEMP DIAGNOSTIC"; runs whisper twice). Keep the
  normal VAD short-circuit path.

### A3. Fix the MCP/IPC stop gap (Accuracy/correctness)
- Today CLI `stop/pause/resume/status` talk to the named pipe `localtranscriber-control`
  (`EngineIpcClient`), but MCP calls `_service.Engine.*` in-process and never hosts/uses the pipe.
  Result: an MCP `stop` and a CLI `stop` can target different engine instances.
- Fix: have `TranscriberService` (`src/LocalTranscriber.Mcp/TranscriberService.cs`) host an
  `EngineIpcServer` when it owns a live real engine (mirror `SessionCommands.cs:52` /
  `MainWindowViewModel`), OR make MCP control verbs route through `EngineIpcClient` first and only
  fall back to the in-process engine. Document the single ownership model in `docs/MCP.md`.

### A4. Documentation drift (Maintainability)
- `docs/ARCHITECTURE.md`: add the three projects it predates — `Agent`, `Context`, `Voice` — to the
  project table and dependency diagram (12 projects, not 9).
- `docs/ROADMAP.md`: replace "all 14 phases done / nothing pending" with the real open items
  (from CLAUDE.md handoff + this plan's follow-ups).
- `docs/AGENT.md`: it's already marked superseded — add a one-line pointer to the current
  voice-sidecar docs so it isn't mistaken for live design.

---

## Phase B — Structural (contained, test-backed)

### B1. Unify composition on the generic host (Consistency/DI)
Only MCP uses `Microsoft.Extensions.Hosting` + DI today (`src/LocalTranscriber.Mcp/Program.cs`).
WPF hand-news every ViewModel in `MainWindow.xaml.cs:16-60` (`= new()`), and the engine self-builds
inside `MainWindowViewModel:47`; CLI uses static command factories.

- Add `services.AddTranscriptionCore(AppConfig)` extension (new file in `LocalTranscriber.Engine`)
  that registers the engine graph currently wired by hand in `EngineFactory.CreateReal`
  (`EngineFactory.cs:13`) — SQLite stores, whisper, sherpa services, recognition. `EngineFactory`
  becomes a thin wrapper over it (keep for tests / non-DI callers; don't duplicate the wiring).
- WPF (`App.xaml.cs`, currently empty): build a host in `OnStartup` via
  `Host.CreateApplicationBuilder`, call `AddTranscriptionCore`, register ViewModels, resolve
  `MainWindow` from DI; dispose host in `OnExit`. Remove `= new()` initializers; inject ViewModels.
- CLI: build a host in its entry point, resolve command handlers/engine from DI.
- Net effect: one shared registration path, three thin composition roots — the pattern Meetily /
  Hyprnote use (thin clients over one core).

### B2. Provider selection via keyed services (Consistency/DI, Maintainability)
- `src/LocalTranscriber.Voice/AgentConversationFactory.cs:23-31` string-switches on
  `config.Agent.Provider` (`ToLowerInvariant`). Replace with .NET 8 **keyed DI services**:
  `AddKeyedSingleton<IRealtimeVoiceConversation>("openai"/"claude-cli"/"hybrid", ...)` resolved by a
  small `IProviderSelector` that maps config → key. Keeps the `Resolution(Session?, Notice?)`
  no-throw contract. Removes the string switch and centralizes selection.

### B3. Split the two God-classes (Maintainability)
Both are internally well-sectioned but overloaded; extract along existing seams — behavior-preserving,
covered by existing tests in `tests/LocalTranscriber.Engine.Tests` and `.Voice.Tests`.

- **`RealTranscriptionEngine.cs` (~950 lines):** extract the system-audio speaker pipeline
  (`ProcessSystemWindowAsync`, `ExtractAveragedEmbeddingAsync`, `AssignSpeaker`) into a
  `SpeakerLabeler` collaborator, and the capture/watchdog/reconnect lifecycle into a
  `CaptureHost`. Engine keeps orchestration + lifecycle state machine only.
- **`RealtimeVoiceSession.cs` (~810 lines):** separate connection/reconnect, the mic-pump, and the
  grounding loops into collaborators behind the existing `IRealtimeVoiceConversation` surface.
- Guardrail: no public API changes; run `dotnet test` after each extraction.

### B4. Backpressure + config unification (Accuracy/correctness, Consistency/DI)
- Make the live-path channels bounded: `Channel<AudioWindow>` and `Channel<TranscriptEvent>` in
  `RealTranscriptionEngine` are currently **unbounded** (only the silence gate relieves memory under
  sustained speech). Switch to `BoundedChannelFullMode.DropOldest` (audio) — the voice path already
  proves this pattern (`RealtimeVoiceSession` uses a bounded `Channel<byte[]>(64, DropOldest)`).
- Retire the dual config models: `AppConfig` (mutable) vs `TranscriptionSessionOptions` (immutable
  record) are bridged by hand in `EngineFactory.CreateSessionOptions`. Adopt `IOptionsMonitor<T>`
  bound from `config.json` so engine singletons pick up edits (model path, thresholds, provider)
  without restart; add `.ValidateOnStart()`. This is the one low-risk item pulled forward from the
  "full" tier.

---

## Explicitly NOT doing (recorded follow-ups)
- **Full Domain/Application/Infrastructure re-layering** — rejected as YAGNI; dependency direction is
  already correct.
- **Word-level speaker alignment (WhisperX-style)** — current alignment is segment-level. A real
  accuracy win, but a feature effort with its own risk; track separately.
- **Subprocess crash-isolation for whisper/sherpa** — Buzz-style out-of-process native execution so a
  segfault can't kill the WPF host. Worth it later; not a refactor.
- **Dynamic plugin loading (AssemblyLoadContext)** — no third-party-provider requirement exists.

---

## Verification
- `dotnet build` clean with warnings-as-errors after A1.
- `dotnet test` green after **each** structural extraction (B3) and after B1/B2/B4 — 133 tests are
  the regression net; no test should need rewriting for behavior-preserving refactors (only DI
  wiring in test setup may change).
- CI (A3 workflow) runs build+test on a clean checkout — proves no machine-local assumptions leaked.
- Manual smoke (Windows, from CLAUDE.md log runners):
  - `./scripts/run-app.ps1` — start/stop a session, confirm transcript + speaker labels still write.
  - CLI `start` in one process, `stop`/`status` from another — confirm IPC still controls it.
  - MCP `start_transcription` then `stop` (A3) — confirm it now controls the same engine the CLI sees.
  - `agent voice on` with each provider (openai / claude-cli / hybrid) — confirm keyed-DI selection
    (B2) resolves the same backends as before.
- Backpressure (B4): run the fake engine / a long synthetic session and confirm bounded channels cap
  memory without dropping the transcript stream.

## Suggested sequencing
A1 → A2 → A4 (cheap, independent) → A3 (correctness) → B1 (unlocks B2/B4) → B2 → B4 → B3 (largest;
do last, one extraction at a time). Each item is independently shippable and test-verified.
