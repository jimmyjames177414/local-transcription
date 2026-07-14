# Plan: Claude CLI Workspace Provider

## Context

The current agent layer only supports OpenAI Realtime (voice) or null (off). The user wants a second provider that spawns the `claude` CLI as a subprocess with `--cwd` set to a workspace directory. This means Claude Code reads `CLAUDE.md`, memory files, and MCP configs from that directory automatically — no manual curation of `context/*.md` files required. The agent panel then works as a live meeting copilot backed by a real Claude session that already "knows" the project.

---

## Architecture

One-shot subprocess per message with session resumption:

```
StartAsync  →  claude -p "<init prompt>" --cwd <workspace> --output-format json
                → parse session_id from response JSON, store in memory

SendUserTextAsync(text)
            →  claude -p "[Transcript context]\n\nUser: <text>"
                      --resume <session_id> --cwd <workspace> --output-format json
                → parse result, fire AssistantTextAvailable

StopAsync   →  clear session_id
```

Response JSON from `claude -p --output-format json`:
```json
{"type":"result","subtype":"success","result":"...","session_id":"abc123","total_cost_usd":0.001}
```

`ClaudeCliSession` implements `IRealtimeVoiceConversation` so it slots into the existing UI without changes to `MeetingView.xaml`'s message list, bubbles, or copy buttons. Audio methods (`PushToTalkDown/Up`) are no-ops; `UserSpeechTranscribed` and `SpeakReplies` are unused.

---

## Files to Create

### `src/LocalTranscriber.Agent/Claude/ClaudeCliOptions.cs`
```
ClaudeCliOptions (sealed record)
  WorkspacePath        string   (required — the --cwd directory)
  ClaudeBinaryPath     string   = "claude"
  Model                string   = ""   (empty → let claude pick)
  MaxTranscriptEvents  int      = 10
  TranscriptJsonlPath  string?  (optional — path to tailing .jsonl)
```

### `src/LocalTranscriber.Agent/Claude/ClaudeCliSession.cs`
- Implements `IRealtimeVoiceConversation`
- `_sessionId` (string?) — null until first message succeeds
- `StartAsync`: sends a brief init message; sets `State = Active` on success, `Error` on failure (e.g. claude not on PATH)
- `SendUserTextAsync(text)`: builds prompt = `[recent transcript events, if any] + "\n\nUser: " + text`; runs subprocess; fires `AssistantTextAvailable` with result; fires `ResponseCompleted`
- `StopAsync`: clears `_sessionId`, sets `State = Idle`
- `StateChanged` / `ErrorOccurred` / `AssistantTextAvailable` events wired to UI
- Private `RunClaudeAsync(prompt)`: starts `Process`, waits for exit, parses JSON, returns `(result, sessionId)` — throws `InvalidOperationException` on non-success subtype

---

## Files to Modify

### `src/LocalTranscriber.Shared/AgentConfig.cs`
Add `ClaudeCliConfig` class and a `ClaudeCli` property on `AgentConfig`:
```csharp
public sealed class ClaudeCliConfig
{
    public bool   Enabled       { get; set; } = false;
    public string WorkspacePath { get; set; } = "";
    public string Model         { get; set; } = "";
}
// on AgentConfig:
public ClaudeCliConfig ClaudeCli { get; set; } = new();
```

### `src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs`
- Add `ClaudeCliEnabled` (bool), `ClaudeCliWorkspace` (string) properties — backed by `config.Agent.ClaudeCli`, persisted via `PersistConfig`
- In `StartVoiceAsync()`: if `ClaudeCliEnabled`, construct `ClaudeCliOptions` from config and `new ClaudeCliSession(options, transcriptJSONLPath)` instead of going through `RealtimeVoiceFactory`; wire events same way as the existing `resolution.Session` path
- Add a `BrowseWorkspaceCommand` (relay command) that opens a `FolderBrowserDialog` and sets `ClaudeCliWorkspace`

### `src/LocalTranscriber.App/Views/MeetingView.xaml`
In the footer row (Row 3 of the assistant column), add a new sub-row or secondary controls visible only when the user expands the existing "devices ▾" popup — or just add them inline next to the mode ComboBox:
- A `CheckBox` "Claude CLI" bound to `ClaudeCliEnabled`
- A `TextBox` + `Button` ("…") for workspace path, visible only when `ClaudeCliEnabled = true`

The existing message list, bubble styles, and input row need **no changes** — `IRealtimeVoiceConversation` is already what the view model binds.

---

## Transcript Grounding

`ClaudeCliSession` receives `TranscriptJsonlPath`. In `SendUserTextAsync`, before building the prompt:
1. Read the last `MaxTranscriptEvents` lines from the `.jsonl` file (tail from end — no tailing loop needed, just a one-shot read)
2. If any new events since last message, prepend as:
   ```
   [Recent transcript]
   <Speaker>: <text>
   ...
   [End transcript]
   ```
3. Track the last-seen event timestamp to avoid re-injecting the same lines

This is much simpler than the realtime grounding loop because we only need the snapshot at message-send time.

---

## Reuse — existing utilities to call

| Utility | Location | Usage |
|---|---|---|
| `TranscriptEventJsonParser.TryParse` | `src/LocalTranscriber.Agent/TranscriptEventTailer.cs` | Parse `.jsonl` lines for transcript context |
| `SafePathValidator.ResolveInsideRoot` | `src/LocalTranscriber.Shared/` | Validate workspace path before use |
| `PersistConfig` helper | `AgentPanelViewModel` | Persist new config properties |
| `IRealtimeVoiceConversation` | `src/LocalTranscriber.Voice/RealtimeVoiceOptions.cs` | Interface to implement |

---

## Verification

1. Build: `dotnet build` — no errors
2. Run app: `./scripts/run-app.ps1`
3. In Agent Panel footer, check "Claude CLI", set workspace to `C:\_repos\LocalTranscriber`
4. Click Start — status should show Active (not an API-key warning)
5. Type "What project is this?" in the chat input — response should reference GymSpots or LocalTranscriber depending on which workspace was set
6. Type "Summarise the last few minutes" — response should reference recent transcript events
7. Stop session, uncheck Claude CLI, confirm regular OpenAI path still works
8. Test with invalid workspace path — should show error state, not crash
