# Restores NuGet packages and (with consent) downloads local AI models.
# Models are only downloaded when -DownloadModels is passed — never silently.
param(
    [switch]$DownloadModels
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

dotnet restore LocalTranscriber.sln

if ($DownloadModels) {
    New-Item -ItemType Directory -Force -Path "models/whisper" | Out-Null

    $whisperModel = "models/whisper/ggml-small.en.bin"
    if (-not (Test-Path $whisperModel)) {
        Write-Host "Downloading whisper.cpp model ggml-small.en.bin (~466 MB)..."
        Invoke-WebRequest `
            -Uri "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin" `
            -OutFile $whisperModel
    } else {
        Write-Host "Whisper model already present: $whisperModel"
    }

    # ggml-base.en.bin kept as an optional fallback for low-spec machines (set WhisperModelPath in config).
    $whisperBase = "models/whisper/ggml-base.en.bin"
    if (-not (Test-Path $whisperBase)) {
        Write-Host "Downloading optional fallback whisper model ggml-base.en.bin (~142 MB)..."
        Invoke-WebRequest `
            -Uri "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin" `
            -OutFile $whisperBase
    } else {
        Write-Host "Whisper base fallback already present: $whisperBase"
    }

    $vadModel = "models/whisper/ggml-silero-v5.1.2.bin"
    if (-not (Test-Path $vadModel)) {
        Write-Host "Downloading Silero VAD model ggml-silero-v5.1.2.bin (~1.8 MB)..."
        Invoke-WebRequest `
            -Uri "https://huggingface.co/sandrohanea/whisper.net/resolve/v4/vad/ggml-silero-v5.1.2.bin" `
            -OutFile $vadModel
    } else {
        Write-Host "Silero VAD model already present: $vadModel"
    }

    $segModel = "models/speaker/segmentation.onnx"
    if (-not (Test-Path $segModel)) {
        Write-Host "Downloading sherpa-onnx pyannote segmentation model (~7 MB)..."
        New-Item -ItemType Directory -Force -Path "models/speaker" | Out-Null
        $tar = "models/speaker/seg.tar.bz2"
        Invoke-WebRequest `
            -Uri "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2" `
            -OutFile $tar
        tar xjf $tar -C models/speaker
        Copy-Item "models/speaker/sherpa-onnx-pyannote-segmentation-3-0/model.onnx" $segModel
        Remove-Item -Recurse -Force "models/speaker/sherpa-onnx-pyannote-segmentation-3-0", $tar
    } else {
        Write-Host "Segmentation model already present: $segModel"
    }

    $embModel = "models/speaker/embedding.onnx"
    if (-not (Test-Path $embModel)) {
        Write-Host "Downloading sherpa-onnx NeMo TitaNet-small speaker embedding model (~40 MB)..."
        Invoke-WebRequest `
            -Uri "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/nemo_en_titanet_small.onnx" `
            -OutFile $embModel
    } else {
        Write-Host "Embedding model already present: $embModel"
    }
} else {
    Write-Host "Skipping model downloads. Run './scripts/setup.ps1 -DownloadModels' to fetch them."
}

Write-Host "Setup complete."
