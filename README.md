# LocalTranscriber

Local-only Windows transcription app. Captures microphone and system audio, transcribes fully offline (whisper.cpp), labels and remembers speakers (sherpa-onnx + SQLite), writes `.txt` and `.jsonl` transcripts, and exposes a WPF UI, a CLI, and an MCP server.

**Hard rules:** no cloud transcription, no API keys, no paid services, no hidden uploads. Buildable from VS Code terminal only — Visual Studio not required.

## Requirements

- Windows 10/11
- .NET 8 SDK

## Build

```powershell
dotnet build
```

## Test

```powershell
dotnet test
```

## Models (one-time)

```powershell
./scripts/setup.ps1 -DownloadModels   # whisper base.en + sherpa-onnx speaker models (~190 MB)
```

## Run

```powershell
# WPF app
./scripts/run-app.ps1

# CLI — real session, controllable from other terminals
dotnet run --project src/LocalTranscriber.Cli -- start --output "./output/transcripts/meeting.txt"
dotnet run --project src/LocalTranscriber.Cli -- status
dotnet run --project src/LocalTranscriber.Cli -- stop

# MCP server (stdio, register with: claude mcp add local-transcriber -- dotnet run --project src/LocalTranscriber.Mcp)
./scripts/run-mcp.ps1
```

Full command reference in `docs/USAGE.md`. Packaging: `./scripts/publish.ps1` then `./scripts/package.ps1`.

## Architecture

```text
WPF UI  ─────┐
CLI      ─────┼──> LocalTranscriber.Engine ───> Audio / AI / Speakers / Storage
MCP      ─────┘
```

See `docs/ARCHITECTURE.md`. Development status and phase progress in `docs/ROADMAP.md`.
