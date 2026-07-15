# Roadmap

| Phase | Content | Status |
|---|---|---|
| 1 | Repo skeleton | done |
| 2 | Transcript writers (.txt / .jsonl) | done |
| 3 | Fake transcription engine | done |
| 4 | CLI | done |
| 5 | WPF UI | done (fake engine) |
| 6 | SQLite storage | done |
| 7 | MCP server | done (fake engine) |
| 8 | Audio capture (NAudio/WASAPI) | done |
| 9 | whisper.cpp transcription | done |
| 10 | sherpa-onnx diarization | done |
| 11 | Speaker memory | done |
| 12 | End-to-end pipeline | done |
| 13 | Packaging | done |
| 14 | Hardening, testing, docs | done |

## v1 follow-on: live voice sidecar

| Area | Content | Status |
|---|---|---|
| Voice sidecar | Real-time voice conversation (`LocalTranscriber.Voice`): OpenAI realtime / Claude CLI / hybrid providers behind `IRealtimeVoiceConversation`; WSL brain path | done (opt-in, off by default) |
| Context packs | `LocalTranscriber.Context` markdown packs + keyword retrieval to ground the assistant | done |

## Open items (healthy-refactoring pass)

See `plans/can-you-research-simular-zany-journal.md` for the full plan and rationale.

| Item | Content | Status |
|---|---|---|
| CI | GitHub Actions build + test on push/PR | done |
| MCP/IPC control | MCP `stop`/`pause` now route to the same engine the CLI/app controls (shared named pipe) | done |
| Backpressure | Bounded live-path channels (audio queue `DropOldest`; event cap) | done |
| God-class split | `SpeakerLabeler` extracted from `RealTranscriptionEngine`; `CaptureHost` + `RealtimeVoiceSession` split remaining | partial |
| Quality gates | Roslyn analyzers on; central package management + warnings-as-errors still to enable | partial |
| Composition/DI | Unify WPF + CLI on the generic host + shared `AddTranscriptionCore()` (only MCP uses DI today) | planned |
| Provider selection | Replace the string-switch in `AgentConversationFactory` with .NET 8 keyed services | planned |
| Config unification | Retire dual config models (`AppConfig` vs `TranscriptionSessionOptions`) via `IOptionsMonitor` | planned |
| Accuracy (follow-up) | Word-level speaker alignment (WhisperX-style); currently segment-level | backlog |
| Robustness (follow-up) | Optional subprocess crash-isolation for whisper/sherpa native code | backlog |
