# LocalTranscriber

Local-only Windows transcription app **with an optional live meeting AI sidecar**. Captures microphone and system audio, transcribes fully offline (whisper.cpp), labels and remembers speakers (sherpa-onnx + SQLite), writes `.txt` and `.jsonl` transcripts, and exposes a WPF UI, a CLI, and an MCP server.

The agent layer (optional, off by default) is a real-time voice conversation with the AI during a meeting — you talk, it replies in a natural OpenAI voice, grounded in your local context packs and the live transcript. See `docs/AGENT.md` and `docs/REALTIME_PROVIDER.md`.

**Hard rules:** transcription is always local and never uses the cloud; the agent is opt-in and off by default; real-time voice is opt-in and sends audio only when enabled (the default `hybrid` mode transcribes your speech locally and sends text only); no hidden uploads. Buildable from VS Code terminal only — Visual Studio not required.

## Requirements

- Windows 10/11
- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)
- VS Code (recommended). **Visual Studio is not required.**

## Quick start

```powershell
# 1. Restore packages and download the offline AI models (~190 MB, one time)
./scripts/setup.ps1 -DownloadModels

# 2. Build and verify
dotnet build
dotnet test

# 3. Launch the app
./scripts/run-app.ps1
```

Without `-DownloadModels` nothing is fetched; the app then shows helpful errors pointing at the
expected model paths. The three models are:

| File | Purpose | Size |
|---|---|---|
| `models/whisper/ggml-base.en.bin` | offline transcription | ~142 MB |
| `models/speaker/segmentation.onnx` | speaker segmentation | ~6 MB |
| `models/speaker/embedding.onnx` | voice embeddings | ~40 MB |

## Running the app

**WPF UI (the main way):**

```powershell
./scripts/run-app.ps1
```

Or press **F5** in VS Code and choose the **App (WPF UI)** debug profile.

The window has three tabs:

- **Session** — pick an output folder and press **Start**. The live transcript appears in the
  preview while `.txt` (human) and `.jsonl` (machine) files are written continuously.
  Pause / Resume / Stop as needed.
- **Speakers** — rename detected speakers (e.g. "Speaker 2" → "Joe"). Renames keep the voice
  fingerprint, so later sessions greet Joe by name. Forget removes the speaker and its voice data.
- **Settings** — transcript folder, mic/system-audio toggles, model paths, match thresholds,
  chunk size. "Refresh audio devices" lists what the app can capture.

> **Tip:** wear headphones. If your mic hears the remote speakers, their speech bleeds into your
> "Me" track and speaker separation degrades.

For plain transcription you need nothing else — just start a session.

## Other front-ends (optional)

All three front-ends drive the same shared `Engine`.

**CLI** — scriptable; a running session can be controlled from other terminals:

```powershell
dotnet run --project src/LocalTranscriber.Cli -- audio devices
dotnet run --project src/LocalTranscriber.Cli -- start --output "./output/transcripts/meeting.txt"
dotnet run --project src/LocalTranscriber.Cli -- status   # from another terminal
dotnet run --project src/LocalTranscriber.Cli -- stop
dotnet run --project src/LocalTranscriber.Cli -- transcribe --audio "./recording.wav"
```

In the dev checkout, the `localtranscriber` command used throughout the docs means
`dotnet run --project src/LocalTranscriber.Cli --`.

**MCP server** — exposes the transcriber as tools to an MCP client (e.g. Claude):

```powershell
./scripts/run-mcp.ps1
# register: claude mcp add local-transcriber -- dotnet run --project src/LocalTranscriber.Mcp
```

**Meeting AI sidecar (the "agent")** — **off by default, opt-in.** It tails the live `.jsonl`
transcript and produces private suggestions (risks, action items, decisions, contradictions,
questions to ask). It has an offline **fake** provider (no cloud) and an **OpenAI** provider
(the only part that can reach the cloud, and only if you enable it):

```powershell
# offline demo, no key required:
dotnet run --project src/LocalTranscriber.Cli -- agent start-fake --transcript "./output/transcripts/<session>.jsonl"
```

