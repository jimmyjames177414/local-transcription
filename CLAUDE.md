# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Project

**LocalTranscriber** — a local-only Windows transcription app with an optional live meeting
AI sidecar. Captures microphone and system audio, transcribes fully offline (whisper.cpp),
labels and remembers speakers (sherpa-onnx + SQLite), writes `.txt` and `.jsonl` transcripts,
and exposes a WPF UI, a CLI, and an MCP server.

The agent layer (optional, off by default) tails the live transcript and produces private
suggestions — risks, action items, decisions, contradictions — via a fake offline provider or
OpenAI text/realtime models, with local context packs and private TTS. See `docs/AGENT.md`.

Stack: C# / .NET 8, WPF, NAudio/WASAPI (capture), whisper.cpp (transcription),
sherpa-onnx (diarization + speaker ID), SQLite (speaker memory + session metadata).

**Hard rules:** transcription never uses the cloud; the agent is opt-in; no audio ever leaves
the machine; no hidden uploads. Buildable from the VS Code terminal only — Visual Studio not
required. The WPF UI, CLI, and MCP server must all call the same shared `Engine` — never
duplicate transcription logic.

## Solution layout (`src/`)

- `LocalTranscriber.App` — WPF UI
- `LocalTranscriber.Cli` — command-line control
- `LocalTranscriber.Mcp` — MCP stdio server
- `LocalTranscriber.Engine` — shared orchestration engine (all front-ends call this)
- `LocalTranscriber.Audio` — capture (NAudio/WASAPI)
- `LocalTranscriber.AI` — transcription (whisper.cpp)
- `LocalTranscriber.Speakers` — diarization / speaker ID (sherpa-onnx)
- `LocalTranscriber.Storage` — SQLite persistence
- `LocalTranscriber.Agent` — optional live meeting AI sidecar
- `LocalTranscriber.Context` — context packs / retrieval
- `LocalTranscriber.Shared` — shared types/utilities

## Common commands (PowerShell)

- Build: `dotnet build`
- Test: `dotnet test`
- Models (one-time): `./scripts/setup.ps1 -DownloadModels`
- Run app: `./scripts/run-app.ps1`
- Run MCP: `./scripts/run-mcp.ps1`

See `docs/ARCHITECTURE.md`, `docs/USAGE.md`, and `docs/ROADMAP.md`.

## Log runners for Claude

To watch a running session, launch a target with its console captured to a file, then snapshot it:

- Start (backgrounded): `./scripts/run-with-logs.ps1 -Target app|cli|mcp` (pass CLI args via `-AppArgs '...'`)
- Snapshot logs: `./scripts/tail-logs.ps1 [-Target all|app|cli|mcp] [-Lines N] [-Errors]`
- Bounded follow: `./scripts/tail-logs.ps1 -Follow -Timeout 5` (always time-bounded; never blocks)
- Stop everything: `./scripts/stop-logs.ps1`

Log locations (both read by `tail-logs.ps1`):
- `tail-logs/<target>.log` — merged stdout+stderr captured by the runner (gitignored)
- `output/logs/*.log` — the app's own logs (also visible when you just press F5)

Equivalent VS Code tasks: **Run App/CLI/MCP (Claude Logs)**, **Tail Logs**, **Stop Log Runners**.

Pressing **F5** (App / CLI / MCP profiles) runs the `Debug: build + follow logs` preLaunchTask, which
builds and then opens a live `tail-logs.ps1 -Follow` terminal on `output/logs/` while you debug. The
debugger is attached to the process; the tailer just streams the app's own logs alongside it. When you
stop debugging, the `Debug: stop log follower` postDebugTask kills that follower automatically.

**MCP caveat:** `-Target mcp` is observation-only — MCP stdout is the JSON-RPC protocol stream, so the
captured file is not a live client session. Real clients connect via `.vscode/mcp.json`.

## Non-negotiable rules

1. **Stay in scope.** Implement only what was asked. No drive-by refactors or unrequested
   "improvements." Spot something worth fixing? Mention it — don't change it.
2. **Be honest about uncertainty.** If you're guessing or haven't verified, say so plainly.
3. **Never commit or push** unless the user explicitly asks.
4. **Verify before relying.** Confirm a dependency exists before using it; check existing
   patterns before writing new code.
5. **Don't create files needlessly.** Prefer editing existing files. Don't add documentation
   files unless explicitly requested.

## Engineering philosophy

- **KISS** — simplest solution that works.
- **YAGNI** — build only what's needed now.
- **DRY** — reuse existing code and patterns; one shared engine.

## Current state & session handoff (2026-07-09)

- Branch: `agent-upgrade` (all 14 agent phases done, 133 tests green). `main` holds pristine v1 (tag `v1.0-offline`). Merge decision pending. Backups: `C:\_repos\backups\` (git bundle + tar.gz).
- User config (`output/config.json`): `agent.provider=openai`, `agent.openAI.enabled=true`. Code defaults stay disabled/fake by design. OpenAI key: gitignored `output/secrets.json` (env var `OPENAI_API_KEY` wins). Models on this key: `gpt-5.4-mini` (text), `gpt-realtime-2.1-mini` (realtime).
- If working from WSL: use the Windows dotnet — `"/mnt/c/Program Files/dotnet/dotnet.exe" build|test|run` (SDK 8.0.422). Known WSL quirks: `rm -rf` on /mnt/c and `powershell -ExecutionPolicy Bypass` may be permission-denied; /mnt/c 9p cache can lag right after a Windows process writes; two concurrent `dotnet run` hit file locks (run the built dll for a second process).
- Realtime GA API notes (learned live): no `OpenAI-Beta` header; `session.update` needs `session.type="realtime"` + `output_modalities`; reply text arrives via `response.output_text.done`; replies may be a bare JSON array; string confidence labels possible. Parser handles all. Debug: `LT_REALTIME_DEBUG=1` (from WSL add `WSLENV=LT_REALTIME_DEBUG/w`).
- Open items: fill `context/*.md` placeholders (esp. `codename-summary.md` — biggest suggestion-quality lever); user manual checkpoints: Agent tab walkthrough, `agent voice on` trial, real meeting test with headphones.

## Wiki knowledge base

This repo has a claude-obsidian wiki vault at `.local/.context2`. See `CLAUDE.local.md`
for the read-order, the `/wiki` skill family, and vault file-placement rules. Use it for
persistent, cross-session knowledge; keep it selective.
