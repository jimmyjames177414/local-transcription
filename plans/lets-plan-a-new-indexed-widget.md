# Generate Notes button (AI meeting-notes document)

## Context

The Meeting screen already has a live **Notes** panel (the 4-section
`notes-{sessionId}.md` the agent maintains during a call) and a deterministic
**minutes export**. Neither produces a rich, standalone *meeting-notes document*.
The user wants a **Generate Notes** button that, on demand, feeds the current
transcript plus the loaded project context into the detailed prompt at
`prompts/generate-notes-prompt.md` and produces a full notes document (H1 title,
metadata, decisions/Q&A/risks/action-items, Mermaid diagrams, etc.).

Confirmed decisions with the user:
- **Brain:** reuse the *currently configured* agent backend (OpenAI realtime /
  Claude CLI / hybrid) driven as a bounded one-shot — no new API client. The
  generated document must **record the configuration used** (provider / model /
  voice mode / timestamp).
- **Output:** show the result in a **preview window first**, save from there
  (does not touch the live Notes panel or minutes files).
- **Scope:** one button usable **both live and in review**. Transcript is taken
  from the currently-displayed rows (`MainWindowViewModel.Transcript`), which is
  populated in both modes — one code path covers both.

Key constraint discovered during exploration: the app has **no one-shot text
path**. All AI text flows through the streaming `IRealtimeVoiceConversation`
abstraction, and each backend bakes in a "reply concisely / only when addressed"
system prompt that cannot be overridden through the interface. We therefore drive
the backend as a one-shot and put the necessary override *inside the prompt text*.

---

## Approach

### 1. New service — `MeetingNotesGenerator` (`src/LocalTranscriber.Voice/MeetingNotesGenerator.cs`)

One-shot document generation over the configured backend. Mirrors the
create/subscribe/dispose lifecycle already used in
`AgentPanelViewModel.StartVoiceAsync` (`AgentPanelViewModel.cs:608-628`).

```csharp
public sealed class MeetingNotesGenerator
{
    // Optional factory seam so tests can inject a fake IRealtimeVoiceConversation.
    public MeetingNotesGenerator(
        Func<AppConfig, SecretsService?, RealtimeVoiceFactory.Resolution>? sessionFactory = null);

    public async Task<string> GenerateAsync(
        string fullPrompt, AppConfig config, SecretsService? secrets = null,
        int timeoutSeconds = 240, CancellationToken ct = default);
}
```

Implementation notes (all grounded in the backend code):

- **Clone the config, never mutate the caller's.** `AppConfig` is the mutable
  POCO shared with `ConfigService`. Deep-clone (JSON round-trip) and on the clone
  force: `Agent.Realtime.Enabled = true` (factory hard-requires it,
  `RealtimeVoiceFactory.cs:29`), `Agent.Realtime.VoiceMode = "off"` (→
  `TextOnlyOutput`: no mic, no playback, text modality), `Agent.Realtime.SpeakReplies
  = false`, and `Agent.RequiredContextFiles = []` (OpenAI otherwise re-injects
  context into its system prompt — we already put context in `fullPrompt`; see
  pitfall 2). This also sidesteps the factory's `pushToTalk/continuous` audio-consent
  rejection.
- **Build the session with all grounding OFF:**
  `AgentConversationFactory.Create(genConfig, secrets, transcriptJsonlPath: null,
  tools: null, toolHandler: null, notesFilePath: null)`. `null` transcript path
  disables the OpenAI tail/grounding loops (`RealtimeVoiceSession.cs:102-106`) and
  the CLI transcript prepend (`ClaudeCliConversation.cs:526-531`); `null` tools
  prevents a second `response.create`/`ResponseCompleted`; `null` notesFilePath
  drops the CLI notes directive. Throw if `resolution.Session is null` (use
  `resolution.Notice`).
- **Accumulate + await:** subscribe `AssistantTextAvailable` (append deltas to a
  `StringBuilder`), `ResponseCompleted` (→ `TaskCompletionSource.TrySetResult`), and
  **`ErrorOccurred`** (→ record message + `TrySetResult(false)`). `ErrorOccurred` is
  **mandatory**: the CLI backend does *not* raise `ResponseCompleted` on error/timeout
  (`ClaudeCliConversation.cs:271-277,296-298`), so without it a failure hangs until
  our timeout. Use `TaskCreationOptions.RunContinuationsAsynchronously`.