In the UI it's the **Agent tab** (enable → pick mode + provider → Start while a session runs).
See [docs/AGENT.md](docs/AGENT.md) for modes and configuration.

## How speaker memory works

1. Unknown voices in system audio get session labels (Speaker 1, 2, …), kept consistent within a
   session by voice-embedding similarity.
2. You rename or enroll them (UI, CLI, or MCP).
3. In later sessions each voice is matched against stored fingerprints:
   - similarity ≥ 0.72 → `Joe:`
   - 0.62–0.72 → `possibly Joe:`
   - below → a new session label.

Thresholds are configurable (`speakerMatchThreshold`, `speakerUncertainThreshold`).

## Transcript formats

`.txt` (human-readable):

```text
[10:04:12] Me: Can everyone hear me?
[10:04:18] Speaker 1: Yes, I can hear you.
[10:04:23] Joe: Let's move deployment to Friday.
```

`.jsonl` (machine): one JSON object per line with sessionId, timestamp, speakerId, speakerName,
source (`microphone`/`systemAudio`), text, confidence, startMs, endMs.

## Where files land

In the dev checkout, everything is written under `./output/`:

| Path | Contents |
|---|---|
| `output/transcripts/` | `.txt` + `.jsonl` transcripts |
| `output/agent/` | agent suggestions, summaries, action items |
| `output/config.json` | configuration |
| `output/logs/` | logs |

Packaged builds store data under `%AppData%\LocalTranscriber\` and transcripts under
`Documents\LocalTranscriber\Transcripts\`. Override the data root with the
`LOCALTRANSCRIBER_HOME` environment variable. See [docs/SETUP.md](docs/SETUP.md).

## Debugging in VS Code

`.vscode/launch.json` provides ready-to-use debug profiles (press **F5**):

- **App (WPF UI)** — launches the WPF app
- **CLI (prompt for args)** — asks for CLI arguments each run
- **CLI: audio devices** — quick preset
- **MCP server (stdio)** — runs the MCP server
- **Attach to process** — attach to a running instance

Each profile runs the `build` task first and sets the working directory to the repo root so the
dev-checkout paths (`./output/`, `./models/`) resolve. Debugging requires the C# extension
(`ms-dotnettools.csharp`); VS Code will prompt to install it on first use.

## First run checklist

1. `./scripts/setup.ps1 -DownloadModels` (once)
2. `./scripts/run-app.ps1`
3. **Settings** tab → confirm your mic / system-audio devices.
4. **Session** tab → Start, talk for a bit, watch the live transcript, Stop.
5. **Speakers** tab → rename any detected speakers.

## Packaging

```powershell
./scripts/publish.ps1   # self-contained win-x64 exes -> release/LocalTranscriber
./scripts/package.ps1   # zips the release folder
```

## Documentation

| Doc | Topic |
|---|---|
| [docs/SETUP.md](docs/SETUP.md) | prerequisites, models, storage locations |
| [docs/USAGE.md](docs/USAGE.md) | full UI / CLI command reference |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | components and data flow |
| [docs/AGENT.md](docs/AGENT.md) | the optional live meeting AI sidecar |
| [docs/CONTEXT_PACKS.md](docs/CONTEXT_PACKS.md) | context packs for the agent |
| [docs/OPENAI_PROVIDER.md](docs/OPENAI_PROVIDER.md) | OpenAI text provider setup |
| [docs/REALTIME_PROVIDER.md](docs/REALTIME_PROVIDER.md) | OpenAI realtime provider setup |
| [docs/MCP.md](docs/MCP.md) | MCP server tools |
| [docs/PRIVACY.md](docs/PRIVACY.md) | privacy guarantees |
| [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | common issues |
| [docs/ROADMAP.md](docs/ROADMAP.md) | development status |

## Architecture

```text
WPF UI  ─────┐
CLI      ─────┼──> LocalTranscriber.Engine ───> Audio / AI / Speakers / Storage
MCP      ─────┘
```

See `docs/ARCHITECTURE.md`. Development status and phase progress in `docs/ROADMAP.md`.
