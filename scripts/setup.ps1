# Restores NuGet packages and (with consent) downloads local AI models.
# Models are only downloaded when -DownloadModels is passed — never silently.
param(
    [switch]$DownloadModels
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

dotnet restore LocalTranscriber.sln

if ($DownloadModels) {
    $whisperModel = "models/whisper/ggml-base.en.bin"
    if (-not (Test-Path $whisperModel)) {
        Write-Host "Downloading whisper.cpp model ggml-base.en.bin (~142 MB)..."
        New-Item -ItemType Directory -Force -Path "models/whisper" | Out-Null
        Invoke-WebRequest `
            -Uri "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin" `
            -OutFile $whisperModel
    } else {
        Write-Host "Whisper model already present: $whisperModel"
    }
} else {
    Write-Host "Skipping model downloads. Run './scripts/setup.ps1 -DownloadModels' to fetch them."
}

Write-Host "Setup complete."
