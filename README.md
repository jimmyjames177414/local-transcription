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

## Run

```powershell
# WPF app
./scripts/run-app.ps1

# CLI
./scripts/run-cli.ps1 status
dotnet run --project src/LocalTranscriber.Cli -- status

# MCP server (placeholder until Phase 7)
./scripts/run-mcp.ps1
```

## CLI examples

```powershell
dotnet run --project src/LocalTranscriber.Cli -- fake-session --output "./output/transcripts/test.txt" --lines 10
dotnet run --project src/LocalTranscriber.Cli -- tail --file "./output/transcripts/test.txt" --lines 20
dotnet run --project src/LocalTranscriber.Cli -- config show
dotnet run --project src/LocalTranscriber.Cli -- config set transcriptFolder "./output/transcripts"
```

## Architecture

```text
WPF UI  ─────┐
CLI      ─────┼──> LocalTranscriber.Engine ───> Audio / AI / Speakers / Storage
MCP      ─────┘
```

See `docs/ARCHITECTURE.md`. Development status and phase progress in `docs/ROADMAP.md`.