- **Order:** `await StartAsync(ct)` **first** (SendUserTextAsync throws if the
  transport/session isn't up — `RealtimeVoiceSession.cs:254-257`), and keep the
  connect *outside* the timeout window (OpenAI connect can retry up to
  `MaxReconnectAttempts × 15s`). Then `await SendUserTextAsync(fullPrompt, ct)`, then
  start a linked CTS with `CancelAfter(timeoutSeconds)` whose callback calls
  `session.CancelTurn()` and `TrySetCanceled` (the passed token has limited reach
  mid-turn on OpenAI — cancellation must go through `CancelTurn`/dispose).
- **finally:** unsubscribe all three handlers and `await session.DisposeAsync()`
  (dispose calls `StopAsync` internally — no separate stop needed).
- **Guard empty output:** if the trimmed result is empty, throw a clear error
  rather than returning a blank document (a filtered/empty response still fires
  `ResponseCompleted`).

### 2. The prompt asset + assembly

- **Embed** `prompts/generate-notes-prompt.md` as an `EmbeddedResource` in
  `LocalTranscriber.App` (add an `<EmbeddedResource Include="..\..\prompts\generate-notes-prompt.md" Link="Prompts\generate-notes-prompt.md" />` item, or copy the file under the project). Read it via
  `Assembly.GetManifestResourceStream(...)`. This is robust for F5/installed runs;
  editing the prompt means a rebuild (acceptable; a config-path override can be added
  later if wanted).
- **`fullPrompt` layout** (assembled in the App command):
  1. A short **override preamble** — e.g. *"Follow the instructions below exactly to
     produce a complete meeting-notes document. Disregard any earlier instruction to be
     brief or conversational; output only the finished Markdown notes."* (neutralises
     the baked "be concise" prompt — pitfall 1).
  2. The embedded prompt body.
  3. A `SOURCE MATERIAL` section: `## Meeting transcript` (rows rendered as
     `[{Time}] {SpeakerName}: {Text}`, reusing `TranscriptRowViewModel.Time/SpeakerName/Text`)
     then `## Project context` (the context-pack `CombinedText`).
