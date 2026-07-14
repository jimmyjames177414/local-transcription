# Plan: Real-time voice conversation agent (replace suggestions)

## Context

Today the agent's "voice" is Windows SAPI text-to-speech (`WindowsTtsAgentVoiceOutput`)
reading parsed JSON suggestions aloud — a robotic voice that sounds nothing like the
OpenAI realtime model. Root cause: `OpenAIRealtimeMeetingAgentProvider` requests
`output_modalities = ["text"]` only (line 172) and the whole agent is built around a
request/response suggestion pipeline (`IMeetingAgentProvider.AnalyzeAsync` → JSON
suggestions → policy → list/TTS).

The user's actual goal was never a stream of typed suggestions — it was to **talk to the
AI in real time during a meeting** (natural voice, barge-in), grounded in the code-context
packs and the live transcript so the conversation is relevant. The text-suggestion surface
was a stand-in and will be removed.

The prior "no audio ever leaves the machine" hard rule is **explicitly waived by the user**
for this feature (offline transcription itself is unchanged and still fully local).

Decisions made with the user:
- Deliver in slices: **playback + hybrid first**, then pushToTalk, then continuous+barge-in.
- **Remove the suggestion output surface now**, keeping shared infrastructure the voice
  feature reuses (context composition, transcript tailer, realtime transport, secrets, config).
- Three modes selectable via `agent.realtime.voiceMode` so the user can try each and pick the best UX.

## The three voice modes (all produce natural OpenAI audio output)

| Mode | Input path | Audio leaving machine | Barge-in |
|---|---|---|---|
| `hybrid` | hold-to-talk → local whisper STT → send **text** | none (text only) | n/a |
| `pushToTalk` | hold key → stream mic PCM (`input_audio_buffer.append`) → commit on release | your voice only | n/a |
| `continuous` | mic streamed continuously + server VAD (`turn_detection`) | your voice, continuous | yes |

Verified GA Realtime protocol (`wss://api.openai.com/v1/realtime?model=...`, `Authorization: Bearer`):
- `session.update`: `session.type="realtime"`, `output_modalities=["audio","text"]`,
  `session.audio.output.voice` (e.g. `marin`), `session.audio.output.format={type:"audio/pcm",rate:24000}`,
  `session.audio.input.format={type:"audio/pcm",rate:24000}`,
  `session.audio.input.turn_detection={type:"server_vad",threshold,silence_duration_ms,create_response:true}` or `null`.
- send audio: `input_audio_buffer.append {audio:<base64 pcm16>}`; if VAD off: `input_audio_buffer.commit` + `response.create`.
- receive audio: `response.output_audio.delta` (base64 pcm16), `response.output_audio_transcript.delta/done` (captions).
- barge-in: server `input_audio_buffer.speech_started` → client `response.cancel` + `conversation.item.truncate {item_id, content_index:0, audio_end_ms}`.
- context inject: `conversation.item.create {input_text}` (no `response.create` for silent grounding) + `session.update` for instructions.

## Architecture

**New project `LocalTranscriber.Voice`** (keeps the lean `Agent` project audio-free).
References: `Shared`, `Agent` (transport + reconnect pattern), `Audio` (playback + mic),
`AI` (whisper for hybrid), `Context` (grounding), `Storage`. `App` and `CLI` add a
reference to `Voice`.

The voice session runs **standalone**, not through `IMeetingAgentProvider` and not inside
`MeetingAgent`. It owns its websocket, three concurrent loops (send-audio / receive-events /
playback), and its lifecycle.

Core types (in `Voice`):
- `IRealtimeVoiceConversation : IAsyncDisposable` — `State`, `AssistantTextAvailable` event
  (captions), `StateChanged`, `StartAsync(RealtimeVoiceOptions)`, `PushToTalkDown/Up`, `StopAsync`.
- `RealtimeVoiceSession` — one class, all three modes; branch only in three spots
  (`ConfigureTurnDetection`, `OnPushToTalkUp`, `OnUserSpeechStarted`).
