# OpenAI Realtime Provider

Optional low-latency provider over the Realtime websocket (GA API). Same inputs and JSON suggestion contract as the text provider, but a persistent connection: instructions + context go once, then each analysis pass sends only transcript lines the connection hasn't seen. Reconnects with backoff and re-sends pending lines without duplicating suggestions.

**Text only.** `agent.realtime.sendAudio` exists in config but `true` is rejected — raw audio never leaves the machine. Voice output, if you enable it, is local Windows TTS (see AGENT.md), not realtime audio.

## Enable

```powershell
localtranscriber config set agent.provider realtime
localtranscriber config set agent.realtime.enabled true
localtranscriber agent test-realtime --transcript "./output/transcripts/<file>.jsonl"
```

Key resolution is identical to the text provider.

## Config (`agent.realtime`)

| Key | Default |
|---|---|
| `enabled` | false |
| `model` | gpt-realtime-2.1-mini |
| `transport` | websocket |
| `voiceOutputEnabled` | false (reserved) |
| `sendAudio` | false (must stay false) |

## Debugging

`LT_REALTIME_DEBUG=1` prints raw server events to stderr. Known GA behaviors handled: text arrives via `response.output_text.done` (response.done may carry empty content); replies are sometimes a bare JSON array; confidence occasionally comes back as a label — all normalized by the parser.

## Honest note

For text-only suggestions the realtime channel buys latency, not intelligence — the text provider is the workhorse. Realtime earns its keep if/when voice interaction is built on top.
