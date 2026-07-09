# Restores NuGet packages. Model downloads are added in later phases (whisper/sherpa-onnx).
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
dotnet restore LocalTranscriber.sln
Write-Host "Setup complete. Model downloads arrive in a later phase."
