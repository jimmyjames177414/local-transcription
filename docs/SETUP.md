# Setup

## Prerequisites

- Windows 10/11
- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)
- VS Code (optional, recommended). Visual Studio is NOT required.

## Steps

```powershell
git clone <repo>
cd LocalTranscriber
./scripts/setup.ps1
dotnet build
dotnet test
```

## Models

Whisper and speaker models are needed from Phase 9/10 onward. Instructions will land here with those phases. Models are never downloaded silently.
