<#
.SYNOPSIS
  Kills any running LocalTranscriber.App instances (WPF UI), including ones launched
  by F5/the debugger or a desktop shortcut. Used as a debug preLaunch step so you never
  end up with two instances contending for the microphone.

  Safe to run when nothing is up (no-op). Never touches the MCP server, CLI, or unrelated
  dotnet processes — it matches only command lines containing "LocalTranscriber.App".
#>
$ErrorActionPreference = 'SilentlyContinue'

$procs = Get-CimInstance Win32_Process |
    Where-Object { $_.CommandLine -like '*LocalTranscriber.App*' }

foreach ($p in $procs) {
    Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
}

Write-Output ("stray app instances cleared: {0}" -f @($procs).Count)
exit 0
