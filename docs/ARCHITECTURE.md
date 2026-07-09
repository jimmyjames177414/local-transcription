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

## Dependency direction

```text
App / Cli / Mcp -> Engine -> Audio / AI / Speakers / Storage -> Shared
```

Lower layers never reference upper layers.

## Engine

`ITranscriptionEngine` exposes Start/Stop/Pause/Resume/GetStatus and an event stream (`IAsyncEnumerable<TranscriptEvent>`). `FakeTranscriptionEngine` emits synthetic events on a timer so front-ends integrate against a stable API before real audio/AI lands.
