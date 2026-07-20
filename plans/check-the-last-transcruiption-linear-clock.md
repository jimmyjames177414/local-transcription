# Improving transcription + speaker accuracy (vs Google Meet captions)

## Context

Jim compared LocalTranscriber's capture of the 2026-07-20 GymSpots standup
(`output/transcripts/session-20260720-161012.txt`) against Google Meet's caption export
(`~/Downloads/7.20.2016-standup-transcription.md`) and asked, honestly, how to make it more
accurate in **what it hears** and **who is speaking**. This plan is the diagnosis + a staged
fix, grounded in the actual pipeline code and config.

### Honest assessment
- **Content is mostly right** — you can follow the whole meeting from LocalTranscriber's
  output. It even names Ed / Anuj / Ricky / Jim correctly part of the time.
- **But it's noisy** in three visible ways: (1) words duplicated at every ~5s boundary,
  (2) ~12 spurious `Speaker N` labels + pervasive `possibly X`, often flipping mid-sentence,
  (3) domain-word errors ("MCP"→"NCP", "kanban"→"campaign", "gatekeeper"→"get keeper").
- **The speaker comparison is not apples-to-apples.** Google's clean per-name labels come
  from Meet knowing each *participant's* identity (separate streams), not from voice
  diarization. LocalTranscriber receives the entire call as **one mixed `systemAudio`
  loopback stream** (every line in the .jsonl is `source:systemAudio`) and must separate 4
  voices by embedding — the hardest possible input. So we will never trivially match Meet on
  labels, but we can get much closer than we are.

## Evidence (concrete symptoms)

Boundary duplication + mid-word cuts (from the .txt):
- `...how many reps if you do at 135 and then I was just trying to mess` / next line
  `just trying to mess around to seek in expectations` — phrase repeated.
- `135`, `highlighted`, `see some of the issues` all echoed across two lines.

Spurious speakers (from the .jsonl): enrolled speakers carry GUID ids
(`ec7152…`=Ed, `4bb1d1…`=Anuj); everything unmatched becomes an ephemeral
`session_speaker_1/3/5/6/8/9/11/12/14`. A single utterance splits Ed → Speaker 1 → Speaker 3.

## Current configuration (`output/config.json`) + how it flows

