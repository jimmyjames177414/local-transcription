# Setup

## Prerequisites

- Windows 10/11
- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)
- VS Code (optional, recommended). Visual Studio is NOT required.

## Steps

```powershell
git clone <repo>
cd LocalTranscriber
./scripts/setup.ps1 -DownloadModels   # restores packages + fetches models (~190 MB total)
dotnet build
dotnet test
```

Without `-DownloadModels` nothing is fetched; the app shows helpful errors pointing at the expected paths.

## Models

| File | Purpose | Size | Source |
|---|---|---|---|
| `models/whisper/ggml-base.en.bin` | offline transcription | ~142 MB | huggingface.co/ggerganov/whisper.cpp |
| `models/speaker/segmentation.onnx` | speaker segmentation (pyannote) | ~6 MB | github.com/k2-fsa/sherpa-onnx releases |
| `models/speaker/embedding.onnx` | voice embeddings (NeMo TitaNet-small) | ~40 MB | github.com/k2-fsa/sherpa-onnx releases |

Alternative whisper models work too (tiny.en for speed, small.en for accuracy) — update `whisperModelPath` in config.

## Verify

```powershell
./scripts/run-cli.ps1 audio devices
./scripts/run-cli.ps1 audio record-mic --seconds 5 --output ./output/audio/check.wav
./scripts/run-cli.ps1 transcribe --audio ./output/audio/check.wav
./scripts/run-app.ps1
```

## Storage locations

Dev checkout (`LocalTranscriber.sln` present above the working directory): everything under `./output/`.

Packaged build:

```text
%AppData%\LocalTranscriber\config.json
%AppData%\LocalTranscriber\data\localtranscriber.sqlite
%AppData%\LocalTranscriber\logs\
Documents\LocalTranscriber\Transcripts\
```

Override the data root with the `LOCALTRANSCRIBER_HOME` environment variable.

## Packaging

```powershell
./scripts/publish.ps1   # self-contained win-x64 exes -> release/LocalTranscriber
./scripts/package.ps1   # zips the release folder
```
