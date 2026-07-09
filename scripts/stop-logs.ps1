$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

<#
.SYNOPSIS
  Stop every process started by run-with-logs.ps1 (tracked in tail-logs/*.pid) and clean up
  the PID files. Log files are left intact for post-mortem snapshotting.

.EXAMPLE
  ./scripts/stop-logs.ps1
#>

if (-not (Test-Path tail-logs)) {
    Write-Host "[stop-logs] Nothing to stop (no tail-logs/ directory)."
    return
}

$pidFiles = Get-ChildItem tail-logs -Filter *.pid -ErrorAction SilentlyContinue
if (-not $pidFiles) {
    Write-Host "[stop-logs] No running log runners tracked."
    return
}

foreach ($pidFile in $pidFiles) {
    $procId = (Get-Content $pidFile.FullName -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($procId -match '^\d+$') {
        $procId = [int] $procId
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($proc) {
            Write-Host "[stop-logs] Stopping $($pidFile.BaseName) (pid $procId) and its children..."
            # Taskkill /T terminates the whole process tree (cmd wrapper -> dotnet -> app).
            & taskkill /PID $procId /T /F 2>$null | Out-Null
        }
        else {
            Write-Host "[stop-logs] $($pidFile.BaseName) (pid $procId) not running."
        }
    }
    Remove-Item $pidFile.FullName -ErrorAction SilentlyContinue
}

Write-Host "[stop-logs] Done. Log files kept under tail-logs/."
