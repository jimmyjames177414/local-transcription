# Publishes self-contained win-x64 builds of the app, CLI, and MCP server into release/LocalTranscriber.
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$out = "release/LocalTranscriber"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $out | Out-Null

dotnet publish src/LocalTranscriber.App/LocalTranscriber.App.csproj -c Release -r win-x64 --self-contained true -o "$out/app"
dotnet publish src/LocalTranscriber.Cli/LocalTranscriber.Cli.csproj -c Release -r win-x64 --self-contained true -o "$out/cli"
dotnet publish src/LocalTranscriber.Mcp/LocalTranscriber.Mcp.csproj -c Release -r win-x64 --self-contained true -o "$out/mcp"

New-Item -ItemType Directory -Force -Path "$out/models/whisper", "$out/models/speaker" | Out-Null

# Bundle models only when already present locally (they are large; never downloaded here).
if (Test-Path "models/whisper/ggml-base.en.bin") {
    Copy-Item "models/whisper/ggml-base.en.bin" "$out/models/whisper/"
}
if (Test-Path "models/speaker/segmentation.onnx") {
    Copy-Item "models/speaker/segmentation.onnx", "models/speaker/embedding.onnx" "$out/models/speaker/"
}

@"
{
  "transcriptFolder": "",
  "databasePath": "",
  "whisperModelPath": "",
  "speakerModelPath": "",
  "enableMicCapture": true,
  "enableSystemCapture": true,
  "defaultMicSpeakerName": "Me",
  "speakerMatchThreshold": 0.72,
  "speakerUncertainThreshold": 0.62,
  "chunkSeconds": 10,
  "overlapMs": 500,
  "flushIntervalMs": 1000
}
"@ | Set-Content "$out/config.example.json"

@"
LocalTranscriber — local-only Windows transcription.

Run:
  app\LocalTranscriber.App.exe    (Windows UI)
  cli\LocalTranscriber.Cli.exe    (command line: start, stop, status, tail, speakers, ...)
  mcp\LocalTranscriber.Mcp.exe    (MCP stdio server for Claude)

Models:
  The app expects models next to the executables:
    models\whisper\ggml-base.en.bin
    models\speaker\segmentation.onnx
    models\speaker\embedding.onnx
  If missing, download them (see docs/SETUP.md in the repo) — the app never downloads silently.

Data locations (packaged mode):
  Config:      %AppData%\LocalTranscriber\config.json
  Database:    %AppData%\LocalTranscriber\data\localtranscriber.sqlite
  Logs:        %AppData%\LocalTranscriber\logs\
  Transcripts: Documents\LocalTranscriber\Transcripts\

No cloud services. No API keys. Everything stays on this machine.
"@ | Set-Content "$out/README.txt"

Write-Host "Published to $out"