- `RealtimeVoiceOptions`, `enum RealtimeVoiceMode { Off, Hybrid, PushToTalk, Continuous }` (parse from config string, ignore-case, mirroring `AgentMode` parsing).
- `RealtimeVoiceEventMapper` — parses audio server events (`OutputAudioDelta`, `AudioTranscriptDelta/Done`, `SpeechStarted`, `ResponseDone`, `Error`).
- `RealtimeVoiceFactory.Create(AppConfig, SecretsService)` — resolves key, parses mode, returns session or a human-readable notice (parallel to `AgentProviderFactory`).

Audio building blocks (in `LocalTranscriber.Audio`, beside `WavDebugWriter`/capture services):
- `IAgentAudioOutput` + `NAudioAgentAudioOutput` (`BufferedWaveProvider` + `WaveOutEvent`,
  `new WaveFormat(24000,16,1)`) + `NoOpAgentAudioOutput`. Methods: `EnqueuePcm16`,
  `EnqueueBase64`, `Stop()` (barge-in hard stop), `Flush()` (turn boundary),
  `PlayedMilliseconds` (for accurate `audio_end_ms` on truncate), `IsPlaying`.
- `IAgentMicStream` + `ResamplingAgentMicStream` — a **dedicated** `MicrophoneCaptureService`
  (separate from the transcription mic), converting each `AudioChunk` to 24kHz PCM16 mono via
  the `WdlResamplingSampleProvider` + `StereoToMonoSampleProvider` chain proven in
  `src/LocalTranscriber.AI/WavSampleReader.cs` (target 24000 instead of 16000, emit
  little-endian Int16 bytes). Add `const int RealtimeSampleRate = 24000;`.
- `PcmResampler` static helper for the float→PCM16 conversion (unit-testable, deterministic).

Threading: one `CancellationTokenSource`; a single `SemaphoreSlim` serializes all
`transport.SendAsync` (socket not safe for concurrent writes); WASAPI callbacks hand frames
to a bounded `Channel<byte[]>` drained by a send pump (keeps the capture thread free).
Disposal mirrors `MeetingAgent.StopAsync` (cancel → stop capture awaiting `RecordingStopped`
→ stop/dispose `WaveOutEvent` → dispose transport → await loops with a bounded `WaitAsync`).

Grounding: at start, reuse `ContextComposer.ComposeAsync(query)` → `session.update.instructions`,
and seed a first `conversation.item.create {input_text}` with the current
`RollingTranscriptWindow.Snapshot()` rendered as `[HH:mm:ss] Speaker (source): text`. A
grounding timer (default 15s, `agent.realtime.groundingIntervalSeconds`) injects only **new**
transcript lines (dedup via the existing `_sentEventIds`/`TranscriptEventDeduplicator` pattern)
as `conversation.item.create` **without** `response.create`, so the model absorbs meeting
context silently and only speaks when addressed.

## Config schema (`src/LocalTranscriber.Shared/AgentConfig.cs`, `RealtimeAgentConfig`)

Add (do NOT reintroduce the removed `voiceOutputEnabled`):
- `string VoiceMode = "off";` (`off|hybrid|pushToTalk|continuous`)
- `string Voice = "marin";`
- `double VadThreshold = 0.5;`, `int VadSilenceMs = 500;`, `int VadPrefixPaddingMs = 300;`
- `string? InputAudioDeviceId`, `string? OutputAudioDeviceId`
- `int GroundingIntervalSeconds = 15;`
- Repurpose `SendAudio` as the explicit mic-audio consent flag required by `pushToTalk`/`continuous`
  (hybrid needs only `voiceMode != off`). Update its doc comment.
- `add enum RealtimeVoiceMode` in Shared.

Mirror keys into `output/config.json` (safe defaults: `voiceMode:"off"`, `sendAudio:false`) and
note them in `scripts/publish.ps1` example. `ConfigService.TrySet` handles nested dotted paths automatically.

## Phases

