---
title: Claude log-runner workflow (tail-logs) for LocalTranscriber
version: 1.0
date_created: 2026-07-09
last_updated: 2026-07-09
confidence_level: 8
---
# Implementation Plan: Claude log-runner workflow (`tail-logs`)

## Goal

**Story Goal**: Give Claude a reliable, low-friction way to run the App / CLI / MCP with their
console output captured to files, and to snapshot those files (plus the app's own logs) on demand —
mirroring Amethyst's `runbook` + `tail-logs` mechanism, adapted to Windows/.NET/PowerShell.

**Deliverable**: Three PowerShell scripts (`run-with-logs.ps1`, `tail-logs.ps1`, `stop-logs.ps1`),
matching VS Code tasks, a gitignored `tail-logs/` directory, and a CLAUDE.md section documenting the workflow.

**Success Definition**: A developer (or Claude) can run `scripts/run-with-logs.ps1 -Target app`,
leave it running, and later run `scripts/tail-logs.ps1 -Errors` to see recent error lines from every
captured source. `scripts/stop-logs.ps1` cleanly stops everything it started.

## Why

- Claude currently has no view into a running App/CLI session — `dotnet run` output lands in the VS
  Code Debug Console / integrated terminal, which Claude cannot read.
- The app already file-logs to `output/logs/`, but console-only output (unhandled exceptions,
  `Console.WriteLine`, startup banners, build errors) is lost.
- Amethyst proved the pattern's value; this ports the useful 20% (capture + bounded snapshot + stop)
  and drops the parts that don't apply here (port health-polling, service manifests, groups).

## What

A small PowerShell-based log-runner toolkit plus VS Code tasks and docs.

### Success Criteria

- [ ] `scripts/run-with-logs.ps1 -Target app|cli|mcp` builds, launches the built dll, and captures
      merged stdout+stderr to `tail-logs/<target>.log`, recording the PID to `tail-logs/<target>.pid`.
- [ ] `scripts/tail-logs.ps1` snapshots the last N lines (default 100) from every captured source
      **and** the app's own `output/logs/*.log`; supports `-Follow`+`-Timeout` (bounded), `-Errors`, `-Target`.
- [ ] `scripts/stop-logs.ps1` stops every process tracked in `tail-logs/*.pid` (tree kill) and cleans PID files.
- [ ] VS Code tasks exist: "Run App (Claude Logs)", "Run CLI (Claude Logs)", "Run MCP (Claude Logs)",
      "Tail Logs", "Stop Log Runners".
- [ ] `tail-logs/` is gitignored.
- [ ] CLAUDE.md documents the commands, file locations, and the MCP stdout caveat.

## All Needed Context

### Documentation & References

```yaml
- file: scripts/run-app.ps1
  why: Existing run-script convention (ErrorActionPreference, Set-Location to repo root)
  pattern: Header + Set-Location (Join-Path $PSScriptRoot "..")

- file: .vscode/launch.json
  why: Built-dll paths and target project layout to reuse
  pattern: program paths under src/<Project>/bin/Debug/net8.0[-windows]/<Project>.dll

- file: .vscode/tasks.json
  why: Task schema to extend (process type, group, dependsOn "build")
  gotcha: existing "build"/"test" tasks are type "process" calling dotnet on the .sln

- file: docs/MCP.md
  why: MCP already logs tool calls to output/logs/mcp-tool-calls.log
  critical: MCP stdout IS the JSON-RPC stream — never present captured stdout as a live client session
```

### Current Codebase tree (relevant slice)

```text
scripts/            run-app.ps1, run-cli.ps1, run-mcp.ps1, build.ps1, ...
.vscode/            launch.json, tasks.json, mcp.json
output/logs/        localtranscriber-<date>.log, mcp-tool-calls.log   (app's own logs; gitignored via output/)
src/LocalTranscriber.App|Cli|Mcp/bin/Debug/net8.0[-windows]/*.dll
```

### Desired Codebase tree (files added)

```text
tail-logs/                        # NEW — gitignored; captured console logs + .pid files
  .gitkeep
scripts/run-with-logs.ps1         # NEW — build + launch a target with console captured to file
scripts/tail-logs.ps1             # NEW — bounded snapshot/follow across tail-logs/ + output/logs/
scripts/stop-logs.ps1             # NEW — tree-kill tracked PIDs, clean .pid files
.vscode/tasks.json                # MODIFIED — add 5 tasks
.gitignore                        # MODIFIED — add tail-logs/
CLAUDE.md                         # MODIFIED — add "Log runners for Claude" section
```

### Known Gotchas & Windows/.NET Quirks

```text
# CRITICAL: MCP stdout is the JSON-RPC protocol stream. run-with-logs.ps1 -Target mcp is an
#   OBSERVATION mode (no client attached); merged stdout+stderr capture is fine there, but the
#   captured file is NOT a live client session. Real MCP clients connect via .vscode/mcp.json.
# CRITICAL: Run the already-BUILT dll (not `dotnet run`) so we track one clean PID. `dotnet run`
#   spawns build+exec children, making stop unreliable. Build first, then launch the dll.
# GOTCHA: Start-Process cannot redirect stdout and stderr to the SAME file. Launch via a cmd
#   wrapper (`cmd /c "<dll-host> > file 2>&1"`) OR redirect to two files. Plan uses a cmd wrapper
#   so a single merged <target>.log is produced, and we tree-kill on stop.
# GOTCHA: Follow mode must ALWAYS be time-bounded (Amethyst rule) so Claude never blocks. Default
#   -Timeout 10s; refuse -Follow without a positive timeout.
# GOTCHA: The WPF App is a GUI; it stays running until the window closes. That's expected for a
#   background log runner — stop-logs.ps1 is how Claude ends it.
```

## Implementation Blueprint

### Implementation Tasks (ordered by dependencies)

```yaml
Task 1: CREATE tail-logs/.gitkeep + MODIFY .gitignore
  - ADD: "tail-logs/" line to .gitignore (near the output/ / release/ block)
  - PLACEMENT: repo root

Task 2: CREATE scripts/run-with-logs.ps1
  - PARAMS: [ValidateSet('app','cli','mcp')] $Target; [string[]] $Args; [switch] $NoBuild
  - FOLLOW pattern: scripts/run-app.ps1 header (ErrorActionPreference, Set-Location repo root)
  - BEHAVIOR:
      1. Resolve target -> project + built dll path (App=net8.0-windows, Cli/Mcp=net8.0)
      2. Unless -NoBuild: dotnet build the target project
      3. Ensure tail-logs/ exists; define $log = tail-logs/<target>.log, $pid = tail-logs/<target>.pid
      4. Launch built dll via cmd wrapper redirecting merged output to $log; background it
      5. Write child PID to $pid; print where the log is
  - MCP NOTE: emit a one-line warning that this is observation mode (stdout not a live client)

Task 3: CREATE scripts/tail-logs.ps1
  - PARAMS: [int] $Lines=100; [switch] $Follow; [int] $Timeout=10; [switch] $Errors;
            [ValidateSet('all','app','cli','mcp')] $Target='all'
  - FOLLOW pattern: runbook/tail-logs.sh semantics (snapshot default, bounded follow, errors filter)
  - SOURCES: tail-logs/<target>.log (captured console) + output/logs/*.log (app's own logs)
  - BEHAVIOR:
      1. Build source list from $Target (all => every captured file + output/logs/*.log)
      2. Snapshot: per source, header + Get-Content -Tail $Lines (Errors => regex filter)
      3. Follow: Get-Content -Wait bounded by $Timeout (reject Follow without positive Timeout)
  - ERROR_RE: 'error|exception|fail|critical|fatal|unhandled|warn'

Task 4: CREATE scripts/stop-logs.ps1
  - FOLLOW pattern: runbook/stop-debug.sh intent (tree kill, self-protection not needed on Windows)
  - BEHAVIOR:
      1. For each tail-logs/*.pid: read PID, Stop-Process -Id <pid> including child tree
      2. Remove the .pid file; leave .log files intact for post-mortem snapshotting
      3. No-op cleanly if nothing is running

Task 5: MODIFY .vscode/tasks.json
  - ADD tasks (type shell, command pwsh -File scripts/<script>.ps1 -Args...):
      "Run App (Claude Logs)"  -> run-with-logs.ps1 -Target app
      "Run CLI (Claude Logs)"  -> run-with-logs.ps1 -Target cli
      "Run MCP (Claude Logs)"  -> run-with-logs.ps1 -Target mcp
      "Tail Logs"              -> tail-logs.ps1
      "Stop Log Runners"       -> stop-logs.ps1
  - PRESERVE: existing "build"/"test" tasks

Task 6: MODIFY CLAUDE.md
  - ADD "Log runners for Claude" section: commands, tail-logs/ + output/logs/ locations,
    the snapshot-first workflow, and the MCP stdout caveat
```

### Implementation Patterns & Key Details

```powershell
# run-with-logs.ps1 — clean single-PID launch of a built dll with merged capture
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
# ...resolve $dll for $Target; dotnet build unless -NoBuild...
New-Item -ItemType Directory -Force -Path tail-logs | Out-Null
$log = "tail-logs/$Target.log"
# cmd wrapper merges stdout+stderr into one file and lets us background + track the PID
$p = Start-Process cmd -ArgumentList '/c', "dotnet `"$dll`" $Args > `"$log`" 2>&1" -PassThru -WindowStyle Hidden
$p.Id | Out-File "tail-logs/$Target.pid"

# tail-logs.ps1 — bounded follow guard
if ($Follow -and $Timeout -le 0) { throw "-Follow requires a positive -Timeout (seconds)." }
Get-Content -Path $file -Tail $Lines   # snapshot
# follow: Start a job / Get-Content -Wait with a stopwatch cap at $Timeout
```

## E2E Validation Plan
- [ ] `run-with-logs.ps1 -Target cli -Args '--help'` produces `tail-logs/cli.log` with CLI help text.
- [ ] `run-with-logs.ps1 -Target app` launches the WPF window; `tail-logs/app.log` grows; `app.pid` exists.
- [ ] `tail-logs.ps1 -Errors` after forcing an error surfaces only error/warn lines from both sources.
- [ ] `tail-logs.ps1 -Follow -Timeout 5` returns after ~5s and never blocks.
- [ ] `stop-logs.ps1` stops the running App and removes `*.pid`; a second run is a clean no-op.
- [ ] VS Code tasks run each script; F5 debugging still works unchanged and its `output/logs/` output
      is visible via `tail-logs.ps1`.

### Anti-Patterns to Avoid

- ❌ Don't `dotnet run` for background runners — track a single built-dll PID instead.
- ❌ Don't present captured MCP stdout as a live client session.
- ❌ Don't allow unbounded `-Follow` (must have a positive timeout).
- ❌ Don't add port health-polling / manifests / groups — not applicable to a desktop app (YAGNI).
- ❌ Don't change the app's own logging code or move `output/logs/` — out of scope.
```
