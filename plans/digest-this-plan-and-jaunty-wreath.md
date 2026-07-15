# Plan: Digest & Honest Assessment of voice-detection-plan.md

## Verification Summary

All root-cause claims verified against actual code. Line numbers accurate to ±2.

---

## What I verified is TRUE

### Root cause diagnosis (Part B — speaker)
- `SessionSpeakerRegistry.ResolveLabel` (line 45): greedy best-of-all-stored-samples, not centroid ✓
- `0.60` hardcoded threshold (line 24 ctor default) ✓
- 10-embedding cap (line 64) ✓
- `new SessionSpeakerRegistry()` in `RealTranscriptionEngine.cs:126` — no args, always 0.60 ✓
- `FallbackLabel()` returns `_speakers[^1].Label` (last-created, not overlap-based) ✓

### Root cause diagnosis (Part A — transcription)
- `WhisperModelPath` defaults to `ggml-base.en.bin` (AppConfig.cs:7) ✓
- `TranscriptionSessionOptions.Language` field exists (line 16) but `EngineFactory.CreateSessionOptions` never assigns it ✓
- With `Language = null`, `WhisperCppTranscriptionService` calls `WithLanguageDetection()` (lines 44-51) ✓
- Peak gate `< 0.015` at `RealTranscriptionEngine.cs:271` ✓
- `SpeakerMatchThreshold=0.72` / `SpeakerUncertainThreshold=0.62` wired only to enrolled speaker path in EngineFactory (lines 24-28) ✓

### Config / plumbing
- `TranscriptionRequest` (TranscriptionModels.cs) has no `BeamSize`, `Threads`, `Prompt`, `EnableVad` fields yet — plan correctly identifies this as a gap ✓
- `SessionSpeakerRegistryTests.cs` already exists — plan says "add new unit tests", can extend in place ✓
- Whisper.net 1.9.1 confirmed in `.csproj` ✓

---

## Honest confidence breakdown

### A1 — Switch default model to small.en
**Confidence: 99%**. One line in `AppConfig.cs`, one download block in `setup.ps1`. Zero risk.

### A2 — Set Language = "en"
**Confidence: 99%**. `EngineFactory.CreateSessionOptions` already has the field — just assign it. One-liner.  
**Note:** `RealtimeVoiceSession.OnPushToTalkUpAsync` already hardcodes `Language = "en"` (line 338). Main pipeline just needs to catch up.

### A3 — Beam search / threads / prompt
**Confidence: 75%**. Whisper.net 1.9.1 package docs say these APIs exist; plan correctly flags "confirm exact builder chaining at implementation time." Current builder is `factory.CreateBuilder().WithProbabilities().WithLanguage()`. Beam and thread additions should chain cleanly, but exact method names + ordering need a quick package-reference check before writing. Not a blocker — just verify first.

### A4 — Silero VAD
**Confidence: 60%**. This is the weakest section:
- Plan says download "ggml-silero-vad.bin" but the exact filename and HuggingFace URL are not specified. Need to confirm from Whisper.net 1.9.1 package source.
- VAD wiring API (builder method name, model path arg) unverified — plan just says "wire into the builder."
- Silero VAD may need a separate model file type not matching the ggml whisper convention.
- Plan does flag this as a risk. Skip or spike this last if A1-A3 and B1-B4 are done first.

### B1 — Centroid rewrite of SessionSpeakerRegistry
**Confidence: 92%**. Logic is mathematically sound. Poisoning brake (gray-zone 0.40-0.50 → assign but no centroid update) is the key invariant and clearly described. `Snapshot()` method returning raw embeddings for enrolment stays decoupled — plan correctly notes keeping a bounded raw list (cap 20) separate from matching. Existing `SessionSpeakerRegistryTests.cs` needs extension for the brake test.

**One detail plan is silent on:** the registry constructor signature changes from `(double sameSpeakerThreshold = 0.60)` to `(double assignThreshold, double newSpeakerThreshold)` — existing tests hardcode `sameSpeakerThreshold: 0.60` and will break. Easy fix, just flag it.

### B2 — Config wiring
**Confidence: 99%**. Trivial plumbing through `AppConfig` → `TranscriptionSessionOptions` → `EngineFactory` → `StartAsync`. Plan correctly maps the naming to avoid confusion with existing enrolled-speaker thresholds.

