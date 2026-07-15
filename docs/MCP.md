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
| `start_fake_transcription` | Start a fake session (synthetic lines, no audio) |
| `start_transcription` | Start a real local session (mic/system audio + whisper + diarization) |
| `stop_transcription` / `pause_transcription` / `resume_transcription` | Session control |
| `tail_transcript` | Last N lines of a transcript (transcript folder only) |
| `read_current_transcript` | Full current/latest transcript |
| `list_sessions` | Sessions from SQLite |
| `list_known_speakers` | Known speakers from SQLite |
| `rename_speaker` / `forget_speaker` | Speaker management |
| `set_output_folder` | Change transcript output folder |

### Session-control ownership (single owner)

A live session is controlled through one named pipe (`localtranscriber-control`), and only one
process owns it at a time. The control tools (`get_status` / `stop` / `pause` / `resume`) resolve
the target the same way the CLI does:

- If the MCP process started the session itself (`start_transcription` / `start_fake_transcription`),
  it controls that in-process engine directly and hosts the pipe so the CLI/app can reach it too —
  but only when no other process already owns the pipe.
- Otherwise the control tools route over the pipe to whichever process (the WPF app or a CLI
  `start`) owns the live session. This is what lets an MCP `stop` end a session running in the app.
- If nothing is listening, the tools report the idle in-process engine.

## Agent tools

| Tool | Purpose |
|---|---|
| `agent_get_status` | Agent config + latest stored activity |
| `agent_get_suggestions` / `agent_get_latest_suggestion` | Read stored suggestions |
| `agent_get_summary` / `agent_get_action_items` | Running summary / action items |
| `agent_dismiss_suggestion` | Dismiss by id |
| `agent_set_mode` / `agent_get_mode` | Response mode |
| `agent_ask` | Ask about the current meeting (latest transcript + context, configured provider) |
| `context_list_documents` / `context_read_document` / `context_validate` | Context pack (context folder only) |

## Security

- Transport is stdio only; the server never opens a network port.
- Transcript reads are restricted to the configured transcript folder. Relative and absolute paths are resolved and checked; traversal (`..`) outside the folder is rejected.
- No tool executes shell commands or accepts executable paths.
- Every tool call is logged to `output/logs/mcp-tool-calls.log` (no transcript text in logs).
