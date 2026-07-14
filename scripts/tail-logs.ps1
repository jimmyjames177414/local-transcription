<#
.SYNOPSIS
  Snapshot (or bounded-follow) console logs captured by run-with-logs.ps1 plus the app's
  own logs under output/logs/, so Claude can read what a running session is doing.

.DESCRIPTION
  Sources:
    - tail-logs/<target>.log   (console captured by run-with-logs.ps1)
    - output/logs/*.log        (the app's own logs, e.g. localtranscriber-*.log, mcp-tool-calls.log)

  Follow mode is ALWAYS time-bounded so it never blocks.

.EXAMPLE
  ./scripts/tail-logs.ps1                 # last 100 lines of every source
.EXAMPLE
  ./scripts/tail-logs.ps1 -Errors        # only error/warn lines
.EXAMPLE
  ./scripts/tail-logs.ps1 -Follow -Timeout 5 -Target app
#>
param(
    [int] $Lines = 100,
    [switch] $Follow,
    [int] $Timeout = 10,
    [switch] $Errors,
    [ValidateSet('all', 'app', 'cli', 'mcp')]
    [string] $Target = 'all',
    [switch] $Background
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

if ($Follow -and $Timeout -le 0) {
    throw "-Follow requires a positive -Timeout (seconds) so it never blocks."
}

$ErrorRe = 'error|exception|fail|critical|fatal|unhandled|warn'

# Build the list of source files to read.
$sources = New-Object System.Collections.Generic.List[string]

$captured = @{
    app = 'tail-logs/app.log'
    cli = 'tail-logs/cli.log'
    mcp = 'tail-logs/mcp.log'
}

if ($Target -eq 'all') {
    foreach ($f in $captured.Values) { $sources.Add($f) }
    if (Test-Path output/logs) {
        Get-ChildItem output/logs -Filter *.log | ForEach-Object { $sources.Add($_.FullName) }
    }
}
else {
    $sources.Add($captured[$Target])
}

function Show-Snapshot([string] $file) {
    Write-Host ""
    Write-Host "========== $file ==========" -ForegroundColor Cyan
    if (-not (Test-Path $file)) {
        Write-Host "(no log file yet - has it been started?)"
        return
    }
    $content = Get-Content -Path $file -Tail $Lines -ErrorAction SilentlyContinue
    if ($Errors) {
        $content = $content | Where-Object { $_ -imatch $ErrorRe }
        if (-not $content) { Write-Host "(no error/warn lines in last $Lines)"; return }
    }
    $content | ForEach-Object { Write-Host $_ }
}

if (-not $Follow) {
    foreach ($s in $sources) { Show-Snapshot $s }
    return
}

# Bounded follow: show a snapshot first, then stream new lines until the timeout elapses.
foreach ($s in $sources) { Show-Snapshot $s }
Write-Host ""
Write-Host "[tail-logs] Following for ${Timeout}s..." -ForegroundColor Cyan

# In -Background mode (e.g. a debug preLaunchTask) record this follower's PID so a
# postDebugTask can stop it when the debug session ends.
$followPidFile = $null
if ($Background) {
    New-Item -ItemType Directory -Force -Path tail-logs | Out-Null
    $followPidFile = 'tail-logs/tail-follow.pid'
    $PID | Out-File -Encoding ascii $followPidFile
}

try {
    $existing = @{}
    foreach ($s in $sources) {
        $existing[$s] = if (Test-Path $s) { (Get-Content $s -ErrorAction SilentlyContinue | Measure-Object -Line).Lines } else { 0 }
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $Timeout) {
        foreach ($s in $sources) {
            if (-not (Test-Path $s)) { continue }
            $all = Get-Content $s -ErrorAction SilentlyContinue
            $count = ($all | Measure-Object -Line).Lines
            if ($count -gt $existing[$s]) {
                $new = $all | Select-Object -Skip $existing[$s]
                if ($Errors) { $new = $new | Where-Object { $_ -imatch $ErrorRe } }
                $new | ForEach-Object { Write-Host "[$([System.IO.Path]::GetFileName($s))] $_" }
                $existing[$s] = $count
            }
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "[tail-logs] Follow window elapsed." -ForegroundColor Cyan
}
finally {
    if ($followPidFile -and (Test-Path $followPidFile)) { Remove-Item $followPidFile -ErrorAction SilentlyContinue }
}
