# Privacy Review

Verified 2026-07-10 on branch `agent-upgrade`.

Transcription is always local; **real-time voice is opt-in and sends audio only when enabled**
(the earlier "no audio ever leaves the machine" rule is waived for that one feature — see below).

| Guarantee | How it holds |
|---|---|
| Offline transcriber needs no key/cloud | Whisper + sherpa-onnx run in-process from local model files; no network code in Audio/AI/Speakers/Engine. Works with no OpenAI key present. |
| Agent disabled by default | `AgentConfig.Enabled=false`, `realtime.enabled=false`, `realtime.voiceMode=off`, `realtime.sendAudio=false` — all code defaults. |
| Voice runs only when explicitly enabled | `RealtimeVoiceFactory` requires `voiceMode != off`, `realtime.enabled=true`, AND a resolvable key; otherwise returns a visible notice and no session. |
| Mic audio streamed only with consent | `hybrid` sends transcript **text only** (local whisper STT). `pushToTalk`/`continuous` stream the microphone and require `agent.realtime.sendAudio=true`. |
| Meeting/system audio never streamed | Voice input is hard-wired to the microphone (`MicrophoneCaptureService`); system-loopback capture is never used for voice. |
| Reads limited to safe folders | Context: `.md` inside the configured context folder only. Transcripts: transcript folder only. All via `SafePathValidator`; traversal covered by tests. |
| MCP cannot roam the filesystem or run commands | Tools accept names, not paths, resolve inside allowed roots, and no tool shells out. Every tool call logged to `logs/mcp-tool-calls.log`. MCP exposes context tools only; voice is not remotely controllable. |
| API keys never logged or committed | Key lives in env var or `secrets.json` under the gitignored data root. Nothing writes it to logs/console; error bodies come from the server and cannot contain it. |
| Transcript text not logged by default | `AppLog` records events/counts/errors, not transcript lines. MCP tool log records tool names + short args only. |

## What DOES leave the machine (only when you enable it)
When a real-time voice conversation is enabled (`agent.realtime.voiceMode != off` + `enabled=true`):

- **All modes:** transcript text of the rolling window, your context-pack markdown, and your
  spoken turns (as text) go to OpenAI under your API key, and OpenAI returns synthesized voice audio.
- **`hybrid`:** text only — your microphone audio is transcribed locally and never sent.
- **`pushToTalk` / `continuous`:** your **microphone** audio is streamed to OpenAI (requires
  `sendAudio=true`). Meeting/system audio is never sent.
