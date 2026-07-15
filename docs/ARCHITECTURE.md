# Architecture

## Rule

The WPF UI, CLI, and MCP server all call the same shared engine. Transcription logic is never duplicated.

```text
WPF UI  ─────┐
CLI      ─────┼──> LocalTranscriber.Engine ───> Audio / AI / Speakers / Storage
MCP      ─────┘
```

## Projects

| Project | Role |
|---|---|
| `LocalTranscriber.Shared` | Data records (`TranscriptEvent`, `SpeakerLabel`), config model, path safety helpers. No dependencies. |
| `LocalTranscriber.Storage` | Transcript writers (`.txt`, `.jsonl`), config service, speaker/session persistence (SQLite in Phase 6). |
| `LocalTranscriber.Audio` | NAudio/WASAPI capture (Phase 8). |
| `LocalTranscriber.AI` | whisper.cpp transcription wrapper (Phase 9). |
| `LocalTranscriber.Speakers` | sherpa-onnx diarization, embeddings, speaker memory (Phases 10–11). |
| `LocalTranscriber.Engine` | Session orchestration. `ITranscriptionEngine` with fake and real implementations. |
| `LocalTranscriber.App` | WPF UI. |
| `LocalTranscriber.Cli` | Command-line interface. |
| `LocalTranscriber.Mcp` | MCP stdio server for Claude integration. |
| `LocalTranscriber.Agent` | Optional live-meeting sidecar primitives: incremental `.jsonl` tailer, rolling transcript window, OpenAI realtime transport. |
| `LocalTranscriber.Context` | Context packs / retrieval (markdown packs, keyword scoring, budget composer) that ground the assistant. |
| `LocalTranscriber.Voice` | Real-time voice conversation providers (OpenAI realtime / Claude CLI / hybrid) behind `IRealtimeVoiceConversation`. Opt-in, off by default. |

## Dependency direction

```text
App / Cli / Mcp -> Engine -> Audio / AI / Speakers / Storage -> Shared

Optional sidecar (opt-in, off by default):
App / Cli / Mcp -> Voice -> Agent / Context / Audio / AI / Storage -> Shared
```

Lower layers never reference upper layers. The `Voice`/`Agent`/`Context` sidecar is optional and
disabled by default; the offline transcription pipeline never depends on it.

## Engine

`ITranscriptionEngine` exposes Start/Stop/Pause/Resume/GetStatus and an event stream (`IAsyncEnumerable<TranscriptEvent>`). Two implementations:

- `FakeTranscriptionEngine` — synthetic events on a timer; used for tests and MCP demos.
- `RealTranscriptionEngine` — the real pipeline:

```text
mic chunks    -> AudioWindowBuffer -> whisper.cpp -> "Me" events ----------------┐
                                                                                 ├-> .txt + .jsonl + SQLite + stream
system chunks -> AudioWindowBuffer -> diarize -> whisper.cpp -> align -> speaker ┘
                                                          memory (cosine match)
```

Mic and system audio stay separate end to end. Windows are `chunkSeconds` long with `overlapMs` carry; silent windows are skipped (whisper hallucinates on silence). Unknown voices get session-stable labels via `SessionSpeakerRegistry` (embedding similarity); known voices come from SQLite speaker memory with confident/uncertain thresholds.

## Cross-process control

The process hosting a session (WPF app or `cli start`) runs `EngineIpcServer` on the named pipe `localtranscriber-control`. Other local processes (CLI `status/stop/pause/resume`) connect as clients. Local machine only, no network.
