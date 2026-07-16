# Execution Plan — 48-hour remediation (verified & corrected)

## Context

The source plan `plans/golden-swimming-pillow.md` audits every commit from 2026-07-14/15
(hashes `31cd908`, `fcb89b4`, `a2af7d0`, `eb69fb0`, `87243cc`, `1d5617a`, `097a0a7`,
`04dffe7` — all confirmed present in git history) plus three user-reported issues. It found
2 blockers, 6 highs, 13 mediums, 3 user issues, 12 lows introduced by the recent B1 DI merge,
the God-class extractions (B3), the typed providers (B2), and the Claude-CLI/hybrid/WSL feature
work. Goal: land a clean `dotnet build` and `dotnet test` (≥133 green) and make the WPF UI
safe to smoke-test — the B1 DI merge was never UI-verified.

This plan supersedes the source with corrections found by reading the current code. **Key
deltas from the source plan** (do not follow the source verbatim where these conflict):

- **HIGH-4:** `App.OnStartup` is synchronous `protected override void`, *not* `async void`. `await` won't compile — call `StartAsync().GetAwaiter().GetResult()`.
- **BLOCK-1:** `WslDirectoryExists` returns `bool` with an `out string error` param (async can't use `out`), and `Create` returns `RealtimeVoiceFactory.Resolution`. Do **not** rewrite the factory async — wrap the call site in `Task.Run`.
- **MED-3:** `_speaker` in `HybridBrainConversation` is `readonly` — can't null it; drop `readonly` first.
- **MED-7:** the store type is `SqliteKnownSpeakerStore(SqliteDatabase db)`, not `SqliteSpeakerStore(path)`.
- **MED-8:** `ServiceCollectionExtensions` has no `new ConfigService()`; it does `AddSingleton<ConfigService>()` (a *second* instance). The bootstrap `ConfigService` in `App.xaml.cs` is never registered.
- **MED-11/12/13:** test projects live under `tests/`, not `src/`. `LocalTranscriber.Shared.Tests` does **not** exist.
- **LOW-3:** already mitigated (500ms backoff + `maxNumberOfServerInstances=1`) → **skip**.

Decisions taken with the user: **full DB/DI consolidation** (MED-7/8); **AgentProviders tests go
in the existing `tests/LocalTranscriber.Voice.Tests`**; **substantive LOWs only**
(1,2,4,5,6,7,9,10 — skip 3, 8, 11, 12).

Verify when done:
```powershell
dotnet build   # clean
dotnet test    # ≥133 green
```
Then manually smoke-test each WPF screen (Record, Speakers, Agent, Sessions, Notes, Settings).

---

## BLOCK-1 — WSL check freezes the UI thread

**File:** `src/LocalTranscriber.Voice/AgentConversationFactory.cs` (`WslDirectoryExists`, line 158 `p.WaitForExit(10000)`) → called from `CreateClaudeCli` line 82; **fix at the call site** in
`src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs:555`.

`AgentConversationFactory.Create(...)` runs synchronously in `StartVoiceAsync` before the first
real await, on the UI thread. With `UseWsl=true` it blocks up to 10 s. Leave the factory
synchronous; offload the whole call:

```csharp
var resolution = await Task.Run(() => AgentConversationFactory.Create(
    config, new SecretsService(), _currentTranscriptPath(),
    tools: BuildTools(), toolHandler: SaveNote is null ? null : HandleToolCallAsync,
    notesFilePath: NotesFilePath?.Invoke())).ConfigureAwait(true);
```

(Keep `ConfigureAwait(true)` — the rest of `StartVoiceAsync` touches UI-bound VM state.)

## BLOCK-2 (+ HIGH-4, HIGH-5) — closing race, host lifecycle, double-dispose

**Files:** `src/LocalTranscriber.App/MainWindow.xaml.cs` (constructor `Closing +=` lambda,
lines 66-71), `src/LocalTranscriber.App/App.xaml.cs` (`OnStartup` 26-58, `OnExit` 60-75).

1. **MainWindow:** delete the `Closing += async (_, _) => { … _notesService.Dispose(); }` lambda
   (that explicit `_notesService.Dispose()` is HIGH-5 — the DI container already disposes the
   `NotesService` singleton). Replace with a cancel-then-reclose override:

   ```csharp
   private bool _closingConfirmed;

   protected override async void OnClosing(CancelEventArgs e)
   {
       if (_closingConfirmed) { base.OnClosing(e); return; }
       e.Cancel = true;
       try { await AgentPanel.ShutdownAsync(); await Session.ShutdownAsync(); }
       finally { _closingConfirmed = true; Close(); }
   }
   ```
   (`AgentPanel` / `Session` are the existing field names — confirm against the ctor.)

2. **App.OnStartup:** it is `protected override void` (NOT async). After `_host = builder.Build();`
   add `_host.StartAsync().GetAwaiter().GetResult();` (HIGH-4). Currently no `IHostedService` is
   registered, so this is a near no-op today but makes the host contract correct.

3. **App.OnExit:** stop before dispose (graceful hosted-service teardown):
   ```csharp
   _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
   if (_host is IAsyncDisposable asyncHost)
       asyncHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
   else _host?.Dispose();
   base.OnExit(e);
   ```

## HIGH-1 — stale `_events` bleed between sessions

**File:** `src/LocalTranscriber.Engine/RealTranscriptionEngine.cs` (`StopAsync` 384-451).
`_events` (bounded channel, line 41) is only completed in `DisposeAsync` (641), never drained in
`StopAsync`; `TranscriberService` reuses the engine (`_realEngine ??=`), so events written during
the drain surface at the top of the next session. After the final `_cts?.Cancel()` (line 415):
```csharp
// Discard events written during drain so they don't bleed into the next session.
while (_events.Reader.TryRead(out _)) { }
```

## HIGH-2 (+ MED-3) — cancelled hybrid turn re-spoken; speaker double-dispose

**File:** `src/LocalTranscriber.Voice/HybridBrainConversation.cs`.

- **HIGH-2:** `CancelTurn` (115-119) calls `_speaker?.StopSpeaking()` then `_brain.CancelTurn()`;
  the brain's cancel path raises `ResponseCompleted` → `OnBrainCompleted` (77-87) fires
  `_ = SpeakSafeAsync(text)` again. Add a suppress flag; set it in `CancelTurn` before
  `StopSpeaking`, honor it at the top of `OnBrainCompleted` (keep the existing
  `_speakerReady && _speaker is not null && text.Length > 0` guard):
  ```csharp
  private volatile bool _suppressNextSpeak;
  // CancelTurn: _suppressNextSpeak = true; _speaker?.StopSpeaking(); _brain.CancelTurn();
  // OnBrainCompleted top: if (_suppressNextSpeak) { _suppressNextSpeak = false; _reply.Clear(); return; }
  ```
- **MED-3:** both `StopAsync` (121-129) and `DisposeAsync` (131-140) call
  `_speaker.DisposeAsync()`. `_speaker` is `private readonly IReplySpeaker? _speaker;` — **remove
  `readonly`**, null it after the first dispose in `StopAsync`, and have `DisposeAsync` call
  `StopAsync()` first (then the `_speaker is not null` guard is false on the second pass).

## HIGH-3 (+ MED-2, MED-10) — Claude CLI session-ID / resume bugs

**File:** `src/LocalTranscriber.Voice/ClaudeCliConversation.cs`.

- **HIGH-3 (line 478):** `_sessionId ??= id;` → `_sessionId = id;` (CLI's reported id wins;
  `BuildArgs` pre-sets a GUID at line 381 so `??=` never fires).
- **MED-2 (`StopAsync` 608):** reset before the `if (_state is Stopped) return;` guard —
  `_firstTurn = true; _sessionId = null;`.
- **MED-10 (line 270):** `_firstTurn = false;` currently runs unconditionally before the
  exit-code check. Move it into the success `else` branch (after `exitCode != 0 || errored`).

## HIGH-6 — NotesService data race

**File:** `src/LocalTranscriber.App/Services/NotesService.cs` (`OnExternalChange`, ~line 94).
`WriteAsync` holds `_gate`; the FSW callback reads/writes `_content` without it. Acquire `_gate`
in `OnExternalChange` (keep the ~80 ms debounce), read-under-lock, release in `finally`.

## MED-1 — OCE from `TryHostIpcAsync` fails a live session

**File:** `src/LocalTranscriber.Mcp/TranscriberService.cs`. Both `StartRealSessionAsync` (line 97)
and `StartFakeSessionAsync` (line 83) call `TryHostIpcAsync` after `_ownsSession = true` with no
outer guard (the inner try only wraps server *creation*, not the `EngineIpcClient.TrySendAsync`
probe at line 167). Wrap each call:
```csharp
try { await TryHostIpcAsync(_current, cancellationToken).ConfigureAwait(false); }
catch (Exception ex) { AppLog.Warn("mcp", $"IPC host probe failed (non-fatal): {ex.Message}"); }
```

## MED-4 — CaptureHost leaks a capture handle on reconnect failure

**File:** `src/LocalTranscriber.Engine/CaptureHost.cs`. Only the **reconnect** branches
(mic 167-186, system 197-216) assign the field *after* `capture.StartAsync` — if `StartAsync`
throws, `capture` is never assigned to `_mic`/`_system` and never disposed. (The initial-start
path 66-96 assigns before start, so it's already safe.) Wrap each reconnect branch in try/finally
with ownership transfer:
```csharp
var capture = _micFactory();
try {
    if (capture.IsAvailable(captureOptions)) {
        await capture.StartAsync(captureOptions, cancellationToken).ConfigureAwait(false);
        capture.ChunkAvailable += OnMicChunk;
        _mic = capture; capture = null!;   // ownership transferred
    } else { await capture.DisposeAsync().ConfigureAwait(false); capture = null!; _addWarning("…"); }
}
finally { if (capture is not null) await capture.DisposeAsync().ConfigureAwait(false); }
```
Mirror for the system branch with `OnSystemChunk` / `_system`.

## MED-5 — MicStreamPump Start/Stop race leaves the mic open

**File:** `src/LocalTranscriber.Voice/MicStreamPump.cs` (`StartAsync` 30-46, `StopAsync` 82-101).
No lock today. Add `private readonly SemaphoreSlim _lock = new(1, 1);` and wrap the bodies of both
`StartAsync` and `StopAsync` in `await _lock.WaitAsync(...)` / `finally { _lock.Release(); }`
(keep the existing `_micStream is (not) null` short-circuits inside the lock).

## MED-6 — dead `if (engine is null)` branch / always-null `_ipcServer`

**File:** `src/LocalTranscriber.App/ViewModels/MainWindowViewModel.cs`. In DI mode `engine` is
always non-null. Remove the `if (engine is null)` block (45-50) that builds a second
`EngineIpcServer`, the `_ipcServer` field (27), and the `if (_ipcServer is not null)` block in
`ShutdownAsync` (669-672). The DI-owned `EngineIpcServer` singleton handles IPC.

## MED-7 + MED-8 — consolidate ConfigService + SQLite (FULL SHARE)

**Files:** `src/LocalTranscriber.App/App.xaml.cs`,
`src/LocalTranscriber.Engine/ServiceCollectionExtensions.cs`,
`src/LocalTranscriber.Engine/EngineFactory.cs`,
`src/LocalTranscriber.App/MainWindow.xaml.cs` (MED-9).

Today: `App.xaml.cs:30` `new ConfigService().Load()` (registers only the resulting `config`, never
the `ConfigService`); `ServiceCollectionExtensions:20` registers a *second* `ConfigService`;
`EngineFactory.CreateReal(config)` opens its own `new SqliteDatabase(config.DatabasePath)` and
builds 5 stores; `SpeakerManagementViewModel` (App line 41-42) gets no store so it opens *another*
`SqliteDatabase`. Result: 3 ConfigService instances, 2 DB handles, stale Speakers panel.

Consolidate:

1. **`EngineFactory`** — add an overload
   `CreateReal(AppConfig config, SqliteDatabase db)` that uses the passed `db` for all 5 stores
   instead of `new SqliteDatabase(...)`. Keep the existing `CreateReal(config)` delegating to it
   with a fresh `db` (CLI/MCP callers unchanged).
2. **`ServiceCollectionExtensions.AddTranscriptionCore`** — remove
   `AddSingleton<ConfigService>()` (the shared instance comes from `App`). Register:
   ```csharp
   services.AddSingleton(config);
   services.AddSingleton(sp => new SqliteDatabase(config.DatabasePath));
   services.AddSingleton<IKnownSpeakerStore>(sp =>
       new SqliteKnownSpeakerStore(sp.GetRequiredService<SqliteDatabase>()));
   services.AddSingleton<ITranscriptionEngine>(sp =>
       EngineFactory.CreateReal(config, sp.GetRequiredService<SqliteDatabase>()));
   ```
3. **`App.xaml.cs:30`** — share the bootstrap instance:
   ```csharp
   var configService = new ConfigService();
   var config = configService.Load();
   ...
   builder.Services.AddSingleton(configService);   // BEFORE AddTranscriptionCore
   builder.Services.AddTranscriptionCore(config);
   ```
   Change the `SpeakerManagementViewModel` registration (41-42) to inject the shared store:
   ```csharp
   builder.Services.AddSingleton(sp => new SpeakerManagementViewModel(
       store: sp.GetRequiredService<IKnownSpeakerStore>(),
       configService: sp.GetRequiredService<ConfigService>()));
   ```
4. **MED-9 / MainWindow** — `OnDeleteSessionRequestedAsync:116` does
   `new ConfigService().Load()`. Add `ConfigService` to the `MainWindow` ctor params and use the
   shared instance (registered in step 3).

> Verify after: `SqliteKnownSpeakerStore` ctor takes `SqliteDatabase` (confirmed
> `SqliteStores.cs:225`). Confirm `SqliteDatabase` is safe to share across the engine + VM (single
> handle is the goal); if it holds one connection, sharing is strictly better than today's two.

## USER-1 — Claude model field in Settings

**Files:** `src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs`,
`src/LocalTranscriber.App/Views/SettingsView.xaml`. `ClaudeCliAgentConfig.Model` exists
(`AgentConfig.cs:71`) but has no UI; empty defaults to Sonnet-class (slow). Add a backing field
`_claudeModel`, initialize from `config.Agent.ClaudeCli.Model` in the ctor (near line 69), and a
`ClaudeModel` property using the existing early-return `SetProperty` + `PersistConfig` pattern
(mirror `WorkspaceFolder` 362-373 → `PersistConfig(c => c.Agent.ClaudeCli.Model = value ?? "")`).
Add a labeled `TextBox` inside the `UsesClaudeBrain` panel bound to `AgentPanel.ClaudeModel`
(`UpdateSourceTrigger=LostFocus`) with a hint ("e.g. `haiku` for fast meeting help").

## USER-2 — tool-use hints while Claude works

**File:** `src/LocalTranscriber.Voice/ClaudeCliConversation.cs`, the `"assistant"` case
(481-494). Replace the text-only enumeration with a type-dispatch loop that also emits a hint for
`tool_use` blocks, and add a `ToolUseHint(string)` helper mapping `Read/Grep/Glob/Bash/Edit/
Write/WebFetch/WebSearch` → `_[Reading file…]_\n` etc. (default `_[{toolName}…]_\n`). These deltas
flow through the existing `deltas` list.

## USER-3 — explain hidden Browse button in WSL mode

**File:** `src/LocalTranscriber.App/Views/SettingsView.xaml`. Replace the single workspace-hint
`TextBlock` with two mode-specific ones bound to `AgentPanel.UseWsl` via `BoolToVisibility` /
`InverseBoolToVisibility` (confirm both converters are registered in the resource dictionary — add
the inverse if missing): WSL variant explains the Windows picker can't navigate WSL paths (type the
Linux path); native variant keeps the original hint.

## Substantive LOWs (1, 2, 4, 5, 6, 7, 9, 10)

- **LOW-1** `TranscriberService.cs:151-154` — when nothing owns the pipe and no in-process session
  exists, return `"No active session."` instead of the success message.
- **LOW-2** `Ipc/EngineIpc.cs:112-126` — after the 2 s wait, only `_cts.Dispose()` if the listener
  actually completed (guard so we don't dispose the CTS while the listener task still uses its
  token); or await completion without a swallowed timeout before disposing.
- **LOW-4** `SpeakerLabeler.cs:174` — divide by the count of *contributing* embeddings
  (increment a counter for entries whose `Dimensions == dim`), not `embeddings.Count`.
- **LOW-5** `SessionSpeakerRegistry.cs:129-136` — remove `FallbackLabel()` (no `.cs` callers).
- **LOW-6** `ClaudeCliConversation.cs:112-121` — thread `cancellationToken` from
  `SendUserTextAsync` into the turn (link it into the turn CTS) instead of discarding it.
- **LOW-7** `AgentConfig.cs:27-31` — add `"hybrid"` to the `Provider` XML doc.
- **LOW-9** `.github/workflows/ci.yml` — add a `concurrency:` block
  (`group: ${{ github.workflow }}-${{ github.ref }}`, `cancel-in-progress: true`).
- **LOW-10** `.github/workflows/ci.yml` — enable NuGet caching (`cache: true` +
  `cache-dependency-path` on `setup-dotnet@v4`, simplest given the existing `dotnet restore`).

**Skipped:** LOW-3 (already has 500 ms backoff + `maxNumberOfServerInstances=1`), LOW-8 &
LOW-11 (cosmetic/idempotent double-stop), LOW-12 (confirm-only).

## New tests (MED-11, MED-12, MED-13)

xUnit 2.5.3; test projects live under `tests/`. Follow existing style (plain `public class`,
`[Fact]`/`[Theory]`+`[InlineData]`, nested fakes implementing production interfaces —
see `tests/LocalTranscriber.Voice.Tests/ClaudeCliConversationTests.cs`).

- **MED-11** → `tests/LocalTranscriber.Voice.Tests/AgentProviderTests.cs` (reuse; it already
  references `LocalTranscriber.Shared`). Cover `AgentProviders.Parse` for
  `openai`/`claude-cli`/`hybrid`, unknown/`null`/`""` → OpenAI fallback, and `Is()` true/false per
  provider.
- **MED-12** → `tests/LocalTranscriber.Engine.Tests/CaptureHostTests.cs`. Cases: normal
  start/stop; **reconnect `StartAsync`-throws disposes the capture** (MED-4 regression guard, via a
  fake `IAudioCaptureService` factory whose `StartAsync` throws); double-stop idempotent.
- **MED-13** → `tests/LocalTranscriber.Voice.Tests/MicStreamPumpTests.cs`. Cases: normal
  start/stop; **concurrent Start+Stop leaves the mic disposed** (MED-5 regression guard, fake
  `IAgentMicStream`); cancellation mid-frame.

---

## Recommended execution order

1. BLOCK-1 (call-site `Task.Run`) — isolated.
2. BLOCK-2 + HIGH-4 + HIGH-5 — `App.xaml.cs` + `MainWindow.xaml.cs` together.
3. HIGH-1 — `RealTranscriptionEngine.StopAsync`.
4. HIGH-2 + MED-3 — `HybridBrainConversation`.
5. HIGH-3 + MED-2 + MED-10 (+ LOW-6) — `ClaudeCliConversation`.
6. HIGH-6 — `NotesService`.
7. MED-1 (+ LOW-1) — `TranscriberService`.
8. MED-4 — `CaptureHost`; MED-5 — `MicStreamPump`.
9. MED-6 + MED-7 + MED-8 + MED-9 — DI/DB consolidation (batch; highest risk — build after).
10. USER-1/2/3.
11. LOW-2, LOW-4, LOW-5, LOW-7, LOW-9, LOW-10.
12. MED-11/12/13 tests — write last; `dotnet build` + `dotnet test`.

## Verification

1. `dotnet build` — clean (no warnings-as-errors regressions).
2. `dotnet test` — ≥133 green, including the 3 new files.
3. Launch the app (`./scripts/run-with-logs.ps1 -Target app`, snapshot with
   `./scripts/tail-logs.ps1`). Smoke-test: start/stop recording; Speakers panel rename reflects in
   transcript (MED-7); open Settings, set Claude model = `haiku`, toggle WSL (Browse hint swaps,
   USER-3); with the Claude/hybrid backend, send a message that triggers a tool and confirm the
   `_[Searching code…]_` hints appear (USER-2); close the window while recording — no
   `ObjectDisposedException`, no 120 s hang (BLOCK-2).
4. Confirm the offline/no-cloud rules still hold: transcription stays local; agent/voice remain
   opt-in and off by default (no changes here touch those defaults).

## Files touched

| File | Items |
|------|-------|
| `src/LocalTranscriber.App/App.xaml.cs` | BLOCK-2, HIGH-4, MED-7, MED-8 |
| `src/LocalTranscriber.App/MainWindow.xaml.cs` | BLOCK-2, HIGH-5, MED-6*, MED-9 |
| `src/LocalTranscriber.App/ViewModels/MainWindowViewModel.cs` | MED-6 |
| `src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs` | BLOCK-1, USER-1 |
| `src/LocalTranscriber.App/Views/SettingsView.xaml` | USER-1, USER-3 |
| `src/LocalTranscriber.App/Services/NotesService.cs` | HIGH-6 |
| `src/LocalTranscriber.Engine/RealTranscriptionEngine.cs` | HIGH-1 |
| `src/LocalTranscriber.Engine/CaptureHost.cs` | MED-4 |
| `src/LocalTranscriber.Engine/EngineFactory.cs` | MED-7 (overload) |
| `src/LocalTranscriber.Engine/ServiceCollectionExtensions.cs` | MED-7, MED-8 |
| `src/LocalTranscriber.Engine/SpeakerLabeler.cs` | LOW-4 |
| `src/LocalTranscriber.Engine/SessionSpeakerRegistry.cs` | LOW-5 |
| `src/LocalTranscriber.Engine/Ipc/EngineIpc.cs` | LOW-2 |
| `src/LocalTranscriber.Mcp/TranscriberService.cs` | MED-1, LOW-1 |
| `src/LocalTranscriber.Voice/AgentConversationFactory.cs` | (BLOCK-1 fixed at call site — no change expected) |
| `src/LocalTranscriber.Voice/ClaudeCliConversation.cs` | HIGH-3, MED-2, MED-10, USER-2, LOW-6 |
| `src/LocalTranscriber.Voice/HybridBrainConversation.cs` | HIGH-2, MED-3 |
| `src/LocalTranscriber.Voice/MicStreamPump.cs` | MED-5 |
| `src/LocalTranscriber.Shared/AgentConfig.cs` | LOW-7 |
| `.github/workflows/ci.yml` | LOW-9, LOW-10 |
| *(new)* `tests/LocalTranscriber.Voice.Tests/AgentProviderTests.cs` | MED-11 |
| *(new)* `tests/LocalTranscriber.Engine.Tests/CaptureHostTests.cs` | MED-12 |
| *(new)* `tests/LocalTranscriber.Voice.Tests/MicStreamPumpTests.cs` | MED-13 |

\* `MainWindow` gets a `ConfigService` ctor param (MED-9); the `_ipcServer` removal is in the ViewModel.

## Out of scope / to mention, not change (per repo rule 1)

- LOW-3, LOW-8, LOW-11, LOW-12 (skipped by decision — noted above).
- No changes to transcription locality or agent/voice opt-in defaults.
