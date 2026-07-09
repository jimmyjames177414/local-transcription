# Usage

## Windows UI

```powershell
./scripts/run-app.ps1
```

- **Session tab**: pick an output folder, press Start. Live transcript appears in the preview; `.txt` and `.jsonl` files are written continuously. Pause/Resume/Stop as needed.
- **Speakers tab**: rename detected speakers (e.g. "Speaker 2" → "Joe"). Renames keep voice embeddings, so later sessions greet Joe by name. Forget removes the speaker and its voice data.
- **Settings tab**: transcript folder, mic/system toggles, model paths, match threshold, chunk size. Refresh audio devices lists what the app can capture.

## CLI

```powershell
# real session (blocks until Ctrl+C; control from other terminals)
localtranscriber start --output "./output/transcripts/meeting.txt" --mic true --system true
localtranscriber status
localtranscriber pause
localtranscriber resume
localtranscriber stop

# transcripts
localtranscriber tail --file "./output/transcripts/meeting.txt" --lines 50
localtranscriber read --file "./output/transcripts/meeting.txt"
localtranscriber sessions

# one-shot transcription of an existing WAV
localtranscriber transcribe --audio "./recording.wav" --output "./transcript.txt"

# audio utilities
localtranscriber audio devices
localtranscriber audio record-mic --seconds 10 --output "./output/audio/mic-test.wav"
localtranscriber audio record-system --seconds 10 --output "./output/audio/system-test.wav"
localtranscriber audio record-both --seconds 10 --output-folder "./output/audio"

# speakers
localtranscriber speakers list
localtranscriber speakers rename --from "Speaker 2" --to "Joe"
localtranscriber speakers forget --name "Joe"
localtranscriber speakers enroll --name "Joe" --audio "./samples/joe.wav"
localtranscriber speakers match --audio "./samples/unknown.wav"
localtranscriber speakers diarize --audio "./output/audio/system-test.wav"

# config
localtranscriber config show
localtranscriber config set transcriptFolder "./output/transcripts"
```

In the dev checkout, `localtranscriber` means `dotnet run --project src/LocalTranscriber.Cli --`.

## Transcript formats

`.txt` (human):

```text
[10:04:12] Me: Can everyone hear me?
[10:04:18] Speaker 1: Yes, I can hear you.
[10:04:23] Joe: Let's move deployment to Friday.
[10:04:31] possibly Martina: I need to check the test results.
```

`.jsonl` (machine): one JSON object per line with sessionId, timestamp, speakerId, speakerName, source (`microphone`/`systemAudio`), text, confidence, startMs, endMs.

## How speaker memory works

1. Unknown voices in system audio get session labels (Speaker 1, Speaker 2, ...) held consistent within the session by voice-embedding similarity.
2. Rename a speaker (UI, CLI, or MCP) — or enroll from a WAV sample.
3. In later sessions, each voice is compared against stored embeddings:
   - similarity ≥ 0.72 → `Joe:`
   - 0.62–0.72 → `possibly Joe:`
   - below → new session label.

Thresholds are configurable (`speakerMatchThreshold`, `speakerUncertainThreshold`).

## Accuracy expectations (honest)

- Whisper base.en is very good on clear speech, weaker on heavy accents/noise/crosstalk.
- Speaker labels are best-effort. Diarization runs on ~10s chunks, which limits context; overlapping speakers and similar voices cause misattribution. Expect `possibly` labels and occasional swaps.
- The microphone track is always labeled with your name (`Me` by default). If your mic hears the speakers (no headphones), remote speech will bleed into the `Me` track — wear headphones for clean separation.
- Silence is skipped to avoid whisper inventing text, but steady background noise can still produce artifacts like `[Music]`.
