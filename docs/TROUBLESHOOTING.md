# Troubleshooting

## "Whisper model not found"
Run `./scripts/setup.ps1 -DownloadModels`, or place `ggml-base.en.bin` at the path shown in the error, or fix `whisperModelPath` via `localtranscriber config set`.

## "Speaker model not found"
Same script downloads `models/speaker/segmentation.onnx` and `embedding.onnx`.

## No system audio captured
WASAPI loopback produces nothing while the system is silent — that is normal. Play audio and retry. Check the output device with `localtranscriber audio devices`.

## No microphone found / capture fails
- `localtranscriber audio devices` should list at least one input device.
- Windows Settings → Privacy → Microphone: allow desktop apps.
- Another app may hold the device in exclusive mode.

## Transcription is slow
base.en should run near real-time on a modern CPU. If chunks queue up (status shows growing lag), switch to the smaller model `ggml-tiny.en.bin` and update `whisperModelPath`.

## Everything is labeled "Me"
Your microphone is picking up the speakers. Wear headphones so the mic only hears you; remote voices then arrive only via the system-audio track where diarization and speaker memory run.

## Speaker labels are wrong
Expected sometimes — see accuracy notes in USAGE.md. Improve results by:
- enrolling speakers from clean solo samples (`speakers enroll`)
- raising `speakerMatchThreshold` (fewer false names, more `possibly`)
- setting the exact speaker count when diarizing files (`--num-speakers`)

## Weird lines like "[Music]" or "(engine revving)"
Whisper hallucinates descriptions on noise. The engine skips silent windows, but steady noise above the threshold still gets transcribed.

## Where are my files?
- Dev checkout: `./output/` (config, DB, logs, transcripts)
- Packaged: config/DB/logs under `%AppData%\LocalTranscriber\`, transcripts under `Documents\LocalTranscriber\Transcripts\`

## Agent shows "Using the offline fake provider"
The configured provider could not start — the message says why (not enabled, or no key). See docs/OPENAI_PROVIDER.md. The fake provider is keyword-based and intentionally dumb.

## Agent produces no suggestions
- Is a session writing the `.jsonl` the agent watches? `agent status` shows latest activity.
- SilentObserver still requires new transcript events; silence in → silence out.
- With placeholders in `context/`, the agent has no project knowledge — fill `codename-summary.md`.
- Some suggestions are stored but not shown (cooldown/low confidence/dismissed): `agent suggestions --include-dismissed`.

## Agent voice doesn't speak
`agent voice on`, then check: mode must be PrivateCoach or InterruptWhenImportant, priority ≥ High, and voice goes to the DEFAULT audio device — set your headphones as default. `agent voice test` bypasses everything except TTS itself.

## Realtime provider fails
`LT_REALTIME_DEBUG=1 localtranscriber agent test-realtime ...` dumps raw events. Transient corporate-proxy SSL failures happen — retry. See docs/REALTIME_PROVIDER.md.

## MCP server not responding in Claude
- Register with: `claude mcp add local-transcriber -- dotnet run --project src/LocalTranscriber.Mcp` (from repo root)
- The server is stdio-only; nothing should print to its stdout except protocol traffic.
- Tool calls are logged to `logs/mcp-tool-calls.log` under the data folder.
