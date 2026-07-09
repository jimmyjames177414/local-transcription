# Privacy Review

Verified 2026-07-09 on branch `agent-upgrade`.

| Guarantee | How it holds |
|---|---|
| Offline transcriber needs no key/cloud | Whisper + sherpa-onnx run in-process from local model files; no network code in Audio/AI/Speakers/Engine. Works with no OpenAI key present. |
| Agent disabled by default | `AgentConfig.Enabled=false`, provider `fake`, `openAI.enabled=false`, `realtime.enabled=false`, `voice.enabled=false`, `meetingParticipant` disabled — all code defaults. |
| Fake provider makes no network calls | Pure keyword rules; verified by tests and code inspection. |
| OpenAI providers run only when explicitly enabled | `AgentProviderFactory` requires `enabled=true` AND a resolvable key; otherwise falls back to fake with a visible reason. |
| No raw audio to any provider | Providers receive transcript TEXT events only. `agent.realtime.sendAudio=true` is actively rejected by the factory. |
| Reads limited to safe folders | Context: `.md` inside the configured context folder only. Agent outputs: agent output folder only. Transcripts: transcript folder only. All via `SafePathValidator`; traversal covered by tests. |
| MCP cannot roam the filesystem or run commands | Tools accept names, not paths, resolve inside allowed roots, and no tool shells out. Every tool call logged to `logs/mcp-tool-calls.log`. |
| API keys never logged or committed | Key lives in env var or `secrets.json` under the gitignored data root. Nothing writes it to logs/console; HTTP error bodies come from the server and cannot contain it. |
| Transcript text not logged by default | `AppLog` records events/counts/errors, not transcript lines. MCP tool log records tool names + short args only. |

## What DOES leave the machine (only when you enable it)
When `agent.provider` is `openai` or `realtime` and enabled: transcript text of the rolling window, your context pack markdown text, the running summary, and your ask questions go to OpenAI under your API key. Nothing else; never audio.
