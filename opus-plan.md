 Plan: Talk to a Claude Code CLI instance running in a chosen workspace
                                                                        ↑
 Context

 The user wants the LocalTranscriber assistant's brain and context to be a Claude Code CLI                                                        ↑
 instance launched in a workspace root they choose — i.e. "talk to claude as if it started in
 the workspace root, so context comes from the Claude CLI instance." Thi↑ is not a change to the
 existing markdown context-pack folder; it is a new agent backend that shells out to the
 external claude CLI (confirmed installed: v2.1.209 at C:\Users\james.a.miller\.local\bin\claude.exe),
 running with its working directory set to the workspace so Claude reads that project's CLAUDE.md,                                              ↑
 files, and tools.

 Decisions locked with the user:                                        ↑
 - Surface: typed chat first-class, plus hybrid voice (hold-to-talk → local whisper STT → CLI →
 reply shown as captions; no spoken TTS reply — the app has no local TTS↑engine).
 - Permissions: full agent — the CLI may edit files and run commands in the workspace
 (gated behind an explicit one-time consent, mirroring the app's mic-streaming consent pattern,
 per the project's consent-first / no-hidden-actions rules).            ↑
 - Workspace UI: a folder picker in Settings and a quick-switch dropdown on the meeting screen.

 Why this is feasible / the key seam                                    ↑

 The meeting chat UI already talks to the assistant only through the interface                                                              ↑
 IRealtimeVoiceConversation (src/LocalTranscriber.Voice/RealtimeVoiceOptions.cs:81). The           ↑
 ViewModel holds _voice of that type, sends turns via SendUserTextAsync, and renders streamed                                                   ↑
 replies from the AssistantTextAvailable + ResponseCompleted events. So a Claude-CLI backend                                                     ↑
 that implements the same interface drops into the existing chat/voice plumbing with almost no
 UI rewrite. Provider selection revives the agent.provider selector pattern that existed before
 the voice pivot (removed in commit 7ec4ea5; recoverable at 7ec4ea5^ as
 src/LocalTranscriber.Agent/AgentProviderFactory.cs for reference).     ↑

 Hard-rules check: transcription stays fully local (untouched); the agent stays opt-in and off by                                                ↑
 default (Provider defaults to "openai"; claude-cli requires enabling + a workspace + consent);                                                  ↑
 cloud use by the agent is already permitted when opted in; edit/command capability is gated behind                                             ↑
 explicit consent — no hidden actions.
                                                                        ↑
 ---
 Approach

 Add a ClaudeCliConversation : IRealtimeVoiceConversation in LocalTranscriber.Voice that
 spawns claude per user turn, streams its output back through the existing events, and reuses the                                                 ↑
 existing local-whisper STT for hybrid voice. Select it via a new agent.provider config value                                            ↑
 through a small dispatching factory. Surface provider + workspace in the UI.                                                                    ↑

 1. Config model — src/LocalTranscriber.Shared/AgentConfig.cs
                                                                        ↑
 Add to AgentConfig:
 public string Provider { get; set; } = "openai";        // "openai" | "claude-cli"                                                           ↑
 public ClaudeCliAgentConfig ClaudeCli { get; set; } = new();
 New class in the same file (mirrors RealtimeAgentConfig's style; POCO, serialized by ConfigService):                                          ↑
 public sealed class ClaudeCliAgentConfig
 {                                                                      ↑
     public bool Enabled { get; set; }
     public string ExecutablePath { get; set; } = "claude";   // resolve↑ on PATH
     public string WorkspaceFolder { get; set; } = "";        // process↑working directory (required)
     public string Model { get; set; } = "";                  // "" = CLI default; else alias e.g. "opus"
     public bool AllowEditsAndCommands { get; set; }          // stored consent for full-agent mode                                            ↑
     public int TimeoutSeconds { get; set; } = 300;
     public List<string> RecentWorkspaces { get; set; } = new(); // for the meeting quick-switch dropdown                                      ↑
 }
 Provider defaulting to "openai" preserves current behavior for existing↑configs.

 2. New backend — src/LocalTranscriber.Voice/ClaudeCliConversation.cs (net-new)

 Implements IRealtimeVoiceConversation. There is no existing redirected-subprocess code in the
 repo; model the async stdout read loop on TranscriptEventTailer.TailAsync
 (src/LocalTranscriber.Agent/TranscriptEventTailer.cs). Reuse the same STT dependency the voice
 session uses: ILocalTranscriptionService (default WhisperCppTranscriptionService) +
 MicrophonePushToTalkRecorder.

 Behavior:
 - SendUserTextAsync(text) and the hybrid STT result both funnel into one RunTurnAsync(text).                                                    ↑
 - RunTurnAsync spawns the CLI one-shot per turn via System.Diagnostics.Process /
 ProcessStartInfo with:                                                 ↑
   - WorkingDirectory = WorkspaceFolder (this is what "starts in the workspace root"),
 UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput/Error = true.
   - Args: -p --output-format stream-json --verbose <message>
       - first turn: --session-id <generated-uuid>; subsequent turns: --resume <same-uuid>
 (keeps one continuous conversation for the session).
     - --model <Model> when set.                                        ↑
     - Permission: when AllowEditsAndCommands (consent granted) →
 --permission-mode bypassPermissions (required so edits/bash run headless without a prompt                                                       ↑
 that can't be answered in -p mode). Otherwise read-only → --tools "Read,Grep,Glob"
 (answers only, cannot modify).                                         ↑
   - Parse each stdout JSON line (stream-json): emit assistant text blocks as AssistantTextAvailable                                              ↑
 deltas; the terminal {"type":"result",...} (and process exit) → ResponseCompleted.                                                     ↑
 Capture session_id from the init/system line on the first turn.
   - StateChanged: Ready → Thinking (turn running) → Ready. ErrorOccurred on non-zero exit,
 stderr, timeout (TimeoutSeconds), or missing executable. Kill the process on Stop/Dispose;                                                       ↑
 guard one turn at a time.
   - Verify during implementation that stream-json output requires --verbose in this CLI                                                  ↑
 version, and confirm the exact JSON line shapes with a quick
 claude -p --output-format stream-json --verbose "hi" capture.          ↑
 - PushToTalkDown/Up (hybrid only): copy the hybrid branch of
 RealtimeVoiceSession.OnPushToTalkUpAsync (RealtimeVoiceSession.cs:314-364) — record → local                     ↑
 whisper (_transcriber.TranscribeAsync) → raise UserTextCommitted with the recognized text →
 RunTurnAsync(text). No audio leaves the machine. pushToTalk/continuous modes are not
 supported (they stream mic audio to a provider that doesn't exist here).
 - Testability seam: inject a Func<IClaudeProcess> (or a stdout-line-reader delegate) the way
 RealtimeVoiceSession injects transportFactory/transcriber, so unit tests parse a captured
 transcript fixture without spawning claude.

 3. Provider selection — new src/LocalTranscriber.Voice/AgentConversationFactory.cs
                                                                        ↑
 A thin dispatcher returning the existing RealtimeVoiceFactory.Resolution(IRealtimeVoiceConversation?, string? Notice) shape:
 Create(config, secrets, transcriptPath, tools, toolHandler):
   switch config.Agent.Provider:
     "claude-cli":
         validate ClaudeCli.Enabled, WorkspaceFolder set & exists, executable resolvable
           → new ClaudeCliConversation(options)  (else Resolution(null, notice))
         note: gated on ClaudeCl(ClaudeCliConversation implements it).                                 ↑
 - In StartVoiceAsync (line 383): call AgentConversationFactory.Create(...) instead of
 RealtimeVoiceFactory.Create (line 393); gate the OpenAI-only line
 config.Agent.Realtime.Enabled = true (line 390) to the openai provider.
 - Add bound properties (each persists via the existing PersistConfig helper, lines 657-669):
 Providers (["openai","claude-cli"]) + SelectedProvider → c.Agent.Provider;                                                      ↑
 WorkspaceFolder → c.Agent.ClaudeCli.WorkspaceFolder; RecentWorkspaces (ObservableCollection,
 pushed on connect) → c.Agent.ClaudeCli.RecentWorkspaces.               ↑
 - Relax command gating (lines 72-74): typed chat must work for claude-cli even with voice mode off,
 e.g. AgentEnabled && (SelectedProvider == "claude-cli" || SelectedVoiceMode != "off") for
 SendTextCommand and StartVoiceCommand.
 - Full-agent consent: before first start of a claude-cli session with edit/command capability,
 raise the existing ConsentRequested flow (handled in MainWindow.xaml.cs:131 via                                             ↑
 VoiceModeConsentDialog) with a message that the CLI can modify files & run commands in the
 chosen workspace; on accept set ClaudeCli.AllowEditsAndCommands = true and persist. (Reuse the
 dialog; new copy text.)                                                ↑

 5. UI                                                                  ↑

 - Settings — src/LocalTranscriber.App/Views/SettingsView.xaml, "Assistant & privacy" section
 (lines 191-237):
   - Provider ComboBox mirroring the Voice-mode combo (lines 199-202) → AgentPanel.Providers /
 SelectedProvider.
   - Workspace folder picker mirroring the Context-folder block (lines 215-225): TextBox bound to
 AgentPanel.WorkspaceFolder + a Browse… button. Add a BrowseWorkspaceFolder_Click handler
 in SettingsView.xaml.cs copying BrowseTranscriptFolder_Click (line 15, uses
 Microsoft.Win32.OpenFolderDialog).
 - Meeting quick-switch — src/LocalTranscriber.App/Views/MeetingView.xaml, ASSISTANT header near
 Start/Stop (lines 526-530): an editable ComboBox ItemsSource="{Binding AgentPanel.RecentWorkspaces}",
 Text/SelectedItem → AgentPanel.WorkspaceFolder, visible when provider is claude-cli.

 6. CLI parity (optional, keeps "all front-ends share logic")
so agent talk can                                                      ↑
 drive claude-cli too. Mark optional — the WPF path is the primary deliverable.
                                                                        ↑
 ---
 Files to touch
                                                                        ↑
 - src/LocalTranscriber.Shared/AgentConfig.cs — add Provider, ClaudeCliAgentConfig.
 - src/LocalTranscriber.Voice/ClaudeCliConversation.cs — new backend.   ↑
 - src/LocalTranscriber.Voice/AgentConversationFactory.cs — new dispatcher.
 - src/LocalTranscriber.App/ViewModels/AgentPanelViewModel.cs — factory call, provider/workspace
 properties, gating, consent.
 - src/LocalTranscriber.App/Views/SettingsView.xaml (+ .xaml.cs) — provider combo, workspace picker.
 - src/LocalTranscriber.App/Views/MeetingView.xaml — workspace quick-switch dropdown.                                                 ↑
 - src/LocalTranscriber.Cli/AgentCommands.cs — (optional) use the dispatcher.                                                            ↑
 - Tests: tests/LocalTranscriber.Agent.Tests/ (or Voice tests) — new ClaudeCliConversationTests.                                            ↑

 Not touched: transcription pipeline, LocalTranscriber.Context (the markdown context-pack feature
 is orthogonal and stays as-is), MCP server.
                                                                        ↑
 ---
 Verification
                                                                        ↑
 1. Build & unit tests: dotnet build, dotnet test. Add ClaudeCliConversationTests that feed a                                 ↑
 captured stream-json transcript through the injectable process seam and assert
 AssistantTextAvailable deltas + a single terminal ResponseCompleted, pl↑s session-id reuse
 across two turns (--session-id then --resume). No real claude spawn in tests.
 2. CLI shape sanity (manual, during impl): run
 claude -p --output-format stream-json --verbose "hello" in a workspace and confirm the JSON line
 shapes / --verbose requirement the parser relies on.
 3. Manual app test (./scripts/run-app.ps1 or F5, watch with ./scripts/tail-logs.ps1):
   - Settings → Assistant & privacy: set Provider = claude-cli, Browse… a repo as the workspace.
   - Use the meeting quick-switch dropdown to point at a second workspac↑ and confirm the next reply
 reflects the new directory.
 4. Regression: with Provider = openai (default), confirm the existing OpenAI realtime voice/chat
th is unchanged.

 Open items / notes for implementation

 - Meeting-transcript grounding is not wired for claude-cli — context comes from the workspace
 (files + CLAUDE.md), per the user's intent. Passing the live transcript to the CLI (e.g. via
 --append-system-prompt or a temp file) is a possible later enhancement, out of scope here.
 - Latency: one-shot-per-turn spawns a process each turn (~1-2s cold start). Acceptable for KISS;
 a persistent --input-format stream-json process is a later optimization if needed.
 - Windows exec resolution: claude resolves to claude.exe on PATH; allow
╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