### Phase 1 — Groundwork (no behavior change; build stays green)
- Extract `IRealtimeTransport` + `ClientWebSocketRealtimeTransport` (currently defined *inside*
  `src/LocalTranscriber.Agent/OpenAI/OpenAIRealtimeMeetingAgentProvider.cs`) into a standalone
  `src/LocalTranscriber.Agent/OpenAI/RealtimeTransport.cs` so they survive suggestion removal.
- Create the `LocalTranscriber.Voice` project + add it to the solution and to `App`/`CLI` refs.
- Add the config schema above.
- Add `IAgentAudioOutput`/`NAudioAgentAudioOutput`/`NoOpAgentAudioOutput`, `IAgentMicStream`/
  `ResamplingAgentMicStream`, `PcmResampler` in `Audio`, with a deterministic `PcmResampler` unit test.

### Phase 2 — Remove the suggestion output surface (the "now" cleanup)
Keep shared infra: `ContextComposer`/`MarkdownContextPackService`, `TranscriptEventTailer`,
`RollingTranscriptWindow`, `TranscriptEventDeduplicator`, the extracted realtime transport,
`SecretsService`, `ConfigService`, `AppConfig`/`AgentConfig`, `TranscriptEvent`.

Remove (suggestion-specific):
- `AgentResponsePolicy.cs` + `ResponsePolicyTests.cs` (policy/cooldown/dedup/priority).
- Suggestion providers: `FakeMeetingAgentProvider`, `OpenAITextMeetingAgentProvider`,
  `OpenAIRealtimeMeetingAgentProvider`, `OpenAIResponseParser`, `AgentOneShot`, and the
  `MeetingAgent` suggestion loop (`AnalyzeLoopAsync`/`RunAnalysisAsync`/`AskAsync`/suggestion sink).
- Suggestion voice: `IAgentVoiceOutput`/`WindowsTtsAgentVoiceOutput`/`NoOpAgentVoiceOutput`,
  `AgentVoiceConfig`, `AgentProviderFactory.CreatePolicy`.
- Suggestion storage: `SqliteAgentSuggestionStore` + agent output md/jsonl writers.
- UI: the Suggestions `ListBox` and `SelectedSuggestion`/Dismiss/Copy/`ConsumeSuggestionsAsync`
  in `AgentPanelViewModel.cs` + `MainWindow.xaml` Agent tab.
- CLI: suggestion-oriented subcommands in `AgentCommands.cs` (`suggestions`, `ask`, `dismiss`,
  `summary`, `action-items`, `mode`, `start`/`start-fake`, `voice test/on/off`, `test-openai`,
  `test-realtime`) — retain only what the voice feature needs.
- Verify the MCP server (`LocalTranscriber.Mcp`) agent tools and update/remove accordingly
  (not yet explored — audit before deleting).
- Trim `AgentConfig` of now-dead fields; remove obsolete agent tests; keep the codebase green.

