# Plan: Stop speaker over-splitting & reduce mis-hearings

> **Navigate by symbol, not by line.** Line numbers below were verified against the
> working tree on 2026-07-14, but they drift as the files change. When a reference looks
> off by a few lines, trust the quoted symbol/method name — that's the anchor.

## Context

Two field-reported problems in the live meeting pipeline:

1. **Speaker over-splitting** — the same person is labelled "Speaker 2", "Speaker 3",
   "Speaker 4" across a meeting. Verified root cause: `SessionSpeakerRegistry.ResolveLabel`
   (`src/LocalTranscriber.Engine/SessionSpeakerRegistry.cs:45`) is a greedy nearest-neighbour
   matcher that (a) compares each new embedding to the **best single stored sample**, no
   averaging (`:53-58`); (b) uses a hardcoded `0.60` cosine threshold (`:24`) too high for
   same-speaker/short-utterance variation; (c) freezes each speaker's reference set at 10
   embeddings (`:64`); and (d) is not wired to config — `new SessionSpeakerRegistry()` is
   constructed with no args (`RealTranscriptionEngine.cs:126`, in `StartAsync`), so it always
   uses `0.60`. Whenever a genuine same-speaker chunk scores below `0.60`, a brand-new
   "Speaker N" is minted (`:69`). Diarization also re-clusters every ~10 s window in isolation
   (cluster ids are chunk-local, per the class doc `:12-17`), so cross-chunk identity rests
   entirely on this fragile matcher.

2. **Mis-hearing (wrong words)** — prime cause is the transcription model tier:
   `AppConfig.WhisperModelPath` defaults to `ggml-base.en.bin`
   (`src/LocalTranscriber.Shared/AppConfig.cs:7`), the second-smallest Whisper model.
   Compounding: the meeting path never sets a language, so it runs `WithLanguageDetection()`
   against an English-only model (`WhisperCppTranscriptionService.cs:44-51`;
   `EngineFactory.CreateSessionOptions` never assigns `Language`, `:43-55`); no decoding tuning
   (default greedy, no beam search, no thread count, no prompt); fixed ~10 s time-based windows
   with no VAD plus a crude peak-amplitude silence gate (`RealTranscriptionEngine.cs:271`,
   `Peak(window) < 0.015`).

**Intended outcome:** one stable label per real person for the duration of a session, and
markedly fewer wrong words — while keeping the hard rule that transcription stays fully local.

### Decisions (confirmed with user)

- Whisper model → `small.en` (large accuracy jump over base, still real-time on CPU).
- Speaker fix = **logic only**, keep the current NeMo TitaNet-small embedding model (preserves
  already-enrolled speakers; no re-enrolment, no vector-dimension break).
- A few seconds of latency is acceptable → favour accuracy (beam search + VAD are on the table).

### Threshold landscape (verified, don't conflate)

- `SpeakerMatchThreshold=0.72` / `SpeakerUncertainThreshold=0.62` (`AppConfig.cs:12-13`) feed
  `SpeakerRecognitionService` for **cross-session, SQLite-enrolled** speakers, wired via
  `EngineFactory.cs:26-27`. These stay as-is.
- The **session registry** (unnamed, in-meeting speakers) uses its own separate `0.60`. This plan
  adds new config for it — named to avoid collision with the above.

---
## Part A — Transcription accuracy

### A1. Upgrade the default model to `small.en`
- `AppConfig.cs:7`: default `WhisperModelPath = "models/whisper/ggml-small.en.bin"`.
- `scripts/setup.ps1`: download `ggml-small.en.bin` (~466 MB) from
  `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin`. Keep the existing
  base download block as a commented/optional fallback so low-spec machines can switch back via
  config.

### A2. Set the language explicitly (stop auto-detect on a `.en` model)
- Add `string Language = "en"` to `AppConfig.cs`.
- `EngineFactory.CreateSessionOptions` (`:43-55`): set `Language = config.Language`. The field
  already exists on `TranscriptionSessionOptions` (`:16`) but is never assigned.
  `RealTranscriptionEngine.TranscribeAsync` (`:412`) already forwards `_options.Language`, so this
  one assignment routes `WithLanguage("en")` instead of `WithLanguageDetection()`.

### A3. Decoding tuning in the shared transcription service
Extend `TranscriptionRequest` (`src/LocalTranscriber.AI/TranscriptionModels.cs`) with optional
`BeamSize`, `Threads`, `Prompt`, and apply them in `WhisperCppTranscriptionService` (`:43-58`):
- `.WithBeamSearchSamplingStrategy()` at beam size 5 — replaces the default greedy decode.
- `.WithThreads(n)` where `n` defaults to `max(1, Environment.ProcessorCount - 1)`.
- `.WithPrompt(prompt)` when a non-empty `InitialPrompt` is configured (meeting vocabulary: names,
  product/jargon terms).

**API verified present** in the installed `Whisper.net 1.9.1` (`WithBeamSearchSamplingStrategy`,
`WithThreads`, `WithPrompt`, `WithGreedySamplingStrategy` all in the package XML docs). Confirm exact
builder chaining at implementation time.

Populate these from `_options` in `RealTranscriptionEngine.TranscribeAsync`. This keeps the one
shared engine contract (CLI / voice paths get the same tuning through the same request type).

New config on `AppConfig.cs`: `WhisperBeamSize = 5`, `WhisperThreads = 0` (0 = auto),
`InitialPrompt = ""`. Thread through `TranscriptionSessionOptions` and
`EngineFactory.CreateSessionOptions`.

### A4. VAD-gated transcription (Whisper.net built-in Silero VAD)
`Whisper.net 1.9.1` ships VAD (Silero ggml model) — **verified** (extensive `Vad`/`VAD` coverage in
the package XML docs). Use it so whisper transcribes only detected speech regions within a window,
cutting the silence/boundary hallucinations the code already comments on
(`RealTranscriptionEngine.cs:273`).
- `setup.ps1`: download the Silero VAD ggml model into `models/whisper/`.
- `AppConfig`: `EnableVad = true`, `VadModelPath = "models/whisper/ggml-silero-vad.bin"`.
- Wire VAD into the builder in `WhisperCppTranscriptionService` when enabled and the model file
  exists; otherwise skip gracefully (feature-detect, no hard failure).
- Keep the cheap `Peak < 0.015` pre-gate as a first-pass filter; secondary once VAD runs.

Chunk length stays at ~10 s. With the latency budget this is fine; revisit to 12–15 s only if
boundary errors persist after A1–A4.

---
## Part B — Speaker labelling stability (logic only, keep TitaNet-small)

### B1. Rewrite `SessionSpeakerRegistry` to centroid matching (the core fix)
File: `src/LocalTranscriber.Engine/SessionSpeakerRegistry.cs`.
- Maintain a **running centroid per speaker**: `sumVector` + `count`, `centroid = sum / count`
  (incremental, O(1) memory). Match a new embedding against each speaker's centroid (cosine), not
  against the best individual sample.
- **Centroid-poisoning brake (required).** Update the centroid **only on a confident match**
  (similarity ≥ `assignThreshold`). Gray-zone assignments (see below) still get the label but
  **must not** fold into the mean — otherwise one wrong merge permanently drags a speaker's centroid
  and compounds for the rest of the meeting, silently merging two people. This brake is not optional;
  it's what keeps the lowered threshold safe.
- Keep a bounded list of raw embeddings (cap ~20) only to feed enrolment in
  `NameSessionSpeakerAsync` — decoupled from matching, so the cap no longer destabilises identity.
- **Lower + configurable thresholds with hysteresis:**
  - `assignThreshold` (default `0.50`): best centroid similarity ≥ this → assign **and update centroid**.
  - `newSpeakerThreshold` (default `0.40`): mint a new "Speaker N" only when best similarity is below
    this.
  - Scores in the `0.40–0.50` band → assign to the nearest existing speaker (bias against splitting),
    but **do not update the centroid** (the brake above). Different-speaker cosine is typically < 0.4
    for TitaNet; same-speaker often 0.5–0.7.
- Constructor takes both thresholds; wire from config at the registry construction site
  (`RealTranscriptionEngine.cs:126`) from `_options`.

### B2. Wire the thresholds through config
- `AppConfig`: `SameSpeakerThreshold = 0.50`, `NewSpeakerThreshold = 0.40`.
- Add matching fields to `TranscriptionSessionOptions`; set in `EngineFactory.CreateSessionOptions`;
  consume in `StartAsync`.
- **Document the naming split at the fields** to avoid future confusion: `SameSpeakerThreshold` /
  `NewSpeakerThreshold` gate **in-session** clustering (against session centroids), while
  `SpeakerMatchThreshold` (0.72) / `SpeakerUncertainThreshold` (0.62) gate **cross-session**
  recognition of SQLite-**enrolled** speakers (wired via `EngineFactory.cs:26-27`). They are
  independent knobs on different code paths — do not conflate them.

### B3. Average embeddings per cluster before matching (bounded)
File: `RealTranscriptionEngine.ProcessSystemWindowAsync` (`:331-335`). Today each diarized cluster is
resolved from its single longest segment (`var longest = cluster.OrderByDescending(...).First()`).
Instead, extract embeddings from that cluster's qualifying segments (≥ 700 ms) and average them into
one robust cluster embedding before `ResolveLabel`.
- **Cost cap (required):** average **at most ~3 segments** per cluster (longest-first). Each extra
  segment is another `ExtractEmbeddingAsync` (sherpa) call, and everything is serialized behind the
  single transcription `SemaphoreSlim` (`WhisperCppTranscriptionService.cs:12`); uncapped averaging
  multiplies per-window CPU on top of small.en + beam + VAD.
- Fall back to the longest segment when only one qualifies.

### B4. Improve the short-segment fallback (resolve at the caller, not in the registry)
`SessionSpeakerRegistry.FallbackLabel` (`:76-82`) returns the **last-created speaker**, which
misattributes short interjections and compounds any earlier over-split. `FallbackLabel()` has no
cluster context and is invoked from inside `ResolveSpeakerAsync` (`:373`) precisely because that
cluster's own segment is too short — so "inherit the overlapping cluster" cannot live in that method.

Fix at the caller instead:
- In `ProcessSystemWindowAsync` / `AssignSpeaker` (`:341-367`), when a segment has no usable
  embedding, assign it the label of the diarized cluster it most overlaps (the overlap machinery in
  `AssignSpeaker` already exists), falling through to the window's dominant resolved cluster.
- Use a neutral unknown label only when nothing resolves at all — never "whoever was minted last".
- Keep the 700 ms embedding gate (short clips genuinely produce bad embeddings).

Cross-session enrolled thresholds (`0.72`/`0.62`) stay as-is; revisit only if testing shows enrolled
speakers being missed.

---
## Config changes summary (`AppConfig.cs`)

| Field | Old | New | Purpose |
|---|---|---|---|
| `WhisperModelPath` | `ggml-base.en.bin` | `ggml-small.en.bin` | A1 accuracy |
| `Language` | — | `"en"` | A2 stop auto-detect |
| `WhisperBeamSize` | — | `5` | A3 beam search |
| `WhisperThreads` | — | `0` (auto) | A3 CPU threads |
| `InitialPrompt` | — | `""` | A3 domain vocab |
| `EnableVad` / `VadModelPath` | — | `true` / silero path | A4 VAD |
| `SameSpeakerThreshold` | — | `0.50` | B1/B2 assign gate (session) |
| `NewSpeakerThreshold` | — | `0.40` | B1 new-speaker gate (session) |

## Files to modify

- `src/LocalTranscriber.Shared/AppConfig.cs` — new config fields.
- `src/LocalTranscriber.Engine/SessionSpeakerRegistry.cs` — centroid rewrite + poisoning brake (B1).
- `src/LocalTranscriber.Engine/RealTranscriptionEngine.cs` — pass thresholds at registry construction,
  bounded cluster-embedding averaging (B3), forward decode params, caller-side fallback (B4).
- `src/LocalTranscriber.Engine/EngineFactory.cs` + `TranscriptionSessionOptions.cs` — plumb new options.
- `src/LocalTranscriber.AI/WhisperCppTranscriptionService.cs` + `TranscriptionModels.cs` —
  beam / threads / prompt / VAD.
- `scripts/setup.ps1` — download `ggml-small.en.bin` + Silero VAD model.

## Verification

1. `dotnet build` then `dotnet test` — all existing tests green.
2. **New unit tests for `SessionSpeakerRegistry`** (add to the engine/speakers test project, or create
   one) with synthetic embeddings proving: (a) repeated same-speaker vectors with realistic jitter stay
   one label; (b) two clearly different vectors get two labels; (c) thresholds are honoured from the
   constructor; (d) **the poisoning brake** — a gray-zone (0.40–0.50) assignment does not move the
   centroid (feed a borderline vector, assert the centroid is unchanged and a later clean same-speaker
   vector still matches). This directly guards the reported bug and the B1 fix.
3. `./scripts/setup.ps1 -DownloadModels` to fetch small.en + Silero VAD.
4. Real end-to-end run via `./scripts/run-app.ps1` (or F5) with a 2–3 person meeting through system
   audio + headphones: confirm each real person keeps one label across the whole session and spot-check
   transcript wording. Watch with `./scripts/tail-logs.ps1`.
5. A/B the transcript against a `base.en` run of the same audio to sanity-check the accuracy gain.

## Risks / notes

- Confirm exact `Whisper.net 1.9.1` builder chaining for beam search and VAD at implementation time
  (APIs verified present in the package; precise signatures to check against the installed assembly).
- `small.en` + beam search + VAD + B3 averaging raises per-window CPU; acceptable under the "few
  seconds" latency budget, but watch the window queue on a low-core laptop (transcription serialised
  behind one `SemaphoreSlim`). If it lags: drop beam size to 3, tune `WhisperThreads`, or reduce the
  B3 segment cap first.
- Threshold defaults (`0.50` / `0.40`) are research-informed starting points for TitaNet-small and are
  now config-tunable; the real-meeting test (step 4) settles final values. The hysteresis band trades a
  little under-splitting risk for stability — the poisoning brake is what keeps that trade safe.
- Larger model download changes first-run setup time; the `-DownloadModels` gate stays opt-in.
