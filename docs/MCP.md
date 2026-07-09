# MCP Server

`LocalTranscriber.Mcp` is a local MCP server over **stdio**. No network transport.

## Register with Claude Code

From the repo root:

```powershell
claude mcp add local-transcriber -- dotnet run --project src/LocalTranscriber.Mcp
```

Once published (Phase 13), point at the exe instead:

```powershell
claude mcp add local-transcriber -- C:\path\to\release\LocalTranscriber\LocalTranscriber.Mcp.exe
```

## Tools

| Tool | Purpose |
|---|---|
| `get_status` | State, session id, output paths, event count |
| `start_fake_transcription` | Start a fake session (real audio arrives in Phase 12) |
| `stop_transcription` / `pause_transcription` / `resume_transcription` | Session control |
| `tail_transcript` | Last N lines of a transcript (transcript folder only) |
| `read_current_transcript` | Full current/latest transcript |
| `list_sessions` | Sessions from SQLite |
| `list_known_speakers` | Known speakers from SQLite |
| `rename_speaker` / `forget_speaker` | Speaker management |
| `set_output_folder` | Change transcript output folder |

## Security

- Transport is stdio only; the server never opens a network port.
- Transcript reads are restricted to the configured transcript folder. Relative and absolute paths are resolved and checked; traversal (`..`) outside the folder is rejected.
- No tool executes shell commands or accepts executable paths.
- Every tool call is logged to `output/logs/mcp-tool-calls.log` (no transcript text in logs).
