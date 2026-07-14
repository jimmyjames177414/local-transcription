# OpenAI Realtime Voice

Optional real-time **voice conversation** with the AI over the OpenAI Realtime websocket (GA
API). You talk to the assistant during a meeting and it replies in a natural OpenAI voice,
grounded in your local code-context packs and the live transcript. This replaces the earlier
text-suggestion surface, which has been removed.

The session runs standalone (`LocalTranscriber.Voice`): it owns its websocket, audio playback,
and — for the streaming modes — microphone capture. Reconnects use exponential backoff and
re-send the session config + grounding.

## Privacy waiver

Offline transcription is unchanged and still fully local. Real-time voice is **opt-in** and is
the one feature that sends data to OpenAI:

- **Default is `off`** — no connection, nothing sent.
- **hybrid** sends **text only** (your speech is transcribed locally by whisper first); no audio
  leaves the machine.
- **pushToTalk / continuous** stream your **microphone** audio and therefore require explicit
  consent via `agent.realtime.sendAudio=true`.
- Meeting / system-loopback audio is **never** streamed — voice input is hard-wired to the
  microphone only.

## The three modes (`agent.realtime.voiceMode`)

| Mode | Input path | Audio leaving machine | Barge-in |
|---|---|---|---|
| `hybrid` | hold-to-talk → local whisper STT → send text | none (text only) | n/a |
| `pushToTalk` | hold key → stream mic PCM, commit on release | your voice only | n/a |
| `continuous` | mic streamed continuously + server VAD | your voice, continuous | yes |

All modes produce natural OpenAI audio output (`voice`, e.g. `marin`). `hybrid` is the safe
default to try first.

## Enable

```powershell
localtranscriber config set agent.realtime.enabled true
localtranscriber config set agent.realtime.voiceMode hybrid
localtranscriber agent talk --transcript "./output/transcripts/<file>.jsonl" --mode hybrid
```

For `pushToTalk` / `continuous` also set `agent.realtime.sendAudio true`. Key resolution matches
the previous providers (env var `OPENAI_API_KEY`, else `output/secrets.json`). In the WPF app the
controls live on the **Agent** tab (voice mode, Start/Stop voice, Hold-to-talk, live captions).

## Config (`agent.realtime`)

| Key | Default | Notes |
|---|---|---|
| `enabled` | false | opens the realtime connection |
| `model` | gpt-realtime-2.1-mini | |
| `voiceMode` | off | off \| hybrid \| pushToTalk \| continuous |
| `voice` | marin | OpenAI output voice |
| `vadThreshold` | 0.5 | server VAD sensitivity (continuous); higher = less sensitive |
| `vadSilenceMs` | 500 | silence before a turn ends (continuous) |
| `vadPrefixPaddingMs` | 300 | audio kept before detected speech (continuous) |
| `inputAudioDeviceId` | null | microphone device; null = default |
| `outputAudioDeviceId` | null | playback device; null = default |
| `groundingIntervalSeconds` | 15 | how often new transcript lines are injected silently |
| `sendAudio` | false | **consent** to stream mic audio (required by pushToTalk/continuous) |

## Grounding

At start the session composes instructions from your context packs
(`ContextComposer.ComposeAsync`) and seeds the current transcript window silently
(`conversation.item.create` with `input_text`, **no** `response.create`). A timer then injects
only new transcript lines on the same silent channel, so the model tracks the meeting and speaks
only when you address it.

## Barge-in (continuous)

When the server reports `input_audio_buffer.speech_started`, the session stops playback, captures
`audio_end_ms` from the actual playback position (what was heard, not what was enqueued), and
sends `response.cancel` + `conversation.item.truncate`. Use headphones to avoid the model hearing
its own voice; raise `vadThreshold` if it self-interrupts.

## Protocol notes (GA, learned live)

- `session.update` needs `session.type="realtime"`, `output_modalities=["audio","text"]`,
  `session.audio.output.voice` + `format {type:"audio/pcm", rate:24000}`, and
  `session.audio.input.format` + `turn_detection` (`server_vad` for continuous, `null` otherwise).
- Audio in: `input_audio_buffer.append {audio:<base64 pcm16>}`; when VAD is off,
  `input_audio_buffer.commit` + `response.create`.
- Audio out: `response.output_audio.delta` (base64 pcm16); captions via
  `response.output_audio_transcript.delta/done`.
- No `OpenAI-Beta` header.

## Debugging

`LT_REALTIME_DEBUG=1` prints raw server events (and sent frames) to stderr. Use it to confirm
`hybrid` sends **no** `input_audio_buffer.append` (text only). From WSL, add
`WSLENV=LT_REALTIME_DEBUG/w`.