| Key | Value | Note |
|-----|-------|------|
| `whisperModelPath` | `ggml-base.en.bin` | **base.en** = weakest English tier. (Default discrepancy: `AppConfig.cs:7` says `small.en`, `AppPaths.cs:32-34` fresh-install fallback says `base.en`; Jim's file pins base.) `setup.ps1:15-34` already fetches `small.en`. |
| `chunkSeconds` | `5` | window = flush trigger, by **buffer size** not timer (`AudioWindowBuffer.cs:69-74`). Advance = 5−0.5 = 4.5s/window. |
| `overlapMs` | `500` | trailing 500ms re-fed to next window (`AudioWindowBuffer.cs:79-91`) → boundary audio transcribed twice. |
| `flushIntervalMs` | `1000` | **DEAD key** — no reader anywhere in code. |
| `initialPrompt` | `""` | no domain/name priming (`WhisperCppTranscriptionService.cs:82-85`). |
| `whisperBeamSize` | `5` | fine. |
| `enableVad` | `true` (silero v5.1.2) | **binary skip gate only** (`WhisperCppTranscriptionService.cs:44-55`), does not cut window boundaries. |
| `speakerMatchThreshold` | `0.75` | confident enrolled-match cutoff. |
| `speakerUncertainThreshold` | `0.62` | below → drops to `Speaker N`. |
| `sameSpeakerThreshold` / `newSpeakerThreshold` | `0.5` / `0.4` | in-session centroid stitch floors (`SessionSpeakerRegistry.cs:67`). |

Note a latent inconsistency: the "possibly" render cutoff is **hardcoded 0.72**
(`TranscriptFormatting.cs:11`) and ignores config's 0.75 — so a 0.73 match renders as a
confident name even though recognition treated it as uncertain.

## Root-cause analysis

### A. ASR accuracy — `src/LocalTranscriber.AI/WhisperCppTranscriptionService.cs` (Whisper.net 1.9.1)
1. **Model too small.** `base.en` is the dominant driver of proper-noun/jargon errors.
   `small.en` is already downloaded; `medium.en` is better still (slower).
2. **No `initialPrompt`.** Empty by default → nothing biases decoding toward the meeting's
   names + vocabulary. With a fresh `WhisperProcessor` built per window
   (`WhisperCppTranscriptionService.cs:92`), `condition_on_previous_text` is effectively off
   across windows, so the initial prompt is the *only* lever for consistent domain vocab.
3. **Fixed windowing + un-deduped 500ms overlap.** `AudioWindowBuffer.cs:69-91`. The only
   post-merge guard drops a line only on **exact full-string** match to the previous line for
   that source (`RealTranscriptionEngine.cs:341-349`) — partial-phrase overlaps always slip
   through. No temperature-fallback / suppress-tokens / logprob thresholds are set.
4. **Late resampling (not an accuracy bug, but wasteful):** capture is device-native
   (48kHz stereo float), written full-rate to a temp WAV, then resampled to 16kHz mono on
   read (`WavSampleReader.cs:11-42`). Whisper does get correct 16kHz mono.

### B. Diarization / speaker ID — models: pyannote-segmentation-3.0 + **NeMo TitaNet-small**
Orchestrator `src/LocalTranscriber.Engine/SpeakerLabeler.cs`.
5. **Per-window diarization, stitched by a weak centroid registry.** Diarization runs
   independently on each 5s window (`RealTranscriptionEngine.cs:299-300` → `SpeakerLabeler.cs:49`);
   cluster ids are chunk-local. Cross-window continuity is *only* the `SessionSpeakerRegistry`
   centroid match at **0.50 assign / 0.40 new** floors (`SessionSpeakerRegistry.cs:67,110,116,123`).
   A returning voice whose averaged embedding dips under 0.40 mints a fresh `Speaker N` —
   no cap, no re-merge pass → `Speaker 1..14`.
6. **Wide gray band + weak embedding model.** Tiers: `≥0.75` confident / `0.62–0.75` →
   `possibly` / `<0.62` → `Speaker N` (`SpeakerRecognitionService.cs:56`). TitaNet-**small**
   cosine scores on short (≥700ms, up to 3, averaged — `SpeakerLabeler.cs:126-129`) segments
   from compressed loopback commonly land in the gray band → pervasive `possibly` and
   name↔number flip-flop.
7. **Short chunks hurt clustering.** pyannote-3.0 clusters better on ≥10s of context; 5s
   gives it half. Also, `defaultLabel` becomes literal **"Unknown"** whenever a window has ≥2
   clusters but a segment is unembeddable (`SpeakerLabeler.cs:75-85`) — another flip source.
8. **Time-overlap alignment across a double-transcribed boundary** (`AssignSpeaker`,
   `SpeakerLabeler.cs:92-111`) lets the same boundary phrase surface under two speakers.

## Recommended approach (staged)

**Everything here stays live and 100% local/free** — no cloud, no cost. The live constraint
is *not* what's hurting accuracy; the small model, empty prompt, a dedup bug, and per-window
diarization are. Only the heaviest models (`medium`/`large`) and true whole-recording
diarization would need an offline pass, and those are deliberately out of scope for now.

**Recommendation: Tier 1 + Tier 2, done in two stages.** Apply Tier 1 (config) first, re-run
one real standup, and look at it — that measurement decides whether Tier 3 is ever worth
building. But plan on Tier 2 regardless: the two most visible complaints (duplicated words,
speaker flip-flop) are **code** problems that config alone cannot fix. Tier 3 (online global
speaker tracking + stronger embedding model) is intentionally deferred — don't build the big
thing before Tier 2's result proves it's needed.

**Highest single lever:** enroll clean 20–30s voice samples for Ed/Anuj/Ricky
(CLI `speakers enroll`). Recognition is currently matching against embeddings grabbed
mid-meeting; strong references + `small.en` + the prompt likely get most of the way before
any smoothing code runs.

### Tier 1 — config-only, high impact, no code
- **Switch model to `small.en`** (already on disk) in `output/config.json` →
  `whisperModelPath`. Fixes many domain-word errors immediately.
- **Populate `initialPrompt`** with the recurring names + vocabulary, e.g.:
  `"Standup with Anuj, Ricky, Ed, and Jim. Topics: staging, dev branch, main, gatekeeper,
  pull request, kanban board, Bluetooth, MCP, Miro, Slack, progressive overload, test
  harness."`
- **Raise `chunkSeconds` to 8–10 and drop `overlapMs` to 0–200.** Longer windows =
  better whisper context *and* better pyannote clustering; less overlap = fewer duplicate
  phrases. (Trades ~5s more latency.)
- **Widen the known-speaker band:** lower `speakerMatchThreshold` toward `0.62–0.66` so
  enrolled Ed/Anuj/Ricky stop rendering as `possibly`. (Also align the hardcoded 0.72 render
  cutoff — see Tier 2.)
- Re-run a session and diff against the Meet export to confirm.

### Tier 2 — code, removes the structural noise
- **Word-boundary-aware overlap dedup** (or skip re-transcription of the overlap region using
  whisper segment timestamps) in `RealTranscriptionEngine.EmitAsync`
  (`RealTranscriptionEngine.cs:341-349`) — eliminates duplicated boundary phrases.
- **Make the "possibly" cutoff read config** instead of the hardcoded `0.72`
  (`TranscriptFormatting.cs:11`), so threshold tuning actually takes effect.
- **Temporal speaker smoothing / hysteresis:** prefer the previous window's speaker unless the
  new embedding clearly matches a different enrolled speaker; stop `defaultLabel` from
  emitting bare "Unknown" mid-turn (`SpeakerLabeler.cs:75-85`).
- **Enroll clean reference samples** for Ed/Anuj/Ricky (CLI `speakers enroll --name … --audio …`,
  `SpeakerCommands.cs:111-140`) so recognition has strong centroids to match against.

### Tier 3 — the real "who's speaking" fix (larger)
- **Session-level diarization pass** instead of per-window: cluster the whole meeting's audio
  once, then map each cluster centroid to an enrolled speaker. Replaces the fragile
  per-window + 0.4 centroid stitch. Best done as a **post-session re-process** of the retained
  full-session audio (also unlocks a bigger whisper model + cross-window context for ASR).
- **Upgrade the embedding model** from TitaNet-small to a stronger sherpa-onnx speaker model
  (e.g. 3D-Speaker ERes2Net / WeSpeaker ResNet) for more separable embeddings → narrower gray
  band. Add to `scripts/setup.ps1` model downloads.

## Files to modify (by tier)
- **Tier 1:** `output/config.json` only.
- **Tier 2 (as built):** new `src/LocalTranscriber.Engine/TranscriptStitcher.cs` (word-level
  overlap dedup) wired into `RealTranscriptionEngine.cs` (`EmitAsync`); config-driven "possibly"
  cutoff threaded via `TranscriptionSessionOptions.cs` + `EngineFactory.cs` →
  `PlainTextTranscriptWriter` (`TranscriptFormatting.cs` was already parameterized); speaker
  smoothing entirely within `SpeakerLabeler.cs` (`SessionSpeakerRegistry.cs` was not needed);
  `AppConfig.cs` InitialPrompt documented. Tests: `TranscriptStitcherTests.cs`.
- **Tier 3:** `SpeakerLabeler.cs` / a new session-diarization path, `SherpaOnnxServices.cs`,
  `scripts/setup.ps1` (new embedding model), and a re-process entry point in the
  Engine/CLI. **Prerequisite to verify:** whether full-session audio is retained today
  (temp WAVs are per-window in `RealTranscriptionEngine.WriteTempWav:379-385`).

## Verification
- **A/B on the same meeting:** capture a short multi-speaker session (or re-run retained
  audio), diff the `.txt` against the Meet export. Success = far fewer boundary duplications,
  fewer spurious `Speaker N`, fewer `possibly`, correct domain words.
- `dotnet test` stays green (133 tests).
- Manual: run the App on a live multi-speaker call and eyeball the transcript tab.