- **Config footer** (the user's addendum): after generation, append
  `\n\n---\n\n<sub>Generated by LocalTranscriber · {provider} · model {model} · voice
  mode {voiceMode} · {yyyy-MM-dd HH:mm}</sub>` using the *actual* config used. Built in
  the command (it knows the config).

### 3. Context loading (reuse existing)

In the command, load the context pack the same way the backend does: construct
`MarkdownContextPackService` (in `LocalTranscriber.Context`) and call
`LoadAsync(new ContextPackOptions { ContextFolder = <resolved config.Agent.ContextFolder>,
MaxTotalCharacters = config.Agent.MaxContextCharacters, RequiredFiles = config.Agent.RequiredContextFiles }, ct)`,
then use `ContextPack.CombinedText`. Context is available to App transitively via
Voice; add a direct `ProjectReference` to `LocalTranscriber.Context` in the App
csproj if the compiler doesn't resolve it. When no context folder/files exist,
`CombinedText` is empty — fine.

### 4. Command + busy state — `MainWindowViewModel` (`src/LocalTranscriber.App/ViewModels/MainWindowViewModel.cs`)

Follow the hand-rolled MVVM pattern (`Mvvm/RelayCommand.cs`; `AsyncRelayCommand`
auto-disables while running).

- `public AsyncRelayCommand GenerateNotesCommand { get; }`, constructed in the ctor
  with can-execute `() => Transcript.Count > 0` (call
  `GenerateNotesCommand.RaiseCanExecuteChanged()` where other transcript-dependent
  commands are refreshed, e.g. alongside `CopyTranscriptCommand`).
- `private async Task GenerateNotesAsync()`:
  1. `var config = new ConfigService().Load();`
  2. render transcript from `Transcript`; load context (§3); read embedded prompt;
     assemble `fullPrompt` (§2).
  3. `var md = await new MeetingNotesGenerator().GenerateAsync(fullPrompt, config, new SecretsService());`
     (offload the sync factory work with `Task.Run` like
     `AgentPanelViewModel.cs:608` if needed).
  4. append the config footer.
  5. `ShowGenerateNotesPreview?.Invoke(md, suggestedFileName)` where
     `suggestedFileName = $"generated-notes-{(ReviewSessionId ?? SessionId ?? DateTime.Now:yyyyMMdd)}.md"`.
  6. `try/catch` → set `ErrorText`/banner and `AppLog.Warn` on failure (mirror
     `StartAsync` error handling).
- Add a `public bool IsGeneratingNotes` bound flag (pulsed around the call, marshalled
  via the existing `PostToUi`) so the button can show a "Generating…" label/spinner
  via a `Button.Style` `DataTrigger` (same idiom as the Start button at
  `MeetingView.xaml:427-436`).
- Add `public Action<string, string>? ShowGenerateNotesPreview { get; set; }`
  (markdown, suggestedFileName) — the dialog callback seam, mirroring
  `ShowSpeakerRenameDialog` (`MainWindowViewModel.cs:81`).

### 5. Preview window — `src/LocalTranscriber.App/Views/Dialogs/GenerateNotesPreviewWindow.xaml(.cs)`

Model on the existing dialogs (`Views/Dialogs/SpeakerRenameDialog.xaml`). Contents:
- An `mdxaml:MarkdownScrollViewer` (already used at `MeetingView.xaml:981`) bound to the
  markdown for a rendered preview, plus a toggle to a read-only raw-markdown `TextBox`.
  Note: MdXaml renders Mermaid fenced blocks as plain code — they render as diagrams
  only once the saved `.md` is opened in a Mermaid-aware viewer; call this out in the
  window (a small hint label) so it isn't mistaken for a bug.
- Buttons: **Save** (a `Microsoft.Win32.SaveFileDialog` defaulting to the minutes
  folder `MinutesExportConfig.Folder`/`~/meetings` with the suggested filename; on OK
  `File.WriteAllText` then open via `Process.Start(new ProcessStartInfo(path){
  UseShellExecute = true })`, the same idiom as `NotesPanelViewModel.OpenNotesFile`),
  **Copy** (clipboard), **Close**.

### 6. Wire the callback + the button

- **`MainWindow.xaml.cs`** (next to `Session.ShowSpeakerRenameDialog`, `:58`):
  ```csharp
  Session.ShowGenerateNotesPreview = (markdown, suggestedName) =>
      new Views.Dialogs.GenerateNotesPreviewWindow(markdown, suggestedName) { Owner = this }.ShowDialog();
  ```
- **`MeetingView.xaml`** — add a `Button` to the Notes-column header `StackPanel`
  (`~:955-974`, next to `✏ Edit`/`notes.md ↗`), `Style="{StaticResource
  SubtleButtonStyle}"`, `Command="{Binding Session.GenerateNotesCommand}"`,
  `ToolTip="Generate a full meeting-notes document from the transcript and context"`,
  with a `DataTrigger` on `Session.IsGeneratingNotes` swapping the label to
  "Generating…". `AsyncRelayCommand` auto-disables it while running and can-execute
  greys it out when the transcript is empty.

## Files to touch

- **New:** `src/LocalTranscriber.Voice/MeetingNotesGenerator.cs`
- **New:** `src/LocalTranscriber.App/Views/Dialogs/GenerateNotesPreviewWindow.xaml` (+ `.xaml.cs`)
- `src/LocalTranscriber.App/LocalTranscriber.App.csproj` (embed the prompt; add Context ref if needed)
- `src/LocalTranscriber.App/ViewModels/MainWindowViewModel.cs` (command, busy flag, callback, transcript/context/prompt assembly)
- `src/LocalTranscriber.App/MainWindow.xaml.cs` (wire `ShowGenerateNotesPreview`)
- `src/LocalTranscriber.App/Views/MeetingView.xaml` (the button)
- (source of truth) `prompts/generate-notes-prompt.md` — unchanged, embedded as-is

## Verification

- **Build:** `dotnet build`.
- **Unit test (Voice.Tests):** using the `sessionFactory` seam, inject a fake
  `IRealtimeVoiceConversation` that, on `SendUserTextAsync`, raises two
  `AssistantTextAvailable` deltas then `ResponseCompleted`; assert `GenerateAsync`
  returns the concatenated text. Add a second test: fake raises `ErrorOccurred`
  (no `ResponseCompleted`) → `GenerateAsync` throws with the message (guards the CLI
  error path). `dotnet test`.
- **Manual (`./scripts/run-app.ps1`), OpenAI backend (current default):**
  1. Open a finished session in review (Sessions → open). Transcript populates →
     **Generate Notes** enables.
  2. Click it → button shows "Generating…" and disables → preview window opens with a
     rendered document (H1 title + sections; Mermaid shown as code with the hint).
  3. Confirm the config footer names provider `openai` / model `gpt-realtime-2.1-mini`
     / voice mode / timestamp.
  4. **Save** → SaveFileDialog defaults to `~/meetings` → file written and opened; the
     live Notes panel and any minutes files are untouched. **Copy** puts markdown on the
     clipboard.
  5. Start a live recording, let a few transcript lines land, click **Generate Notes** →
     it uses the live rows (same path) and produces a document.
  6. Error path: temporarily clear the OpenAI key → clicking surfaces a friendly
     error (banner/`ErrorText`), no crash, button re-enables.
- **Optional (Claude CLI backend):** set `agent.provider=claude-cli` with a workspace,
  repeat step 2 — confirm the same one-shot flow works and the footer reflects the CLI
  provider/model.