### B3 — Cluster embedding averaging (≤3 segments)
**Confidence: 78%**. Logic is right but the plan underspecifies the refactor:

Current `ProcessSystemWindowAsync` calls `ResolveSpeakerAsync(wavPath, longest, ct)` which does embedding extraction + recognition + registry lookup in one shot.

For B3, you need to:
1. Extract up to 3 embeddings (`_embedding.ExtractEmbeddingAsync`) separately
2. Average the vectors
3. Then run the recognition + registry path on the averaged embedding

This means either (a) refactoring `ResolveSpeakerAsync` to accept a pre-extracted embedding, or (b) extracting embeddings inline and calling a new `ResolveSpeakerFromEmbedding` helper. Option (b) is cleaner. The plan doesn't spell this out — implementer must decide. Not hard, just unspecified.

**Cost cap concern:** Plan says "serialized behind the single transcription `SemaphoreSlim` (WhisperCppTranscriptionService.cs:12)". Slight inaccuracy — the whisper lock serializes whisper calls, not embedding calls. Sherpa embedding has its own internal lock. The real bottleneck is the sequential window queue. The effect (extra CPU per window) is correct, just the attribution is off.

### B4 — Short-segment fallback fix
**Confidence: 80%**. The fix (use overlap-based cluster label instead of "last created") is correct. Current `ResolveSpeakerAsync` (line 371) returns `FallbackLabel()` for < 700 ms. Plan moves fallback logic to `ProcessSystemWindowAsync` — when cluster has no usable embedding, assign the overlapping resolved cluster's label.

**Implementation detail plan glosses over:** `clusterLabels` dictionary is built by calling `ResolveSpeakerAsync` once per cluster. A fallback cluster will have `SpeakerLabel("speaker_unknown", "Speaker N", IsKnown: false)`. To do overlap-based fallback, `ProcessSystemWindowAsync` needs to find which *other* cluster (with a real label) most overlaps the fallback cluster's diarized segments in time. The `AssignSpeaker` helper operates on whisper segments, not on diarized clusters vs. each other — so a new small helper or inline loop is needed. Doable, just plan-level hand-waving.

---

## What's missing from the plan

1. **Silero VAD model URL** — must find this before A4 work starts.
2. **B3 ResolveSpeakerAsync refactor pattern** — plan implies it but doesn't specify which approach. Decide before coding B3.
3. **Existing registry test breakage** — B1 changes constructor signature; `SessionSpeakerRegistryTests.cs` tests use `sameSpeakerThreshold: 0.60` which won't compile. Update tests as part of B1.
4. **`NameSessionSpeakerAsync` in RealTranscriptionEngine** — not mentioned by plan. It presumably reads `_registry.Snapshot()` embeddings to enrol a named speaker. With B1 keeping the raw embedding list intact (cap 20), this should still work — just verify at impl time.

---

## Recommended implementation order

1. A1 + A2 — trivial, immediate accuracy win, validate pipeline doesn't break
2. B1 + B2 — core speaker fix; extend `SessionSpeakerRegistryTests.cs` for brake test
3. B3 + B4 — speaker quality polish; B3 needs ResolveSpeakerAsync refactor decision
4. A3 — beam/threads/prompt; check Whisper.net builder API first
5. A4 (Silero VAD) — last, after confirming model filename and API

---

## Files to modify

(same as plan, verified correct)
- `src/LocalTranscriber.Shared/AppConfig.cs`
- `src/LocalTranscriber.Engine/SessionSpeakerRegistry.cs` — full rewrite
- `src/LocalTranscriber.Engine/RealTranscriptionEngine.cs` — registry ctor, B3 restructure, B4 fallback
- `src/LocalTranscriber.Engine/EngineFactory.cs` + `TranscriptionSessionOptions.cs`
- `src/LocalTranscriber.AI/WhisperCppTranscriptionService.cs` + `TranscriptionModels.cs`
- `scripts/setup.ps1`
- `tests/LocalTranscriber.Engine.Tests/SessionSpeakerRegistryTests.cs` — extend (not create)

---

## Overall

Plan is solid. Root causes accurate. Solutions appropriate. Risks are real but properly flagged. Overall confidence ~85%. The 15% uncertainty lives in A4 (VAD model/API), B3 (ResolveSpeakerAsync restructuring), and B4 (overlap fallback helper). None are blockers — all resolve at implementation time with a quick code check before writing.
