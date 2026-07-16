using System.Text;
using System.Text.Json;
using LocalTranscriber.Agent;
using LocalTranscriber.AI;
using LocalTranscriber.Audio;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Voice;

/// <summary>All settings for one Claude Code CLI conversation. Built by <see cref="AgentConversationFactory"/>.</summary>
public sealed record ClaudeCliConversationOptions
{
    public required string ExecutablePath { get; init; }
    public required string WorkspaceFolder { get; init; }
    public string Model { get; init; } = "";
    public bool AllowEditsAndCommands { get; init; }

    /// <summary>Run claude inside WSL: launch wsl.exe, cd into the Linux <see cref="WorkspaceFolder"/>,
    /// exec <see cref="ExecutablePath"/> there. Windows path args (notes) are translated to /mnt/…</summary>
    public bool UseWsl { get; init; }

    /// <summary>WSL distro for <c>wsl.exe -d</c>; empty uses the default distro.</summary>
    public string WslDistro { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 300;
    public int MaxTranscriptEvents { get; init; } = 10;
    public RealtimeVoiceMode Mode { get; init; } = RealtimeVoiceMode.Hybrid;

    /// <summary>Live transcript .jsonl to snapshot into each turn for meeting grounding (optional).</summary>
    public string? TranscriptJsonlPath { get; init; }

    public string? InputAudioDeviceId { get; init; }
    public string WhisperModelPath { get; init; } = "models/whisper/ggml-base.en.bin";
    public string AgentOutputFolder { get; init; } = System.IO.Path.Combine("output", "agent");

    /// <summary>
    /// Absolute path to the session's notes markdown file. When set AND edits are allowed, Claude is
    /// told to maintain it with its own file tools (the app's file-watcher live-reloads the panel).
    /// </summary>
    public string? NotesFilePath { get; init; }
}

/// <summary>
/// An assistant conversation backed by the external Claude Code CLI, launched one-shot per turn with
/// its working directory set to a user-chosen workspace so Claude reads that project's CLAUDE.md,
/// files, memory, and MCP tools. Implements <see cref="IRealtimeVoiceConversation"/> so it drops into
/// the existing chat/voice UI. Typed turns and hybrid hold-to-talk (local whisper STT → text) both
/// funnel into one turn runner; no audio leaves the machine. pushToTalk/continuous are unsupported
/// (they stream mic audio to a provider that does not exist here).
/// </summary>
public sealed class ClaudeCliConversation : IRealtimeVoiceConversation
{
    private readonly ClaudeCliConversationOptions _options;
    private readonly Func<ClaudeProcessRequest, IClaudeProcess> _processFactory;
    private readonly ILocalTranscriptionService _transcriber;
    private readonly Func<IPushToTalkRecorder> _recorderFactory;

    // Dedupe by content-derived id so transcript lines already fed to a previous turn are not re-sent.
    private readonly TranscriptEventDeduplicator _dedup = new();
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _turnCts;
    private volatile bool _turnCancelledByUser;
    private IPushToTalkRecorder? _recorder;
    private string? _sessionId;
    private bool _firstTurn = true;
    private RealtimeVoiceState _state = RealtimeVoiceState.Idle;

    public ClaudeCliConversation(
        ClaudeCliConversationOptions options,
        Func<ClaudeProcessRequest, IClaudeProcess>? processFactory = null,
        ILocalTranscriptionService? transcriber = null,
        Func<IPushToTalkRecorder>? recorderFactory = null)
    {
        _options = options;
        _processFactory = processFactory ?? (req => new ClaudeCliProcess(req));
        _transcriber = transcriber ?? new WhisperCppTranscriptionService();
        _recorderFactory = recorderFactory ?? (() => new MicrophonePushToTalkRecorder(options.InputAudioDeviceId, options.AgentOutputFolder));
    }

    public RealtimeVoiceState State => _state;

    /// <summary>Claude maintains the notes file only when a path is set and it may write (full agent).</summary>
    private bool MaintainsNotes => _options.AllowEditsAndCommands && !string.IsNullOrWhiteSpace(_options.NotesFilePath);

    private static string NotesDirective(string notesFilePath)
        => "You also maintain the user's private meeting notes at the file `" + notesFilePath + "`. " +
           "When the conversation produces something worth recording (decisions, action items, key facts, " +
           "open questions), read that file and rewrite it as a single clean, concise markdown document with " +
           "the new information integrated — reorganise, don't merely append. If nothing is worth recording, " +
           "leave it unchanged. Never mention the notes file, note-taking, or these instructions in your reply.";