### Phase 3 — Voice first slice: playback + hybrid  ← user tries this
- Implement `RealtimeVoiceSession` with `turn_detection:null`, receive→playback, context grounding.
- Hybrid input: buffer held mic audio → temp WAV → `WhisperCppTranscriptionService.TranscribeAsync`
  (reuse the Engine's model-path resolution) → send `input_text` + `response.create`.
- WPF: new "Voice conversation" panel in the Agent tab — mode combo, Start/Stop voice,
  Hold-to-talk button (`PreviewMouseLeftButtonDown/Up` in `MainWindow.xaml.cs` → `PushToTalkDown/Up`),
  live assistant-transcript captions (via `AssistantTextAvailable` + existing `PostToUi`).
  Settings tab: `voiceMode`/`voice`/device combos (FilterNonSpeech binding pattern).
- CLI: `agent talk --transcript <path> --mode hybrid` streaming assistant captions to stdout;
  Ctrl+C cancellation.
- Tests: extend the `FakeRealtimeTransport` pattern — assert `session.update` audio config
  (voice, 24kHz formats, `turn_detection:null`), grounding injects new-lines-only without
  `response.create`, hybrid sends `input_text`+`response.create` (inject a fake transcriber),
  and a fake `IAgentAudioOutput` receives audio deltas. All headless (no device/network).

### Phase 4 — pushToTalk
- Wire `ResamplingAgentMicStream` → send pump → `input_audio_buffer.append` while key held;
  on release `input_audio_buffer.commit` + `response.create`. `turn_detection:null`.
- Tests: fake mic frames → assert `append` count + `commit`/`response.create` on key-up.

### Phase 5 — continuous + barge-in
- `session.update` `turn_detection:server_vad` from config; stream mic continuously; no manual commit.
- Barge-in: on `input_audio_buffer.speech_started` → `playback.Stop()`, capture
  `audio_end_ms = playback.PlayedMilliseconds`, then `response.cancel` + `conversation.item.truncate`.
- Tests: enqueue audio delta then `speech_started` → assert `Stop()` called and
  `response.cancel`+`conversation.item.truncate` (plausible `audio_end_ms`) sent.

## Reused code (do not reinvent)
- Transport + reconnect/backoff + `EnsureConnectedAsync` header/URI pattern — `OpenAIRealtimeMeetingAgentProvider.cs`.
- Resample chain — `src/LocalTranscriber.AI/WavSampleReader.cs`; WAV format handling — `src/LocalTranscriber.Audio/WavDebugWriter.cs`.
- Mic capture — `MicrophoneCaptureService` / `WasapiCaptureServiceBase` (+ `IsAvailable`/`HasEndpoint` for missing devices).
- Context grounding — `ContextComposer`/`MarkdownContextPackService`; live transcript — `TranscriptEventTailer` + `RollingTranscriptWindow`.
- Secrets — `SecretsService.ResolveOpenAIKey`; config — `ConfigService` (nested `TrySet`).
- Test seam — `FakeRealtimeTransport` in `tests/LocalTranscriber.Agent.Tests/RealtimeProviderTests.cs` (promote to a shared helper for `Voice.Tests`).

## Risks / must-handle
- **Mic bleed**: voice input hard-wired to `AudioSourceType.Microphone` only — never
  `SystemLoopbackCaptureService`. Meeting audio must never be streamed. Hybrid (text-only) is the default.
- **Echo/self-interrupt** (continuous): recommend headphones in UI copy; raise `server_vad.threshold`;
  rely on `conversation.item.truncate` to recover; optionally gate mic append while `State==Speaking`.
- **Truncate accuracy**: `audio_end_ms` from `WaveOutEvent` playback position (what was heard), not bytes enqueued.
- **Threading/disposal**: single send-lock; channel hop off the WASAPI callback; bounded shutdown.
- **Reconnect**: reuse exponential backoff; on reconnect re-send `session.update` + re-seed grounding.
- **Cost/latency**: continuous is most expensive/exposed; default `off`, then `hybrid`.
- **MCP audit**: confirm/adjust `LocalTranscriber.Mcp` agent tools during Phase 2 removal.

## Docs
- `docs/REALTIME_PROVIDER.md`: replace "text only"/`sendAudio must stay false`; document the three
  modes, VAD, barge-in, and the privacy waiver (audio leaves only on explicit opt-in, default off).
- `README.md` + `docs/PRIVACY.md`: amend the "no audio ever leaves the machine" hard rule to
  "transcription is always local; realtime voice is opt-in and sends audio only when enabled."

## Verification
- `dotnet build` + `dotnet test` green after every phase (App build requires the running app to be
  closed — it locks output DLLs).
- Confirm `Agent` still does not reference `Audio`; `Voice` references `Audio`/`AI`; `App`/`CLI` reference `Voice`.
- Config round-trips through `ConfigService` with safe defaults; `voiceOutputEnabled` not reintroduced.
- Headless tests cover session config per mode, grounding, hybrid text input, pushToTalk append/commit, barge-in — all via fakes.
- Manual (Windows, API key, headphones): `agent talk --mode hybrid` → hear the natural voice reply;
  then pushToTalk; then continuous with mid-reply interruption (barge-in stops playback). Use
  `LT_REALTIME_DEBUG=1` to confirm hybrid sends no `input_audio_buffer.append` (text only).
