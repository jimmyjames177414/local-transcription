# Remediation Plan: All commits 2026-07-14 and 2026-07-15

**Repo:** `C:\_repos\LocalTranscriber` · **Branch:** `main`  
**Prepared:** 2026-07-15  
**Scope:** Full audit of every commit pushed in the last 48 hours, plus three user-reported issues.

Commands to verify when done:
```powershell
dotnet build   # must be clean
dotnet test    # must stay at 133 (or more) green
```

Smoke-test each screen manually after building — the B1 WPF DI merge was never UI-verified.

---

## BLOCKERS (fix before anything else)

---

### BLOCK-1 — `WslDirectoryExists` freezes the WPF UI thread for up to 10 s

**File:** `src/LocalTranscriber.Voice/AgentConversationFactory.cs:158`  
**Introduced:** commits `31cd908`, `fcb89b4`  
**Found by:** both B2/B3 review and feature-commit review

`WslDirectoryExists` calls `p.WaitForExit(10_000)` — the synchronous overload. `AgentConversationFactory.Create` is synchronous. `AgentPanelViewModel.StartVoiceAsync` calls `Create` before its first `await`, so the code runs on the WPF UI thread. With WSL=true, clicking **Start** freezes the window for up to 10 seconds.

**Fix — make the WSL check async:**

In `AgentConversationFactory.cs`, change:

```csharp
// Before (synchronous, blocks UI):
private static bool WslDirectoryExists(string distro, string linuxPath)
{
    var psi = new ProcessStartInfo("wsl.exe") { ... };
    using var p = Process.Start(psi)!;
    p.WaitForExit(10000);
    return p.ExitCode == 0;
}
```

To:

```csharp
// After (async):
private static async Task<bool> WslDirectoryExistsAsync(
    string distro, string linuxPath, CancellationToken ct = default)
{
    var psi = new ProcessStartInfo("wsl.exe") { ... };  // same args as today
    using var p = Process.Start(psi)!;
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
    try { await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
    catch (OperationCanceledException) { return false; }
    return p.ExitCode == 0;
}
```

Make `Create` return `Task<IRealtimeVoiceConversation>` (or wrap just the WSL branch in `Task.Run`).  
In `AgentPanelViewModel.StartVoiceAsync`, `await` the factory call.

---

### BLOCK-2 — `async void` Closing handler races with `App.OnExit` host disposal

**File:** `src/LocalTranscriber.App/MainWindow.xaml.cs:66`  
**Introduced:** commit `eb69fb0` (B1)  
**Found by:** B1 DI review

`Closing += async (_, _) =>` is `async void`. WPF fires it, sees `void`, closes the window, and immediately runs `App.OnExit` → `_host.DisposeAsync()` on the UI thread while the async body is still running on the thread pool. `DisposeAsync` calls `_control.Dispose()` while `Session.ShutdownAsync()` may still be waiting on `_control.WaitAsync()` — `ObjectDisposedException` is thrown and re-posted to the WPF dispatcher as an unhandled exception. When recording is active the UI thread blocks in `GetAwaiter().GetResult()` for up to 120 s.

**Fix — use `OnClosing` override with cancel-then-reclose pattern:**

Remove the `Closing +=` lambda from `MainWindow` constructor. Add:

```csharp
private bool _closingConfirmed;

protected override async void OnClosing(CancelEventArgs e)
{
    if (_closingConfirmed) { base.OnClosing(e); return; }
    e.Cancel = true;
    try
    {
        await AgentPanel.ShutdownAsync();
        await Session.ShutdownAsync();
    }
    finally
    {
        _closingConfirmed = true;
        Close();   // re-enters OnClosing; guard skips async work
    }
}
```

Also remove `_notesService.Dispose()` from the old Closing handler body (see HIGH-3).

Update `App.OnExit` to call `StopAsync` before `DisposeAsync` (guarantees engine is stopped before DI container disposes it):

```csharp
protected override void OnExit(ExitEventArgs e)
{
    _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    (_host as IAsyncDisposable)?.DisposeAsync().AsTask().GetAwaiter().GetResult()
        ?? _host?.Dispose();
    base.OnExit(e);
}
```

---

## HIGH SEVERITY

---

### HIGH-1 — `_events` channel not drained between sessions; stale events bleed into next session

**File:** `src/LocalTranscriber.Engine/RealTranscriptionEngine.cs:41`  
**Introduced:** commit `87243cc` (PR #1 bounded-channel change)  
**Found by:** PR #1 review

`_events` is a `readonly` field. `_events.Writer.TryComplete()` is only called in `DisposeAsync`, not `StopAsync`. When a session ends: (1) the UI consumer is cancelled, (2) `StopAsync` drains the window queue — but events written during drain sit in `_events`. Because `TranscriberService` reuses `_realEngine` across sessions (`??=`), these stale events surface at the top of the next session's transcript.

**Fix — flush `_events` at the end of `StopAsync`, after cancelling `_cts`:**

After the existing `_cts.Cancel()` call in `StopAsync`, add:

```csharp
// Discard any events written during the drain phase so they don't
// bleed into the next session's consumer.
while (_events.Reader.TryRead(out _)) { }
```

---

### HIGH-2 — Cancelled hybrid turn is spoken again after `StopSpeaking`

**File:** `src/LocalTranscriber.Voice/HybridBrainConversation.cs:85`  
**Introduced:** commit `a2af7d0`  
**Found by:** feature-commit review

`CancelTurn()` calls `_speaker.StopSpeaking()` first, then `_brain.CancelTurn()`. The brain's cancellation catch-block raises `AssistantTextAvailable` + `ResponseCompleted` synchronously. `OnBrainCompleted` then calls `SpeakSafeAsync` with `_cts.Token` — which is **not** the turn-level token. Audio starts again after `StopSpeaking` already returned.

**Fix — add a suppress flag:**

```csharp
private volatile bool _suppressNextSpeak;

public void CancelTurn()
{
    _suppressNextSpeak = true;
    _speaker?.StopSpeaking();
    _brain.CancelTurn();
}

private async Task OnBrainCompleted(...)
{
    if (_suppressNextSpeak) { _suppressNextSpeak = false; return; }
    await SpeakSafeAsync(_reply.ToString()).ConfigureAwait(false);
    _reply.Clear();
}
```

---

### HIGH-3 — `_sessionId ??= id` in `ParseLine` is always a no-op; `--resume` uses wrong ID

**File:** `src/LocalTranscriber.Voice/ClaudeCliConversation.cs:478`  
**Introduced:** commit `31cd908`  
**Found by:** feature-commit review

`BuildArgs` (line 381) pre-sets `_sessionId = Guid.NewGuid().ToString()` before any output is parsed, so the null-coalescing `??=` on `ParseLine:478` never fires. If the CLI assigns its own canonical session ID (distinct from the UUID passed via `--session-id`), every `--resume <our-uuid>` call targets an unknown session and all turns after the first fail.

**Fix:**

```csharp
// ParseLine:478 — change:
_sessionId ??= id;
// to:
_sessionId = id;   // CLI's reported ID always wins
```

---

### HIGH-4 — `_host.StartAsync()` never called; `IHostedService` silently broken

**File:** `src/LocalTranscriber.App/App.xaml.cs:54`  
**Introduced:** commit `eb69fb0` (B1)  
**Found by:** B1 DI review

The host is built and services are manually resolved, but `StartAsync` is never called. Any future `IHostedService` registration will silently never run. `StopAsync` is also never called (only `DisposeAsync`), which skips the graceful hosted-service teardown.

**Fix — add to `OnStartup` after `_host = builder.Build()`:**

```csharp
await _host.StartAsync();
```

`OnStartup` is already `async override void`, so `await` is valid.

`StopAsync` is covered by the `App.OnExit` change in BLOCK-2.

---

### HIGH-5 — `NotesService` double-disposed (DI container + explicit call)

**File:** `src/LocalTranscriber.App/MainWindow.xaml.cs:71`  
**Introduced:** commit `eb69fb0` (B1)  
**Found by:** B1 DI review

`NotesService` is registered as a DI singleton; the container calls `Dispose()` during host disposal. The old `Closing +=` handler also calls `_notesService.Dispose()` explicitly. The second call throws `ObjectDisposedException` on `_gate.Dispose()`.

**Fix:** Remove `_notesService.Dispose()` from the old Closing handler. The BLOCK-2 fix removes the entire Closing lambda; `NotesService` cleanup is handled by the DI container through `OnExit`.

---

### HIGH-6 — `NotesService.OnExternalChange` reads and writes `_content` outside the semaphore

**File:** `src/LocalTranscriber.App/Services/NotesService.cs:94`  
**Introduced:** commit `a2af7d0`  
**Found by:** feature-commit review

`WriteAsync` holds `_gate` (a `SemaphoreSlim`) when updating `_content`. `OnExternalChange` (a `FileSystemWatcher` callback on a thread-pool thread) reads and writes `_content` without acquiring `_gate`. The hybrid backend creates exactly this race: Claude CLI writes the file (FSW fires) while the OpenAI tool-call path calls `WriteAsync`. One update is lost; `_content` diverges from disk.

**Fix — acquire `_gate` in `OnExternalChange`:**

```csharp
private async void OnExternalChange(object sender, FileSystemEventArgs e)
{
    await Task.Delay(80).ConfigureAwait(false);   // debounce (same as before)
    await _gate.WaitAsync().ConfigureAwait(false);
    try
    {
        var text = await TryReadAsync().ConfigureAwait(false);
        if (text is null || text == _content) return;
        _content = text;
        _lastWrite = DateTimeOffset.UtcNow;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }
    finally { _gate.Release(); }
}
```

---

## MEDIUM SEVERITY

---

### MED-1 — OCE from `TryHostIpcAsync` propagates after session is already started

**File:** `src/LocalTranscriber.Mcp/TranscriberService.cs:97`  
**Introduced:** commit `87243cc` (PR #1)  
**Found by:** PR #1 review

`StartRealSessionAsync` sets `_ownsSession = true` and starts the engine (line 94–96), then calls `TryHostIpcAsync` (line 97). If the caller's `CancellationToken` fires during the 1-second pipe-probe window inside `TryHostIpcAsync`, the `OperationCanceledException` propagates to the outer catch that formats it as `"Failed to start: The operation was canceled."` — but the session IS live. The MCP caller receives a failure message for a running session.

**Fix — wrap `TryHostIpcAsync` in its own try/catch in both `Start*` methods:**

```csharp
try { await TryHostIpcAsync(cancellationToken).ConfigureAwait(false); }
catch (Exception ex) { AppLog.Warn("mcp", $"IPC host probe failed (non-fatal): {ex.Message}"); }
```

---

### MED-2 — `_firstTurn` and `_sessionId` not reset in `StopAsync`; restart resumes dead session

**File:** `src/LocalTranscriber.Voice/ClaudeCliConversation.cs:608`  
**Introduced:** commit `31cd908`  
**Found by:** feature-commit review

`StopAsync` cancels the CTS but does not reset `_firstTurn = true` or `_sessionId = null`. A subsequent `StartAsync` on the same instance hits the resume branch with a stale ID that the CLI no longer recognises. Current callers always create a fresh instance per start, masking the bug.

**Fix — reset at the start of `StopAsync` (before the early-return guard):**

```csharp
public async Task StopAsync(CancellationToken cancellationToken = default)
{
    _firstTurn = true;
    _sessionId = null;
    if (_state is RealtimeVoiceState.Stopped) return;
    // ... rest unchanged
}
```

---

### MED-3 — `HybridBrainConversation` speaker double-disposed via `StopAsync` + `DisposeAsync`

**File:** `src/LocalTranscriber.Voice/HybridBrainConversation.cs:127` and `:137`  
**Introduced:** commit `a2af7d0`  
**Found by:** feature-commit review

Both `StopAsync` and `DisposeAsync` call `_speaker.DisposeAsync()`. `RealtimeSpeaker` holds a live WebSocket and is not idempotent; the second dispose may throw `ObjectDisposedException` or attempt a second network teardown.

**Fix — null out after first dispose:**

```csharp
public async Task StopAsync(...)
{
    if (_state is RealtimeVoiceState.Stopped) return;
    _sessionCts?.Cancel();
    await _brain.StopAsync(cancellationToken).ConfigureAwait(false);
    if (_speaker is { } s) { _speaker = null; await s.DisposeAsync().ConfigureAwait(false); }
    SetState(RealtimeVoiceState.Stopped);
}

public async ValueTask DisposeAsync()
{
    await StopAsync().ConfigureAwait(false);
    _sessionCts?.Dispose();
    // _speaker is already null here (StopAsync null'd it)
}
```

---

### MED-4 — `CaptureHost` leaks `IAudioCaptureService` when `StartAsync` throws during reconnect

**File:** `src/LocalTranscriber.Engine/CaptureHost.cs:172` (mic) and `:202` (system)  
**Introduced:** commit `87243cc` (PR #1, SpeakerLabeler extraction/god-class split)  
**Found by:** B3 review

If `capture.StartAsync` throws, `capture` is not yet assigned to `_mic`, so `StopAsync` never calls `capture.DisposeAsync()`. The WASAPI handle leaks until process exit.

**Fix — wrap in try/finally with ownership transfer pattern:**

```csharp
var capture = _micFactory();
try
{
    if (capture.IsAvailable(captureOptions))
    {
        await capture.StartAsync(captureOptions, cancellationToken).ConfigureAwait(false);
        capture.ChunkAvailable += OnMicChunk;
        _mic = capture;
        capture = null!;   // ownership transferred; don't dispose in finally
    }
}
finally
{
    if (capture is not null)
        await capture.DisposeAsync().ConfigureAwait(false);
}
```

Apply the same pattern to the system-audio branch (~line 202).

---

### MED-5 — `MicStreamPump` has a Start/Stop race that leaves the mic device open

**File:** `src/LocalTranscriber.Voice/MicStreamPump.cs:31` and `:82`  
**Introduced:** commit `097a0a7` (B3)  
**Found by:** B3 review

No lock between `StartAsync` and `StopAsync`. If `StopAsync` is called in the window after the null-check but before `_micStream = _micStreamFactory()`, `StopAsync` sees null and returns. `StartAsync` then assigns the stream, starts the pump. The pump exits via `OperationCanceledException` (session cancelled) but `_micStream.StopAsync()` / `DisposeAsync()` are never called — mic handle stays open.

**Fix — add a `SemaphoreSlim(1, 1)` guard:**

```csharp
private readonly SemaphoreSlim _lock = new(1, 1);

public async Task StartAsync(CancellationToken ct = default)
{
    await _lock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        if (_micStream is not null) return;
        // ... existing body
    }
    finally { _lock.Release(); }
}

public async Task StopAsync()
{
    await _lock.WaitAsync().ConfigureAwait(false);
    try
    {
        if (_micStream is null) return;
        // ... existing body
    }
    finally { _lock.Release(); }
}
```

---

### MED-6 — Dead `if (engine is null)` branch and always-null `_ipcServer` in `MainWindowViewModel`

**File:** `src/LocalTranscriber.App/ViewModels/MainWindowViewModel.cs:46`  
**Introduced:** commit `eb69fb0` (B1)  
**Found by:** B1 DI review

In DI mode `engine` is never null, so the fallback that creates a second `IPC server` + `_ipcServer` is unreachable. `_ipcServer` is always `null`; the `if (_ipcServer is not null)` in `ShutdownAsync` never executes. Misleads readers about IPC ownership.

**Fix:** Remove the `if (engine is null)` block (~lines 46–50), the `_ipcServer` field, and the matching null-check in `ShutdownAsync`. The DI-owned `EngineIpcServer` singleton handles IPC.

---

### MED-7 — `SpeakerManagementViewModel` registered without `IKnownSpeakerStore`; owns its own SQLite connection

**File:** `src/LocalTranscriber.App/App.xaml.cs:41`  
**Introduced:** commit `eb69fb0` (B1)  
**Found by:** B1 DI review

Null-store fallback creates a separate `SqliteDatabase` (second open connection). Speaker renames via transcript don't show in the Speakers panel until manual refresh; two connections to the same SQLite file can cause lock contention.

**Fix:** Expose `IKnownSpeakerStore` from `ServiceCollectionExtensions` as a singleton and inject it:

```csharp
// In ServiceCollectionExtensions.cs:
services.AddSingleton<IKnownSpeakerStore>(sp =>
    new SqliteSpeakerStore(sp.GetRequiredService<AppConfig>().DatabasePath));
services.AddSingleton<ITranscriptionEngine>(sp =>
    EngineFactory.CreateReal(sp.GetRequiredService<AppConfig>(),
                             sp.GetRequiredService<IKnownSpeakerStore>()));

// In App.xaml.cs:
builder.Services.AddSingleton(sp => new SpeakerManagementViewModel(
    sp.GetRequiredService<ConfigService>(),
    sp.GetRequiredService<IKnownSpeakerStore>()));
```

This requires `EngineFactory.CreateReal` to accept an injected store. Check the existing signature and adapt as needed.

---

### MED-8 — Two separate `ConfigService` instances

**File:** `src/LocalTranscriber.App/App.xaml.cs:30` + `src/LocalTranscriber.Engine/ServiceCollectionExtensions.cs:20`  
**Introduced:** commit `eb69fb0` (B1)  
**Found by:** B1 DI review

A pre-host `new ConfigService()` loads `AppConfig` for bootstrapping. `ServiceCollectionExtensions` then registers a second `new ConfigService()` via DI. ViewModels get the second instance. Runtime config saves through the first instance are invisible to the second.

**Fix — share the instance:**

```csharp
// App.xaml.cs:
var configService = new ConfigService();
var config = configService.Load();
builder.Services.AddSingleton(configService);
builder.Services.AddSingleton(config);

// ServiceCollectionExtensions.cs — remove new ConfigService(); accept injected AppConfig:
services.AddSingleton<ITranscriptionEngine>(sp =>
    EngineFactory.CreateReal(sp.GetRequiredService<AppConfig>()));
```

---

### MED-9 — `OnDeleteSessionRequestedAsync` constructs `ConfigService` outside DI

**File:** `src/LocalTranscriber.App/MainWindow.xaml.cs:116`  
**Introduced:** commit `eb69fb0` (B1)  
**Found by:** B1 DI review

```csharp
var config = new LocalTranscriber.Storage.ConfigService().Load();
```

Third `ConfigService` instance; runtime config changes (e.g., changed transcript folder) don't affect delete behavior.

**Fix:** Add `ConfigService` to `MainWindow` constructor parameters (already available after MED-8) and use the shared instance.

---

### MED-10 — `_firstTurn = false` set on non-zero exit; next turn resumes dead session

**File:** `src/LocalTranscriber.Voice/ClaudeCliConversation.cs:270`  
**Introduced:** commit `31cd908`  
**Found by:** feature-commit review

`_firstTurn = false` is set unconditionally before the exit-code check. If the first CLI invocation fails (auth error, bad model), `_firstTurn` is still flipped. Retry issues `--resume <uuid>` against a nonexistent session; all subsequent turns fail.

**Fix — only flip inside success branch:**

```csharp
if (exitCode != 0 || errored)
{
    ErrorOccurred?.Invoke(this, ...);
}
else
{
    _firstTurn = false;    // moved here
    ResponseCompleted?.Invoke(this, EventArgs.Empty);
}
```

---

### MED-11 — No unit tests for `AgentProviders.Parse` / `AgentProviders.Is`

**Introduced:** commit `1d5617a` (B2)  
**Found by:** B2/B3 review

B2's core contract — all three provider strings round-trip correctly, unknown strings fall back to OpenAI, `Is()` is correct — has zero test coverage. A typo in a provider constant string is invisible until runtime.

**Add:** `src/LocalTranscriber.Shared.Tests/AgentProviderTests.cs`

Cover: `Parse("openai/claude-cli/hybrid")`, unknown strings → OpenAI fallback, `null`/`""` → OpenAI fallback, `Is()` true/false for each combination.

---

### MED-12 — No isolated unit tests for `CaptureHost`

**Introduced:** commit `87243cc` (PR #1 extraction)  
**Found by:** B3 review

The MED-4 resource-leak path, double-stop idempotency, and watchdog-reconnect isolation are not covered by any test that targets `CaptureHost` directly.

**Add:** `src/LocalTranscriber.Engine.Tests/CaptureHostTests.cs`  
Key cases: normal start/stop, `StartAsync`-throws resource-cleanup (MED-4 regression guard), double-stop idempotent, watchdog reconnects.

---

### MED-13 — No isolated unit tests for `MicStreamPump`

**Introduced:** commit `097a0a7` (B3)  
**Found by:** B3 review

The MED-5 Start/Stop race is not covered by any test.

**Add:** `src/LocalTranscriber.Voice.Tests/MicStreamPumpTests.cs`  
Key cases: normal start/stop, concurrent Start+Stop (MED-5 regression guard), cancellation mid-frame.

---

## USER-REPORTED ISSUES (do alongside remediation)

---

### USER-1 — Expose Claude model field in Settings UI (fix slow responses)

**Files:** `src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs`, `src/LocalTranscriber.App/Views/SettingsView.xaml`

`ClaudeCliAgentConfig.Model` exists in config but has no UI. Empty model defaults to Sonnet-class (10–30 s/turn). Haiku is ~5–10× faster.

**ViewModel — add field + property (near `WorkspaceFolder`):**

```csharp
private string _claudeModel;
// In constructor: _claudeModel = config.Agent.ClaudeCli.Model;

public string ClaudeModel
{
    get => _claudeModel;
    set
    {
        if (!SetProperty(ref _claudeModel, value)) return;
        PersistConfig(c => c.Agent.ClaudeCli.Model = value ?? "");
    }
}
```

**XAML — inside `UsesClaudeBrain` StackPanel after workspace folder block:**

```xml
<TextBlock Text="Claude model" Style="{StaticResource FieldLabel}" />
<TextBox Style="{StaticResource PathInputStyle}" Margin="0,0,0,4"
         Text="{Binding AgentPanel.ClaudeModel, UpdateSourceTrigger=LostFocus}" />
<TextBlock FontSize="10" Foreground="{StaticResource Brush.Text.Hint}" Margin="0,0,0,12" TextWrapping="Wrap"
           Text="Model alias passed to --model (e.g. haiku for fast meeting help, sonnet for deeper analysis). Leave blank for the CLI default." />
```

---

### USER-2 — Emit tool-use hints while Claude works (AI acknowledges it's looking something up)

**File:** `src/LocalTranscriber.Voice/ClaudeCliConversation.cs`

Tool-use content blocks are silently dropped; the user sees nothing between sending a message and receiving the full reply. Replace the text-only loop inside the `"assistant"` case of `ParseLine`:

```csharp
foreach (var block in content.EnumerateArray())
{
    if (!block.TryGetProperty("type", out var bt)) continue;
    string? blockType = bt.GetString();

    if (blockType == "text"
        && block.TryGetProperty("text", out var txt)
        && txt.GetString() is { Length: > 0 } text)
    {
        deltas.Add(text);
    }
    else if (blockType == "tool_use"
        && block.TryGetProperty("name", out var nameEl)
        && nameEl.GetString() is { } toolName)
    {
        deltas.Add(ToolUseHint(toolName));
    }
}
```

Add helper:

```csharp
private static string ToolUseHint(string toolName) => toolName switch
{
    "Read"      => "_[Reading file…]_\n",
    "Grep"      => "_[Searching code…]_\n",
    "Glob"      => "_[Finding files…]_\n",
    "Bash"      => "_[Running command…]_\n",
    "Edit"      => "_[Editing file…]_\n",
    "Write"     => "_[Writing file…]_\n",
    "WebFetch"  => "_[Fetching URL…]_\n",
    "WebSearch" => "_[Searching web…]_\n",
    _           => $"_[{toolName}…]_\n",
};
```

---

### USER-3 — WSL workspace: explain why Browse button is hidden

**File:** `src/LocalTranscriber.App/Views/SettingsView.xaml`

Replace the single hint TextBlock after the workspace Grid with two mode-specific ones:

```xml
<!-- WSL mode: accent-coloured explanation -->
<TextBlock Margin="0,0,0,12" TextWrapping="Wrap" FontSize="10"
           Foreground="{StaticResource Brush.Accent.Text}"
           Visibility="{Binding AgentPanel.UseWsl, Converter={StaticResource BoolToVisibility}}">
    WSL mode: the Browse button is hidden because the Windows folder picker cannot navigate WSL
    paths. Type the Linux path directly above (e.g. /home/you/repos/myproject).
</TextBlock>
<!-- Native mode: original hint -->
<TextBlock Margin="0,0,0,12" TextWrapping="Wrap" FontSize="10"
           Foreground="{StaticResource Brush.Text.Hint}"
           Visibility="{Binding AgentPanel.UseWsl, Converter={StaticResource InverseBoolToVisibility}}">
    The CLI runs here and reads that project's CLAUDE.md, files, memory, and MCP tools.
    Full-agent mode asks for consent on first connect, then can edit files and run commands here.
</TextBlock>
```

---

## LOW SEVERITY (fix opportunistically)

| ID | File | Line | Issue |
|----|------|------|-------|
| LOW-1 | `TranscriberService.cs` | 151–154 | `ControlAsync` no-op returns success string when no session exists; should return `"No active session."` |
| LOW-2 | `Ipc/EngineIpc.cs` | 119–126 | `DisposeAsync` disposes CTS while listener task may still be alive; switch to `CancelAsync()` + bounded wait before dispose |
| LOW-3 | `Ipc/EngineIpc.cs` | 39–68 | IOException-spin if two MCP processes race to own the pipe; harmless in practice (single-process deploys) |
| LOW-4 | `SpeakerLabeler.cs` | 174 | Average divisor uses `embeddings.Count` but skips mismatched-dim entries; should count contributing embeddings |
| LOW-5 | `SessionSpeakerRegistry.cs` | 130 | `FallbackLabel()` is dead code after B3 extraction; remove |
| LOW-6 | `ClaudeCliConversation.cs` | 183 | `SendUserTextAsync` discards `cancellationToken`; thread it into `RunTurnAsync` via linked CTS |
| LOW-7 | `AgentConfig.cs` | 28 | XML doc for `Provider` omits `"hybrid"`; add it |
| LOW-8 | `RealTranscriptionEngine.cs` | 638 | `DisposeAsync` calls `StopAsync()` then `_captureHost.DisposeAsync()` which calls `StopAsync()` a second time; remove the redundant call |
| LOW-9 | `.github/workflows/ci.yml` | 7 | Every PR commit fires CI twice (push + PR events); add `concurrency: group: … cancel-in-progress: true` |
| LOW-10 | `.github/workflows/ci.yml` | 25 | No NuGet cache; add `actions/cache` on `~/.nuget/packages` |
| LOW-11 | `App.xaml.cs` | 55 | `EngineIpcServer` stays live during async cleanup window; cosmetic timing issue, harmless |
| LOW-12 | `App.xaml.cs` + `ServiceCollectionExtensions.cs` | — | `Microsoft.Extensions.Hosting` 10.0.9 on `net8.0-windows`; confirm intentional before locking |

---

## Recommended execution order

```
BLOCK-1  (WSL UI freeze)            ← touches AgentConversationFactory only; independent
BLOCK-2  (async void close)         ← touches MainWindow + App; do with HIGH-4 and HIGH-5
HIGH-1   (events bleed)             ← touches RealTranscriptionEngine; independent
HIGH-2   (cancelled turn re-spoken) ← touches HybridBrainConversation; independent
HIGH-3   (session ID ??= no-op)     ← touches ClaudeCliConversation; do with MED-2, MED-10
HIGH-4   (_host.StartAsync)         ← part of BLOCK-2 batch
HIGH-5   (NotesService dbl-dispose) ← resolved by BLOCK-2 fix
HIGH-6   (NotesService data race)   ← touches NotesService; independent
MED-1    (OCE in TryHostIpcAsync)   ← one-liner; do quickly
MED-3    (speaker double-dispose)   ← touches HybridBrainConversation; do with HIGH-2
MED-4    (CaptureHost leak)         ← touches CaptureHost; independent
MED-5    (MicStreamPump race)       ← touches MicStreamPump; independent
MED-6–9  (DI cleanups)             ← batch together; touches App.xaml.cs / MainWindow
USER-1,2,3                          ← independent of everything; do in one pass
MED-11–13 (new tests)              ← write last; run dotnet test to confirm green
LOW-*    (opportunistic)            ← fill in as you go
```

---

## Files touched

| File | Changes |
|------|---------|
| `src/LocalTranscriber.App/App.xaml.cs` | BLOCK-2, HIGH-4, MED-7, MED-8 |
| `src/LocalTranscriber.App/MainWindow.xaml.cs` | BLOCK-2, HIGH-5, MED-6, MED-9 |
| `src/LocalTranscriber.App/ViewModels/MainWindowViewModel.cs` | MED-6 |
| `src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs` | USER-1 |
| `src/LocalTranscriber.App/Views/SettingsView.xaml` | USER-1, USER-3 |
| `src/LocalTranscriber.App/Services/NotesService.cs` | HIGH-6 |
| `src/LocalTranscriber.Engine/RealTranscriptionEngine.cs` | HIGH-1, LOW-8 |
| `src/LocalTranscriber.Engine/CaptureHost.cs` | MED-4 |
| `src/LocalTranscriber.Engine/ServiceCollectionExtensions.cs` | MED-7, MED-8 |
| `src/LocalTranscriber.Engine/SpeakerLabeler.cs` | LOW-4 |
| `src/LocalTranscriber.Engine/SessionSpeakerRegistry.cs` | LOW-5 |
| `src/LocalTranscriber.Mcp/TranscriberService.cs` | MED-1, LOW-1 |
| `src/LocalTranscriber.Engine/Ipc/EngineIpc.cs` | LOW-2, LOW-3 |
| `src/LocalTranscriber.Voice/AgentConversationFactory.cs` | BLOCK-1 |
| `src/LocalTranscriber.Voice/ClaudeCliConversation.cs` | HIGH-3, MED-2, MED-10, USER-2, LOW-6 |
| `src/LocalTranscriber.Voice/HybridBrainConversation.cs` | HIGH-2, MED-3 |
| `src/LocalTranscriber.Voice/MicStreamPump.cs` | MED-5 |
| `src/LocalTranscriber.Shared/AgentConfig.cs` | LOW-7 |
| `.github/workflows/ci.yml` | LOW-9, LOW-10 |
| *(new)* `src/LocalTranscriber.Shared.Tests/AgentProviderTests.cs` | MED-11 |
| *(new)* `src/LocalTranscriber.Engine.Tests/CaptureHostTests.cs` | MED-12 |
| *(new)* `src/LocalTranscriber.Voice.Tests/MicStreamPumpTests.cs` | MED-13 |