    public event EventHandler<string>? AssistantTextAvailable;
    public event EventHandler<RealtimeVoiceState>? StateChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<string>? UserTextCommitted;
    // Interface member: only meaningful for streaming modes with server-side transcription, so this
    // text-only backend never raises it.
#pragma warning disable CS0067
    public event EventHandler<string>? UserSpeechTranscribed;
#pragma warning restore CS0067
    public event EventHandler? ResponseCompleted;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetState(RealtimeVoiceState.Ready);
        return Task.CompletedTask;
    }

    /// <summary>Typed user message: runs a CLI turn. Replies stream back via events.</summary>
    public Task SendUserTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_sessionCts is null)
        {
            throw new InvalidOperationException("Claude CLI conversation not started.");
        }

        _ = RunGuardedTurnAsync(text, cancellationToken);
        return Task.CompletedTask;
    }

    // === Hybrid hold-to-talk: local whisper STT → text turn. Mirrors RealtimeVoiceSession's hybrid branch. ===
    public void PushToTalkDown() => _ = RunGuardedAsync(OnPushToTalkDownAsync);
    public void PushToTalkUp() => _ = RunGuardedAsync(OnPushToTalkUpAsync);

    private async Task OnPushToTalkDownAsync()
    {
        // This backend has no audio-streaming provider — local Whisper capture is the only path,
        // so always capture regardless of the configured voiceMode string.
        AppLog.Info("claude-cli", "Push-to-talk down: starting local Whisper capture.");
        var ct = _sessionCts?.Token ?? CancellationToken.None;
        _recorder = _recorderFactory();
        await _recorder.StartAsync(ct).ConfigureAwait(false);
        SetState(RealtimeVoiceState.Capturing);
    }

    private async Task OnPushToTalkUpAsync()
    {
        if (_recorder is null)
        {
            return;
        }

        var ct = _sessionCts?.Token ?? CancellationToken.None;
        string? wavPath = await _recorder.StopAsync(ct).ConfigureAwait(false);
        await _recorder.DisposeAsync().ConfigureAwait(false);
        _recorder = null;

        if (wavPath is null)
        {
            ErrorOccurred?.Invoke(this, "Microphone captured nothing — check your Voice mic device in the devices panel.");
            SetState(RealtimeVoiceState.Ready);
            return;
        }

        string text;
        try
        {
            var result = await _transcriber.TranscribeAsync(new TranscriptionRequest
            {
                AudioPath = wavPath,
                ModelPath = _options.WhisperModelPath,
                Language = "en"
            }, ct).ConfigureAwait(false);
            text = result.Text.Trim();
        }
        catch (Exception ex)
        {
            AppLog.Warn("claude-cli", $"Local STT failed: {ex.Message}");
            ErrorOccurred?.Invoke(this, $"Speech recognition failed: {ex.Message}");
            SetState(RealtimeVoiceState.Ready);
            return;
        }
        finally
        {
            TryDelete(wavPath);
        }

        if (text.Length == 0)
        {
            ErrorOccurred?.Invoke(this, "No speech detected — hold longer or speak more clearly.");
            SetState(RealtimeVoiceState.Ready);
            return;
        }

        // Show the user's own words (SendUserTextAsync does not raise this — the caller already has the text).
        AppLog.Info("claude-cli", $"Push-to-talk transcribed {text.Length} chars — sending turn.");
        UserTextCommitted?.Invoke(this, text);
        await RunTurnAsync(text).ConfigureAwait(false);
    }

    /// <summary>Kills the in-flight child process and returns to Ready with a "cancelled" notice.</summary>
    public void CancelTurn()
    {
        if (_state != RealtimeVoiceState.Thinking)
        {
            return;
        }
        _turnCancelledByUser = true;
        _turnCts?.Cancel();
    }

    private async Task RunGuardedTurnAsync(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            await RunTurnAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("claude-cli", $"Turn failed: {ex.Message}");
            ErrorOccurred?.Invoke(this, $"Assistant turn failed: {ex.Message}");
            SetState(RealtimeVoiceState.Ready);
        }
    }

    private async Task RunTurnAsync(string userText, CancellationToken cancellationToken = default)
    {
        userText = userText.Trim();
        if (userText.Length == 0)
        {
            return;
        }

        // One turn at a time — the CLI keeps a single conversation per session.
        if (!await _turnLock.WaitAsync(0).ConfigureAwait(false))
        {
            ErrorOccurred?.Invoke(this, "The assistant is still working on the previous turn.");
            return;
        }

        // In WSL mode the workspace is a Linux path validated at construction; Directory.Exists
        // (a Windows check) would always fail on it, so only guard the native-Windows case here.
        if (!_options.UseWsl && !Directory.Exists(_options.WorkspaceFolder))
        {
            _turnLock.Release();
            ErrorOccurred?.Invoke(this, $"Workspace folder not found: {_options.WorkspaceFolder}");
            SetState(RealtimeVoiceState.Ready);
            return;
        }

        _turnCancelledByUser = false;
        var sessionToken = _sessionCts?.Token ?? CancellationToken.None;
        // Link the caller's token too so a send cancelled by the caller also tears the turn down.
        var turnCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, cancellationToken);
        turnCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        _turnCts = turnCts;

        IClaudeProcess? process = null;
        try
        {
            SetState(RealtimeVoiceState.Thinking);
            string prompt = await BuildPromptAsync(userText, turnCts.Token).ConfigureAwait(false);
            var request = _options.UseWsl
                ? BuildWslRequest(BuildArgs(prompt))
                : new ClaudeProcessRequest(_options.ExecutablePath, _options.WorkspaceFolder, BuildArgs(prompt));
            process = _processFactory(request);

            bool errored = false;
            await foreach (var line in process.ReadLinesAsync(turnCts.Token).ConfigureAwait(false))
            {
                foreach (var delta in ParseLine(line, ref errored))
                {
                    AssistantTextAvailable?.Invoke(this, delta);
                }
            }

            int exitCode = await process.WaitForExitAsync(turnCts.Token).ConfigureAwait(false);

            if (exitCode != 0 || errored)
            {
                string detail = Truncate(process.StandardError);
                ErrorOccurred?.Invoke(this, detail.Length > 0
                    ? $"Claude CLI failed (exit {exitCode}): {detail}"
                    : $"Claude CLI failed (exit {exitCode}).");
            }
            else
            {
                // Only advance past the first turn on success — a failed first turn never opened a
                // session, so the next turn must still use --session-id rather than --resume.
                _firstTurn = false;
                ResponseCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (turnCts.IsCancellationRequested && !sessionToken.IsCancellationRequested)
        {
            // Turn-level cancellation: either the user hit Cancel or the per-turn timeout fired.
            process?.Kill();
            if (_turnCancelledByUser)
            {
                AssistantTextAvailable?.Invoke(this, "\n_(turn cancelled)_");
                ResponseCompleted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                ErrorOccurred?.Invoke(this, $"Assistant turn timed out after {_options.TimeoutSeconds}s.");
            }
        }
        catch (OperationCanceledException)
        {
            // Session teardown (Stop/Dispose): shut the child down quietly, no error surfaced.
            process?.Kill();
        }
        catch (Exception ex)
        {
            process?.Kill();
            AppLog.Warn("claude-cli", $"Turn error: {ex.Message}");
            ErrorOccurred?.Invoke(this, $"Assistant turn failed: {ex.Message}");
        }
        finally
        {
            _turnCts = null;
            turnCts.Dispose();
            if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
            // Don't flip back to Ready while the session is being torn down (StopAsync sets Stopped).
            if (_sessionCts is { IsCancellationRequested: false })
            {
                SetState(RealtimeVoiceState.Ready);
            }
            _turnLock.Release();
        }
    }

    /// <summary>
    /// Assembles the per-turn CLI arguments. First turn opens a session id; later turns resume it.
    /// Ordering matters: <c>--allowedTools</c> is variadic and would swallow the positional prompt, so
    /// the permission/tools flags go first and the single-value <c>--session-id</c>/<c>--resume</c> is
    /// kept immediately before the prompt (the proven-working invocation shape).
    /// </summary>
    private IReadOnlyList<string> BuildArgs(string prompt)
    {
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };

        if (_options.AllowEditsAndCommands)
        {
            // Full agent: required so edits/bash run headless (no permission prompt is answerable in -p mode).
            args.Add("--permission-mode");
            args.Add("bypassPermissions");
        }
        else
        {
            // Read-only: answer questions but never modify the workspace.
            args.Add("--allowedTools");
            args.Add("Read,Grep,Glob");
        }

        // Note maintenance: let Claude reach the notes file (usually outside the workspace) and tell it
        // to keep the notes rebuilt. Requires write capability, so it is gated on full-agent mode.
        if (MaintainsNotes)
        {
            // The notes file lives on the Windows side; in WSL mode Claude reaches it via /mnt/…,
            // so translate both the file path (embedded in the directive) and its --add-dir folder.
            string notesFile = _options.NotesFilePath!;
            string notesDir = Path.GetDirectoryName(notesFile) ?? "";
            if (_options.UseWsl)
            {
                notesFile = ToWslPath(notesFile);
                notesDir = string.IsNullOrEmpty(notesDir) ? "" : ToWslPath(notesDir);
            }

            if (!string.IsNullOrEmpty(notesDir))
            {
                args.Add("--add-dir");
                args.Add(notesDir);
            }
            args.Add("--append-system-prompt");
            args.Add(NotesDirective(notesFile));
        }

        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            args.Add("--model");
            args.Add(_options.Model);
        }

        // Keep the session flag (single-value) last so the variadic --allowedTools cannot eat the prompt.
        if (_firstTurn)
        {
            _sessionId = Guid.NewGuid().ToString();
            args.Add("--session-id");
            args.Add(_sessionId);
        }
        else
        {
            args.Add("--resume");
            args.Add(_sessionId!);
        }

        args.Add(prompt); // positional prompt last
        return args;
    }

    /// <summary>
    /// Wraps the claude invocation to run inside WSL:
    ///   <c>wsl.exe [-d distro] --cd &lt;workspace&gt; -- bash -ic "exec &lt;claude&gt; 'arg' 'arg' …"</c>
    /// <c>--cd</c> sets the Linux working directory (reliable, unlike passing it through the shell);
    /// the interactive shell loads the nvm-managed node/claude onto PATH; and every claude argument is
    /// POSIX single-quoted into one command string so the free-text prompt cannot break parsing.
    /// wsl.exe itself gets a valid Windows working directory (the Linux cwd comes from --cd).
    /// </summary>
    private ClaudeProcessRequest BuildWslRequest(IReadOnlyList<string> claudeArgs)
    {
        var script = new StringBuilder("exec ").Append(ShellQuote(_options.ExecutablePath));
        foreach (var arg in claudeArgs)
        {
            script.Append(' ').Append(ShellQuote(arg));
        }

        var wslArgs = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.WslDistro))
        {
            wslArgs.Add("-d");
            wslArgs.Add(_options.WslDistro);
        }
        wslArgs.Add("--cd");
        wslArgs.Add(_options.WorkspaceFolder);
        wslArgs.Add("--");
        wslArgs.Add("bash");
        wslArgs.Add("-ic");
        wslArgs.Add(script.ToString());

        return new ClaudeProcessRequest("wsl.exe", AppContext.BaseDirectory, wslArgs);
    }

    /// <summary>POSIX single-quote a string so a shell treats it as one literal argument.</summary>
    internal static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Translates an absolute Windows path to its WSL <c>/mnt/&lt;drive&gt;/…</c> equivalent.</summary>
    internal static string ToWslPath(string windowsPath)
    {
        string full = Path.GetFullPath(windowsPath);
        if (full.Length >= 2 && full[1] == ':')
        {
            char drive = char.ToLowerInvariant(full[0]);
            return "/mnt/" + drive + full.Substring(2).Replace('\\', '/');
        }
        return full.Replace('\\', '/');
    }

    /// <summary>
    /// Parses one stream-json stdout line. Emits assistant text blocks as caption deltas, captures the
    /// session id from the init line, and flags a terminal error result. Malformed lines are skipped.
    /// </summary>
    private List<string> ParseLine(string line, ref bool errored)
    {
        var deltas = new List<string>();
        line = line.Trim();
        if (line.Length == 0)
        {
            return deltas;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return deltas; // not a JSON line (or partial): skip, don't kill the turn
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeEl))
            {
                return deltas;
            }

            switch (typeEl.GetString())
            {
                case "system"
                    when root.TryGetProperty("subtype", out var st) && st.GetString() == "init"
                        && root.TryGetProperty("session_id", out var sid) && sid.GetString() is { Length: > 0 } id:
                    // The CLI's reported id wins: BuildArgs pre-sets a GUID for the first turn, so a
                    // ??= here would never adopt the real session id and --resume would break.
                    _sessionId = id;
                    break;

                case "assistant"
                    when root.TryGetProperty("message", out var msg)
                        && msg.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.Array:
                    foreach (var block in content.EnumerateArray())
                    {
                        if (!block.TryGetProperty("type", out var bt))
                        {
                            continue;
                        }
                        switch (bt.GetString())
                        {
                            case "text"
                                when block.TryGetProperty("text", out var txt)
                                    && txt.GetString() is { Length: > 0 } text:
                                deltas.Add(text);
                                break;
                            // Surface tool activity so the user sees Claude is working (not stalled).
                            case "tool_use"
                                when block.TryGetProperty("name", out var tn)
                                    && tn.GetString() is { Length: > 0 } toolName:
                                deltas.Add(ToolUseHint(toolName));
                                break;
                        }
                    }
                    break;

                case "result"
                    when root.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True:
                    errored = true;
                    break;
            }
        }

        return deltas;
    }

    /// <summary>
    /// Prepends a one-shot snapshot of recent, not-yet-seen transcript lines so the CLI works as a
    /// live-meeting copilot. Only new events (per the shared deduplicator) are included.
    /// </summary>
    private async Task<string> BuildPromptAsync(string userText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.TranscriptJsonlPath) || !File.Exists(_options.TranscriptJsonlPath))
        {
            return userText;
        }

        var newEvents = await ReadNewTranscriptEventsAsync(_options.TranscriptJsonlPath!, ct).ConfigureAwait(false);
        if (newEvents.Count == 0)
        {
            return userText;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[Recent transcript]");
        foreach (var e in newEvents)
        {
            sb.AppendLine($"{e.Speaker.DisplayName}: {e.Text}");
        }
        sb.AppendLine("[End transcript]");
        sb.AppendLine();
        sb.Append("User: ").Append(userText);
        return sb.ToString();
    }

    private async Task<List<TranscriptEvent>> ReadNewTranscriptEventsAsync(string path, CancellationToken ct)
    {
        var result = new List<TranscriptEvent>();
        try
        {
            string content;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            var tail = content
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .TakeLast(Math.Max(1, _options.MaxTranscriptEvents));

            foreach (var line in tail)
            {
                var e = TranscriptEventJsonParser.TryParse(line);
                if (e is not null && _dedup.TryAdd(e))
                {
                    result.Add(e);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("claude-cli", $"Could not read transcript for grounding: {ex.Message}");
        }

        return result;
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn("claude-cli", $"Voice turn failed: {ex.Message}");
            ErrorOccurred?.Invoke(this, $"Voice turn failed: {ex.Message}");
            SetState(RealtimeVoiceState.Ready);
        }
    }

    /// <summary>A short italic "working…" caption for a tool_use block, so the user sees Claude is busy.</summary>
    private static string ToolUseHint(string toolName) => toolName switch
    {
        "Read" => "_[Reading file…]_\n",
        "Grep" => "_[Searching code…]_\n",
        "Glob" => "_[Finding files…]_\n",
        "Bash" => "_[Running command…]_\n",
        "Edit" => "_[Editing file…]_\n",
        "Write" => "_[Writing file…]_\n",
        "WebFetch" => "_[Fetching page…]_\n",
        "WebSearch" => "_[Searching the web…]_\n",
        _ => $"_[{toolName}…]_\n"
    };

    private static string Truncate(string text)
    {
        text = text.Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private void SetState(RealtimeVoiceState state)
    {
        if (_state == state)
        {
            return;
        }
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Reset the session identity so a restarted conversation opens a fresh CLI session rather than
        // trying to --resume a dead one. Done before the guard so it runs even on a redundant stop.
        _firstTurn = true;
        _sessionId = null;

        if (_state is RealtimeVoiceState.Stopped)
        {
            return;
        }

        _sessionCts?.Cancel();
        _turnCts?.Cancel();

        if (_recorder is not null)
        {
            try { await _recorder.DisposeAsync().ConfigureAwait(false); } catch { }
            _recorder = null;
        }

        SetState(RealtimeVoiceState.Stopped);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _sessionCts?.Dispose();
        _turnLock.Dispose();
    }
}
