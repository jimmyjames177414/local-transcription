<#
.SYNOPSIS
  Build and launch an App/CLI/MCP target with its console output captured to a file
  that Claude can read via scripts/tail-logs.ps1.

.DESCRIPTION
  Launches the already-built dll (not `dotnet run`) so a single clean PID is tracked in
  tail-logs/<target>.pid. Merged stdout+stderr is written to tail-logs/<target>.log.

  MCP NOTE: `-Target mcp` is OBSERVATION mode only. MCP stdout is the JSON-RPC protocol
  stream; the captured file is NOT a live client session. Real clients connect via
  .vscode/mcp.json.

.EXAMPLE
  ./scripts/run-with-logs.ps1 -Target cli -AppArgs '--help'

.EXAMPLE
  ./scripts/run-with-logs.ps1 -Target app
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('app', 'cli', 'mcp')]
    [string] $Target,

    [string] $AppArgs = "",

    [switch] $NoBuild
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$targets = @{
    app = @{ Project = 'src/LocalTranscriber.App'; Dll = 'src/LocalTranscriber.App/bin/Debug/net8.0-windows/LocalTranscriber.App.dll' }
    cli = @{ Project = 'src/LocalTranscriber.Cli'; Dll = 'src/LocalTranscriber.Cli/bin/Debug/net8.0/LocalTranscriber.Cli.dll' }
    mcp = @{ Project = 'src/LocalTranscriber.Mcp'; Dll = 'src/LocalTranscriber.Mcp/bin/Debug/net8.0/LocalTranscriber.Mcp.dll' }
}

$info = $targets[$Target]

if (-not $NoBuild) {
    Write-Host "[run-with-logs] Building $($info.Project)..."
    dotnet build $info.Project | Out-Host
}

$dll = $info.Dll
if (-not (Test-Path $dll)) {
    throw "Built dll not found: $dll. Build the project first (or omit -NoBuild)."
}

New-Item -ItemType Directory -Force -Path tail-logs | Out-Null
$log = Join-Path (Get-Location) "tail-logs/$Target.log"
$pidFile = "tail-logs/$Target.pid"

if ($Target -eq 'mcp') {
    Write-Host "[run-with-logs] NOTE: MCP observation mode. Captured stdout is the JSON-RPC stream, not a live client session." -ForegroundColor Yellow
}

# Start-Process cannot merge stdout+stderr to a single file, so use a cmd wrapper.
$dllFull = Join-Path (Get-Location) $dll
$cmdLine = "dotnet `"$dllFull`" $AppArgs > `"$log`" 2>&1"
$proc = Start-Process cmd -ArgumentList '/c', $cmdLine -PassThru -WindowStyle Hidden

$proc.Id | Out-File -Encoding ascii $pidFile
Write-Host "[run-with-logs] $Target started (pid $($proc.Id)). Log: tail-logs/$Target.log"
Write-Host "[run-with-logs] Snapshot with: ./scripts/tail-logs.ps1 -Target $Target"
Write-Host "[run-with-logs] Stop with:     ./scripts/stop-logs.ps1"
