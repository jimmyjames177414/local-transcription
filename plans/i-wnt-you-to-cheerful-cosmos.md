# Enhance voice detection: stop speaker over-splitting & reduce mis-hearings

## Context

Two field-reported problems in the live meeting pipeline:

1. **Speaker over-splitting** — the same person is labelled "Speaker 2", "Speaker 3", "Speaker 4"
   across a meeting. Confirmed root cause: `SessionSpeakerRegistry.ResolveLabel`
   (`src/LocalTranscriber.Engine/SessionSpeakerRegistry.cs:45`) is a greedy nearest-neighbour
   matcher that (a) compares each new voice embedding to the **best single stored sample** (no
   averaging), (b) uses a **hardcoded 0.60** cosine threshold that is **too high** for
   same-speaker/short-utterance variation, (c) freezes each speaker's reference set at **10
   embeddings**, and (d) is **not wired to config** — `new SessionSpeakerRegistry()` at
   `RealTranscriptionEngine.cs:118` always uses 0.60, so `SpeakerMatchThreshold` never reaches it.
   Whenever a genuine same-speaker chunk scores below 0.60, a brand-new "Speaker N" is minted
   (`SessionSpeakerRegistry.cs:69`). Diarization also re-clusters every 10 s window in isolation
   (cluster ids are chunk-local), so cross-chunk identity rests entirely on this fragile matcher.

2. **Mis-hearing (wrong words)** — confirmed prime cause is the transcription model tier:
   `AppConfig.WhisperModelPath` defaults to `ggml-base.en.bin`
   (`src/LocalTranscriber.Shared/AppConfig.cs:7`), the second-smallest Whisper model. Compounding
   factors: the meeting path never sets a language so it runs `WithLanguageDetection()` against an
   English-only model (`WhisperCppTranscriptionService.cs:44-51`, `EngineFactory.cs` never sets
   `Language`); no decoding tuning (default greedy, no beam search, no thread count, no prompt);
   fixed 10 s time-based windows with no VAD (`AudioWindowBuffer.cs`) plus a crude peak-amplitude
   silence gate (`RealTranscriptionEngine.cs:252`).

**Intended outcome:** one stable label per real person for the duration of a session, and markedly
fewer wrong words — while keeping the hard rule that transcription stays fully local.

## Decisions (confirmed with user)

- **Whisper model → `small.en`** (big accuracy jump over base, still real-time on CPU).
- **Speaker fix = logic only**, keep the current NeMo TitaNet-small embedding model (preserves
  already-enrolled speakers; no re-enrolment, no vector-dimension break).
- **A few seconds of latency is acceptable** → favour accuracy (beam search + VAD are on the table).

Web research backs the approach: for online speaker ID, match against a **running centroid** (mean
of a cluster's embeddings) rather than a single noisy sample, and gate new-speaker creation with a
lower/hysteresis threshold; for whisper, model size is the dominant WER lever and VAD + a domain
`initial_prompt` reduce hallucination and boundary errors.

---

## Part A — Transcription accuracy

### A1. Upgrade the default model to `small.en`
- `AppConfig.cs:7`: default `WhisperModelPath = "models/whisper/ggml-small.en.bin"`.
- `scripts/setup.ps1:13-19`: download `ggml-small.en.bin` (~466 MB) from
  `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin`. Keep the
  existing base download block as a commented/optional fallback so low-spec machines can switch back
  via config.

### A2. Set the language explicitly (stop auto-detect on a `.en` model)
- Add `string Language = "en"` to `AppConfig.cs`.
- `EngineFactory.CreateSessionOptions` (`EngineFactory.cs:43-55`): set `Language = config.Language`
  (the field already exists on `TranscriptionSessionOptions` but is never assigned).
  `RealTranscriptionEngine.TranscribeAsync:393` already forwards `_options.Language`, so this alone
  routes `WithLanguage("en")` instead of `WithLanguageDetection()`.

### A3. Decoding tuning in the shared transcription service
Extend `TranscriptionRequest` (`src/LocalTranscriber.AI/TranscriptionModels.cs`) with optional
`BeamSize`, `Threads`, `Prompt`, and apply them in `WhisperCppTranscriptionService.cs:43-58`:
- `.WithBeamSearchSamplingStrategy()` at beam size 5 (verified available in Whisper.net 1.9.1;
  confirm exact builder chaining at implementation time) — replaces the default greedy decode.
- `.WithThreads(n)` where `n` defaults to `max(1, Environment.ProcessorCount - 1)`.
- `.WithPrompt(prompt)` when a non-empty `InitialPrompt` is configured (meeting-specific vocabulary:
  names, product/jargon terms) — a proven accuracy lever.
Populate these from `_options` in `RealTranscriptionEngine.TranscribeAsync`. This keeps the one
shared engine contract (CLI / voice paths get the same tuning through the same request type).

New config on `AppConfig.cs`: `WhisperBeamSize = 5`, `WhisperThreads = 0` (0 = auto), and
`InitialPrompt = ""`. Thread these through `TranscriptionSessionOptions` and
`EngineFactory.CreateSessionOptions`.

### A4. VAD-gated transcription (Whisper.net built-in Silero VAD)
Whisper.net 1.9.1 ships VAD via `WhisperVadFactory` (Silero ggml model). Use it so whisper
transcribes only detected speech regions within a window — this directly cuts the silence/boundary
hallucinations the code already comments on (`RealTranscriptionEngine.cs:254`).
- `setup.ps1`: download the Silero VAD ggml model into `models/whisper/`.
- `AppConfig`: `EnableVad = true`, `VadModelPath = "models/whisper/ggml-silero-vad.bin"`.
- Wire the VAD into the builder in `WhisperCppTranscriptionService` when enabled and the model file
  exists; otherwise skip gracefully (feature-detect, no hard failure).
- Keep the cheap `Peak < 0.015` pre-gate as a first-pass filter, but this is secondary once VAD runs.

*(Chunk length stays at 10 s. With a few seconds of latency budget this is fine; revisit to 12–15 s
only if boundary errors persist after A1–A4.)*

---

## Part B — Speaker labelling stability (logic only, keep TitaNet-small)

### B1. Rewrite `SessionSpeakerRegistry` to centroid matching (the core fix)
File: `src/LocalTranscriber.Engine/SessionSpeakerRegistry.cs`.
- Maintain a **running centroid** per speaker: `sumVector` + `count`, centroid = `sum / count`
  (incremental, O(1) memory). Match a new embedding against each speaker's **centroid** (cosine),
  not against the best individual sample.
- Keep a bounded list of raw embeddings (cap ~20) **only** to feed enrolment in
  `NameSessionSpeakerAsync` — decoupled from matching, so the cap no longer destabilises identity.
- **Lower + configurable threshold with hysteresis:**
  - `assignThreshold` (default **0.50**): if best centroid similarity ≥ this → confident assign.
  - `newSpeakerThreshold` (default **0.40**): only mint a new "Speaker N" when best similarity is
    **below** this. Scores in the **gray zone** [0.40, 0.50) → assign to the nearest existing
    speaker (bias against splitting). Centroid matching + this band is what stops the same voice
    becoming 2/3/4 while still separating genuinely different voices (different-speaker cosine
    typically < 0.4 for TitaNet; same-speaker often 0.5–0.7).
- **Centroid-poisoning brake (required — flagged in review):** only fold an embedding **into the
  centroid** on a *confident* assignment (similarity ≥ `assignThreshold`). Gray-zone assignments
  get the label but are **not** merged into the mean. Without this, one wrong gray-zone merge
  permanently pollutes a speaker's centroid and the error compounds for the rest of the meeting —
  trading over-splitting for a silent under-splitting (people-merged) failure mode. This replaces
  the old "update on every assignment" idea.
- Constructor takes both thresholds; **wire from config** — pass them where the registry is
  constructed in `RealTranscriptionEngine.StartAsync` (the `new SessionSpeakerRegistry()` call) from
  `_options`.

### B2. Wire the threshold through config
- `AppConfig`: `SameSpeakerThreshold = 0.50`, `NewSpeakerThreshold = 0.40`.
- Add matching fields to `TranscriptionSessionOptions`; set them in
  `EngineFactory.CreateSessionOptions`; consume in `StartAsync`.
- **Document the naming split at the fields** to avoid future confusion: `SameSpeakerThreshold` /
  `NewSpeakerThreshold` gate **in-session** clustering (against session centroids), while
  `SpeakerMatchThreshold` (0.72) / `SpeakerUncertainThreshold` (0.62) gate **cross-session**
  recognition of SQLite-**enrolled** speakers. They are independent knobs on different code paths.

### B3. Average embeddings per cluster before matching
In `ProcessSystemWindowAsync` (the per-cluster resolve loop). Today each diarized cluster is resolved
from its **single longest segment**. Instead, extract embeddings from that cluster's qualifying
segments (≥ 700 ms) and **average them** into one robust cluster embedding before `MatchAsync` /
`ResolveLabel`. Fall back to the longest segment when only one qualifies. This feeds the centroid
matcher a cleaner vector and further reduces spurious new speakers.
**Latency budget (review note):** embedding extraction runs behind its own lock, so averaging *N*
segments does *N×* the sherpa work per cluster on top of the heavier whisper model. Cap the average
at ~3 segments per cluster (longest-first) so the cost stays bounded; watch the window queue in the
step-4 run.

### B4. Fix the short-segment fallback (restated — the original was circular)
The problem: when a cluster's own longest segment is < 700 ms, `ResolveSpeakerAsync` can extract no
embedding and calls `SessionSpeakerRegistry.FallbackLabel`, which returns the **last-created**
speaker — misattributing short interjections and compounding any earlier over-split. `FallbackLabel`
has no cluster context, so "inherit the overlapping cluster" cannot be a drop-in change to it.
Correct fix, at the caller (`ProcessSystemWindowAsync` / `AssignSpeaker`): resolve each **whisper
segment's** speaker by the diarized cluster it **time-overlaps** (the existing `AssignSpeaker`
overlap logic), and have unresolved/too-short clusters fall through to the window's
already-resolved dominant cluster label rather than minting or picking "last created". Only when no
cluster in the window resolved at all do we emit a neutral unknown. Keep the 700 ms embedding gate
(short clips genuinely produce bad embeddings).

*(Cross-session thresholds `SpeakerMatchThreshold=0.72` / `SpeakerUncertainThreshold=0.62` for
SQLite-enrolled speakers are already configurable and stay as-is; revisit only if testing shows
enrolled speakers being missed.)*

---

## Config changes summary (`AppConfig.cs`)

| Field | Old | New | Purpose |
|-------|-----|-----|---------|
| `WhisperModelPath` | `ggml-base.en.bin` | `ggml-small.en.bin` | A1 accuracy |
| `Language` | — | `"en"` | A2 stop auto-detect |
| `WhisperBeamSize` | — | `5` | A3 beam search |
| `WhisperThreads` | — | `0` (auto) | A3 CPU threads |
| `InitialPrompt` | — | `""` | A3 domain vocab |
| `EnableVad` / `VadModelPath` | — | `true` / silero path | A4 VAD |
| `SameSpeakerThreshold` | — | `0.50` | B1/B2 assign gate |
| `NewSpeakerThreshold` | — | `0.40` | B1 new-speaker gate |

## Files to modify

> **Navigate by symbol, not by line.** All code claims below were verified by *content*, but line
> numbers may have drifted from the revision this plan was drafted against (the working tree has
> uncommitted changes). Locate each change by its method/field name, not the quoted line.

- `src/LocalTranscriber.Shared/AppConfig.cs` — new config fields.
- `src/LocalTranscriber.Engine/SessionSpeakerRegistry.cs` — centroid rewrite (B1, B4).
- `src/LocalTranscriber.Engine/RealTranscriptionEngine.cs` — pass thresholds (`:118`), average
  cluster embeddings (`:310`), forward decode params (`:387`), fallback change.
- `src/LocalTranscriber.Engine/EngineFactory.cs` + `TranscriptionSessionOptions.cs` — plumb new options.
- `src/LocalTranscriber.AI/WhisperCppTranscriptionService.cs` + `TranscriptionModels.cs` — beam/threads/prompt/VAD.
- `scripts/setup.ps1` — download `ggml-small.en.bin` + Silero VAD model.

## Verification
1. `dotnet build` then `dotnet test` — all existing tests green.
2. **New unit tests** for `SessionSpeakerRegistry` (add to the engine/speakers test project, or
   create one): synthetic embeddings prove (a) repeated same-speaker vectors with realistic jitter
   stay one label, (b) two clearly different vectors get two labels, (c) threshold is honoured from
   the constructor. This directly guards the reported bug.
3. `./scripts/setup.ps1 -DownloadModels` to fetch `small.en` + Silero VAD.
4. Real end-to-end run via `./scripts/run-app.ps1` (or F5) with a 2–3 person meeting through system
   audio + headphones: confirm each real person keeps **one** label across the whole session and
   spot-check transcript wording against what was said. Use `./scripts/tail-logs.ps1` to watch.
5. A/B the transcript against a `base.en` run of the same audio to sanity-check the accuracy gain.

## Risks / notes
- Verify the exact Whisper.net 1.9.1 builder chaining for beam search and VAD at implementation time
  (APIs confirmed present; precise method signatures to be checked against the installed package).
- `small.en` + beam search + VAD raises per-window CPU cost; acceptable under the "few seconds"
  latency budget, but watch the window queue on a low-core laptop (transcription is serialised behind
  one `SemaphoreSlim`). If it lags, drop beam size to 3 or `WhisperThreads` tuning first.
- Threshold defaults (0.50 / 0.40) are research-informed starting points for TitaNet-small; they are
  now config-tunable, so the real-meeting test in step 4 is how we settle final values.
- Larger model download changes first-run setup time; the `-DownloadModels` gate stays opt-in.
