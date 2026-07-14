# Plan: Claude Code CLI Workspace Provider

Adopted from the merged plan (opus base + sonnet transcript-grounding + mid-turn cancel).
Anchors verified against the code on 2026-07-14, including the two load-bearing additions:
`LocalTranscriber.Voice.csproj` **does** reference `LocalTranscriber.Agent` (line 5), and
`TranscriptEventDeduplicator.TryAdd` (`TranscriptEventTailer.cs:259-284`) behaves as described.

## Context

Add a second agent backend that shells out to the external **Claude Code CLI**
(confirmed installed: v2.1.209 at `C:\Users\james.a.miller\.local\bin\claude.exe`),
launched with its working directory set to a **workspace root the user chooses**. Claude
then reads that project's `CLAUDE.md`, files, memory, and MCP tools automatically — the
assistant's "brain" is a real Claude session that already knows the project. On top of
that, we feed a **snapshot of the recent meeting transcript** into each turn so it works as
a live meeting copilot, not just a repo Q&A bot.

Decisions locked with the user:
- **Surface:** typed chat first-class, plus hybrid voice (hold-to-talk → local whisper STT →
  CLI → reply shown as captions; no spoken TTS — the app has no local TTS engine).
- **Permissions:** full agent (CLI may edit files / run commands in the workspace), gated
  behind an explicit one-time consent mirroring the mic-streaming consent pattern.
- **Workspace UI:** folder picker in Settings + a quick-switch dropdown on the meeting screen.

## Why this is feasible / the key seam

The meeting chat UI talks to the assistant only through `IRealtimeVoiceConversation`
(`src/LocalTranscriber.Voice/RealtimeVoiceOptions.cs:81`). The ViewModel holds `_voice` of
that type, sends turns via `SendUserTextAsync`, and renders replies from
`AssistantTextAvailable` + `ResponseCompleted`. A Claude-CLI backend that implements the
same interface drops into the existing chat/voice plumbing with almost no UI rewrite.
Provider selection revives the `agent.provider` selector removed in commit `7ec4ea5`
(recoverable at `7ec4ea5^` as `src/LocalTranscriber.Agent/AgentProviderFactory.cs`).

**Assembly placement (important):** the new backend lives in **`LocalTranscriber.Voice`**,
the same assembly that defines `IRealtimeVoiceConversation`. Do NOT place it in
`LocalTranscriber.Agent`: `LocalTranscriber.Voice` already references `LocalTranscriber.Agent`
(verified, `LocalTranscriber.Voice.csproj:5`), so putting a backend in `Agent` that implements a
`Voice` interface would create `Agent → Voice → Agent` — a **circular reference that does not
compile**, not merely an inelegant inversion. Placing it in `Voice` also lets it reach the
`Agent`-side transcript helpers (`TranscriptEventJsonParser`, `TranscriptEventDeduplicator`) for free.

Hard-rules check: transcription stays fully local (untouched); the agent stays opt-in and off
by default (`Provider` defaults to `"openai"`; `claude-cli` requires enabling + a workspace +
consent); edit/command capability is gated behind explicit consent — no hidden actions.

---
## 1. Config model — `src/LocalTranscriber.Shared/AgentConfig.cs`

Add to `AgentConfig`:
```csharp
public string Provider { get; set; } = "openai";   // "openai" | "claude-cli"
public ClaudeCliAgentConfig ClaudeCli { get; set; } = new();
```
New POCO in the same file (mirrors `RealtimeAgentConfig`'s style; serialized by `ConfigService`):
```csharp
public sealed class ClaudeCliAgentConfig
{
    public bool Enabled { get; set; }
    public string ExecutablePath { get; set; } = "claude";   // resolve on PATH → claude.exe on Windows
    public string WorkspaceFolder { get; set; } = "";        // process working directory (required)
    public string Model { get; set; } = "";                  // "" = CLI default; else alias e.g. "opus"
    public bool AllowEditsAndCommands { get; set; }          // stored consent for full-agent mode
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxTranscriptEvents { get; set; } = 10;       // recent transcript lines fed per turn
    public List<string> RecentWorkspaces { get; set; } = new(); // for the meeting quick-switch dropdown
}
```
`Provider` defaulting to `"openai"` preserves current behavior for existing configs.

---
## 2. New backend — `src/LocalTranscriber.Voice/ClaudeCliConversation.cs` (net-new)

Implements `IRealtimeVoiceConversation`. No existing redirected-subprocess code in the repo;
model the async stdout read loop on `TranscriptEventTailer.TailAsync`
(`src/LocalTranscriber.Agent/TranscriptEventTailer.cs`). Reuse the same STT dependency the
voice session uses: `ILocalTranscriptionService` (default `WhisperCppTranscriptionService`) +
`MicrophonePushToTalkRecorder`.

**Behavior:**
- `SendUserTextAsync(text)` and the hybrid STT result both funnel into one `RunTurnAsync(text)`.
- `RunTurnAsync` spawns the CLI one-shot per turn via `Process` / `ProcessStartInfo`:
  - `WorkingDirectory = WorkspaceFolder` (this is what "starts in the workspace root"),
    `UseShellExecute = false`, `CreateNoWindow = true`, `RedirectStandardOutput/Error = true`.
  - Args: `-p --output-format stream-json --verbose <message>`
    - first turn: `--session-id <generated-uuid>`; subsequent turns: `--resume <same-uuid>`
      (one continuous conversation per session).
    - `--model <Model>` when set.
    - Permission: when `AllowEditsAndCommands` (consent granted) →
      `--permission-mode bypassPermissions` (required so edits/bash run headless without a
      prompt that can't be answered in `-p` mode). Otherwise read-only →
      `--tools "Read,Grep,Glob"` (answers only, cannot modify).
  - Parse each stdout JSON line (stream-json): emit assistant text blocks as
    `AssistantTextAvailable` deltas; terminal `{"type":"result",...}` (and process exit) →
    `ResponseCompleted`. Capture `session_id` from the init/system line on the first turn.
  - `StateChanged`: Ready → Thinking (turn running) → Ready. `ErrorOccurred` on non-zero exit,
    stderr, timeout (`TimeoutSeconds`), or missing executable. Kill the process on Stop/Dispose;
    guard one turn at a time.
  - **Mid-turn cancellation (v1, not deferred):** because a full-agent turn can run 30s+ doing
    edits/bash, the user must be able to *interrupt the running turn* — not only via Stop/Dispose
    teardown. Expose a cancel path while `State == Thinking` that kills the child process, transitions
    back to Ready, and emits a "turn cancelled" notice (not `ErrorOccurred`). Back it with a
    per-turn `CancellationTokenSource`; the UI wires a cancel affordance to it (see §5).
  - **Verify during impl** that stream-json needs `--verbose` in this CLI version, and confirm
    the exact JSON line shapes via a quick `claude -p --output-format stream-json --verbose "hi"`.
  - **`stream-json` is the primary format** — full-agent turns (edits + bash) can run 30s+ across
    multiple steps, so streaming progress into `AssistantTextAvailable` deltas genuinely matters for
    UX. But keep **`--output-format json` (single-shot result object) as a documented fallback**: it
    is a far simpler parse, and if the stream-json line shapes surprise us during the verification
    step above, the backend can fall back to it (losing incremental deltas but still emitting one
    `AssistantTextAvailable` + `ResponseCompleted`).
- **Transcript grounding:** before building the prompt in `RunTurnAsync`, if a transcript `.jsonl`
  path is configured:
  1. Tail-read the last `MaxTranscriptEvents` lines (one-shot read from end — no tailing loop).
  2. Parse with `TranscriptEventJsonParser.TryParse` (in `TranscriptEventTailer.cs`).
  3. **Reuse the existing `TranscriptEventDeduplicator`** (`TranscriptEventTailer.cs:259`, reachable
     because `LocalTranscriber.Voice` references `LocalTranscriber.Agent`) to decide which events are
     new — do **not** hand-roll timestamp tracking. It dedupes by a content-derived id (robust to
     non-monotonic or colliding timestamps). Hold one deduplicator per session; feed each parsed
     event through `TryAdd` and prepend only those it reports as new, as:
     ```
     [Recent transcript]
     <Speaker>: <text>
     ...
     [End transcript]

     User: <text>
     ```
  This is a per-turn snapshot, far simpler than the realtime grounding loop.
- `PushToTalkDown/Up` (hybrid only): copy the hybrid branch of
  `RealtimeVoiceSession.OnPushToTalkUpAsync` (`RealtimeVoiceSession.cs:314-365`) — record →
  local whisper (`_transcriber.TranscribeAsync`) → raise `UserTextCommitted` with the recognized
  text → `RunTurnAsync(text)`. No audio leaves the machine. `pushToTalk`/`continuous` modes are
  not supported (they stream mic audio to a provider that doesn't exist here).
- **Testability seam:** inject a `Func<IClaudeProcess>` (or a stdout-line-reader delegate) the
  way `RealtimeVoiceSession` injects `transportFactory`/`transcriber`, so unit tests parse a
  captured transcript fixture without spawning `claude`.
- **Path safety:** validate `WorkspaceFolder` with `SafePathValidator` (in
  `LocalTranscriber.Shared`) before spawning; surface an error state (don't crash) on an
  invalid/nonexistent path.

---
## 3. Provider selection — `src/LocalTranscriber.Voice/AgentConversationFactory.cs` (net-new)

Thin dispatcher returning the existing `RealtimeVoiceFactory.Resolution(IRealtimeVoiceConversation?, string? Notice)` shape.
`Create(config, secrets, transcriptPath, tools, toolHandler)`:
```
switch config.Agent.Provider:
  "claude-cli":
      validate ClaudeCli.Enabled, WorkspaceFolder set & exists, executable resolvable
        → new ClaudeCliConversation(options)   (else Resolution(null, notice))
  default ("openai"):
      → RealtimeVoiceFactory.Create(...)   (unchanged path)
```

---
## 4. ViewModel — `src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs`

- In `StartVoiceAsync` (line 383): call `AgentConversationFactory.Create(...)` instead of
  `RealtimeVoiceFactory.Create` (line 393); gate the OpenAI-only line
  `config.Agent.Realtime.Enabled = true` (line 390) to the openai provider only.
- Add bound properties (each persists via the existing `PersistConfig` helper, line 657):
  `Providers` (`["openai","claude-cli"]`) + `SelectedProvider` → `c.Agent.Provider`;
  `WorkspaceFolder` → `c.Agent.ClaudeCli.WorkspaceFolder`; `RecentWorkspaces`
  (`ObservableCollection`, pushed on connect) → `c.Agent.ClaudeCli.RecentWorkspaces`.
- Relax command gating (`StartVoiceCommand` line 72): typed chat must work for claude-cli even
  with voice mode off, e.g.
  `AgentEnabled && (SelectedProvider == "claude-cli" || SelectedVoiceMode != "off")` for
  `SendTextCommand` and `StartVoiceCommand`.
- **Full-agent consent — new/generalized dialog, NOT a copy swap.** `VoiceModeConsentDialog`
  (`VoiceModeConsentDialog.xaml.cs`) is hardwired to voice-mode semantics end to end: its constructor
  takes a `requestedMode`, its radios are Hybrid/Push/Continuous, `SelectedMode` returns those strings,
  and `DialogResult` is true *only* when the user allowed **mic streaming** (`Allow_Click`, line 55).
  File-edit/command consent is a semantically different decision, so reusing it with "new copy text"
  does not work. Instead add a **sibling dialog** (e.g. `FullAgentConsentDialog`) — or generalize the
  consent control to take a title/body/allow-label and a returned boolean — modelled on the existing
  one's look and on the mic-consent flow wired in `MainWindow.xaml.cs`. Before the first start of a
  claude-cli session with edit/command capability, show it stating the CLI can modify files & run
  commands in the chosen workspace; on accept set `ClaudeCli.AllowEditsAndCommands = true` and persist.
  Until consent is granted (or if declined), the backend runs read-only (`--tools "Read,Grep,Glob"`).

---
## 5. UI

- **Settings** — `src/LocalTranscriber.App/Views/SettingsView.xaml`, "Assistant & privacy" section:
  - Provider ComboBox mirroring the Voice-mode combo → `AgentPanel.Providers` / `SelectedProvider`.
  - Workspace folder picker mirroring the Context-folder block: TextBox bound to
    `AgentPanel.WorkspaceFolder` + a Browse… button. Add `BrowseWorkspaceFolder_Click` in
    `SettingsView.xaml.cs` copying `BrowseTranscriptFolder_Click` (uses
    `Microsoft.Win32.OpenFolderDialog`).
- **Meeting quick-switch** — `src/LocalTranscriber.App/Views/MeetingView.xaml`, ASSISTANT header
  near Start/Stop: editable ComboBox `ItemsSource="{Binding AgentPanel.RecentWorkspaces}"`,
  Text/SelectedItem → `AgentPanel.WorkspaceFolder`, visible when provider is claude-cli.
- **Cancel-turn affordance (backs the mid-turn cancel in §2):** a Cancel button (or repurposed Stop)
  bound to a `CancelTurnCommand`, visible/enabled only while `State == Thinking`, that triggers the
  backend's per-turn cancellation. Without this the mid-turn kill path has no way to be invoked.

The existing message list, bubble styles, copy buttons, and input row need **no changes** —
`IRealtimeVoiceConversation` is already what the view model binds.

## 6. CLI parity (optional)

Route `LocalTranscriber.Cli/AgentCommands.cs` through `AgentConversationFactory` too, to keep
"all front-ends share logic." Mark optional — the WPF path is the primary deliverable.

**Not touched:** transcription pipeline, `LocalTranscriber.Context` (the markdown context-pack
feature is orthogonal), MCP server.

---
## Verification

1. **Build & unit tests:** `dotnet build`, `dotnet test`. Add `ClaudeCliConversationTests`
   feeding a captured stream-json transcript through the injectable process seam; assert
   `AssistantTextAvailable` deltas + a single terminal `ResponseCompleted`, session-id reuse
   across two turns (`--session-id` then `--resume`), and that transcript-grounding prepends
   only new events. No real `claude` spawn in tests.
2. **CLI shape sanity (manual, during impl):** run
   `claude -p --output-format stream-json --verbose "hello"` in a workspace; confirm JSON line
   shapes / the `--verbose` requirement the parser relies on.
3. **Manual app test** (`./scripts/run-app.ps1` or F5; watch with `./scripts/tail-logs.ps1`):
   - Settings → Assistant & privacy: Provider = claude-cli, Browse… a workspace. **Use a throwaway
     scratch repo, NOT `C:\_repos\LocalTranscriber` itself** — a consented full-agent turn can edit
     the workspace's source. (Once flows are trusted, a deliberate self-workspace run is fine.)
   - Click Start → status Active (not an API-key warning). Full-agent consent dialog appears first
     time; decline once and confirm the session runs read-only, then re-run and accept.
   - Type "What project is this?" → reply references the chosen workspace's project.
   - Type "Summarise the last few minutes" → reply references recent transcript events (fed once, not
     re-fed on the next turn — confirms the deduplicator wiring).
   - Start a long turn (e.g. "read every file and summarise"), then hit Cancel mid-turn → status
     returns to Ready with a "cancelled" notice, child process gone (check Task Manager / logs).
   - Use the meeting quick-switch dropdown to point at a second workspace; confirm the next
     reply reflects the new directory.
   - Test an invalid workspace path → error state, no crash.
4. **Regression:** with Provider = openai (default), confirm the existing OpenAI realtime
   voice/chat path is unchanged; stop, switch back to claude-cli, confirm both coexist.

## Open items / notes

- Latency: one-shot-per-turn spawns a process each turn (~1-2s cold start). Acceptable for KISS;
  a persistent `--input-format stream-json` process is a later optimization if needed.
- Transcript snapshot reads only the last `MaxTranscriptEvents` lines per turn, so bursts larger
  than that between turns lose the oldest events — accepted v1 limitation.
- Deeper transcript integration (e.g. `--append-system-prompt` or a temp context file instead of
  an inline snapshot) is a possible later enhancement; the per-turn snapshot above is the v1.
