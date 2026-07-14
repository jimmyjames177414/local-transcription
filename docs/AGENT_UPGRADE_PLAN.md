# Agent Upgrade Plan — Live Meeting AI Sidecar

Audit performed 2026-07-09 at v1.0 (tag `v1.0-offline`, commit 434e3f5). Upgrade work happens on branch `agent-upgrade`. Backups: `repos/backups/LocalTranscriber-v1.0-20260709.bundle` (full git history) + `.tar.gz` (working tree incl. models).

## Current state (v1.0)

| Area | Status |
|---|---|
| Projects | Shared, Storage, Audio, AI, Speakers, Engine, App (WPF), Cli, Mcp + 4 test projects |
| Transcript `.jsonl` | `JsonlTranscriptWriter` (Storage) — one compact object/line: `sessionId, timestamp (UTC ISO), speakerId, speakerName, source (microphone/systemAudio/unknown), text, confidence?, startMs?, endMs?`. **No `id` field in the line.** |
| Event model | `TranscriptEvent` record (Shared) with `SpeakerLabel` |
| Engine | `ITranscriptionEngine`; `FakeTranscriptionEngine` (timer) + `RealTranscriptionEngine` (NAudio → whisper → sherpa-onnx diarize → speaker memory). Both write `.txt` + `.jsonl` + SQLite and stream events |
| CLI | System.CommandLine; areas: session (start/stop/status via named-pipe IPC), transcribe, audio, speakers, config, tail/read/sessions |
| MCP | `ModelContextProtocol` SDK 2.0-preview.1, stdio; `LocalTranscriberTools` (12 tools), `TranscriberService`, `ToolCallLogger`; path safety via `SafePathValidator` |
| UI | WPF TabControl (Session/Speakers/Settings), hand-rolled MVVM (`ObservableObject`, `RelayCommand`, `AsyncRelayCommand`) |
| Config | `AppConfig` (Shared) + `ConfigService` (Storage) at `output/config.json` (dev) / `%AppData%` (packaged) via `AppPaths` |
| SQLite | `SqliteDatabase` (schema string) + stores: sessions, transcript_events, known_speakers, speaker_embeddings |
| Logging | `AppLog` (Shared) daily file; MCP tool-call log |

## Integration points for the agent layer

1. **Input**: tail the live `.jsonl` (never reread `.txt`). Parser must match `JsonlTranscriptWriter.Serialize` field names. Event identity for dedup = file offset + line hash (no id in line).
2. **New projects**: `src/LocalTranscriber.Agent` (→ Shared, Storage, Context), `src/LocalTranscriber.Context` (→ Shared), `tests/LocalTranscriber.Agent.Tests`.
3. **Config**: nested `AgentConfig` added to `AppConfig` (this phase). `ConfigService.TrySet` extended for dot paths (`agent.mode`).
4. **Storage**: append `agent_suggestions` + `agent_state` tables to `SqliteDatabase.Schema`; new stores follow existing store pattern.
5. **Path safety**: reuse `SafePathValidator` for context folder + agent output folder restriction.
6. **Surfaces**: new WPF Agent tab; `AgentCommands`/`ContextCommands` CLI classes; second `[McpServerToolType]` class in Mcp.
7. **Fake data source**: `FakeTranscriptionEngine` produces growing `.jsonl` — the agent test harness.

## Gaps before upgrade

- No agent/context projects (added stage 1).
- JSONL lines lack a stable event id → tailer supplies identity.
- Config `TrySet` was flat → dot-path support added this phase.
- No secrets mechanism → `SecretsService` arrives with the OpenAI provider stage (gitignored `output/secrets.json`, env var takes precedence).

## Phase map (prompt pack → stages)

| Prompts | Stage | Content |
|---|---|---|
| 1 | 0 | this audit, backups, branch, config, context scaffold |
| 2–5 | 1 | tailer, context packs, MeetingAgent + fake provider, output storage |
| 6–7 | 2 | WPF agent panel, CLI/MCP agent tools |
| 8 | 3 | OpenAI text provider + secrets |
| 9 | 4 | OpenAI Realtime provider (websocket, text-only) |
| 10–12 | 5 | private voice (Windows TTS), response modes/policy, keyword context retrieval |
| 13–14 | 6 | join-mode docs, privacy hardening, final docs |

## Hard rules carried through

Offline transcriber untouched and keyless; agent disabled by default; fake provider = no network; no audio to cloud; MCP reads restricted to transcript/context/agent-output folders; no shell execution; secrets never logged or committed.
