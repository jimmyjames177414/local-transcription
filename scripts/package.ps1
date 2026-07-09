# Zips the published release folder. Run ./scripts/publish.ps1 first.
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

if (-not (Test-Path "release/LocalTranscriber")) {
    Write-Error "release/LocalTranscriber not found. Run ./scripts/publish.ps1 first."
}

$stamp = Get-Date -Format "yyyyMMdd"
$zip = "release/LocalTranscriber-win-x64-$stamp.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "release/LocalTranscriber" -DestinationPath $zip
Write-Host "Created $zip"
