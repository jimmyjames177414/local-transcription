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
    public int TimeoutSeconds { get; init; } = 300;
    public int MaxTranscriptEvents { get; init; } = 10;
    public RealtimeVoiceMode Mode { get; init; } = RealtimeVoiceMode.Hybrid;

    /// <summary>Live transcript .jsonl to snapshot into each turn for meeting grounding (optional).</summary>
    public string? TranscriptJsonlPath { get; init; }

    public string? InputAudioDeviceId { get; init; }
    public string WhisperModelPath { get; init; } = "models/whisper/ggml-base.en.bin";
    public string AgentOutputFolder { get; init; } = System.IO.Path.Combine("output", "agent");
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

        _ = RunGuardedTurnAsync(text);
        return Task.CompletedTask;
    }

    // === Hybrid hold-to-talk: local whisper STT → text turn. Mirrors RealtimeVoiceSession's hybrid branch. ===
    public void PushToTalkDown() => _ = RunGuardedAsync(OnPushToTalkDownAsync);
    public void PushToTalkUp() => _ = RunGuardedAsync(OnPushToTalkUpAsync);

    private async Task OnPushToTalkDownAsync()
    {
        if (_options.Mode != RealtimeVoiceMode.Hybrid)
        {
            return; // only hybrid is supported; streaming modes have no provider to stream to
        }

        var ct = _sessionCts?.Token ?? CancellationToken.None;
        _recorder = _recorderFactory();
        await _recorder.StartAsync(ct).ConfigureAwait(false);
        SetState(RealtimeVoiceState.Capturing);
    }

    private async Task OnPushToTalkUpAsync()
    {
        if (_options.Mode != RealtimeVoiceMode.Hybrid || _recorder is null)
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

    private async Task RunGuardedTurnAsync(string text)
    {
        try
        {
            await RunTurnAsync(text).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("claude-cli", $"Turn failed: {ex.Message}");
            ErrorOccurred?.Invoke(this, $"Assistant turn failed: {ex.Message}");
            SetState(RealtimeVoiceState.Ready);
        }
    }

    private async Task RunTurnAsync(string userText)
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

        if (!Directory.Exists(_options.WorkspaceFolder))
        {
            _turnLock.Release();
            ErrorOccurred?.Invoke(this, $"Workspace folder not found: {_options.WorkspaceFolder}");
            SetState(RealtimeVoiceState.Ready);
            return;
        }

        _turnCancelledByUser = false;
        var sessionToken = _sessionCts?.Token ?? CancellationToken.None;
        var turnCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        turnCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        _turnCts = turnCts;

        IClaudeProcess? process = null;
        try
        {
            SetState(RealtimeVoiceState.Thinking);
            string prompt = await BuildPromptAsync(userText, turnCts.Token).ConfigureAwait(false);
            var request = new ClaudeProcessRequest(_options.ExecutablePath, _options.WorkspaceFolder, BuildArgs(prompt));
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
            _firstTurn = false;

            if (exitCode != 0 || errored)
            {
                string detail = Truncate(process.StandardError);
                ErrorOccurred?.Invoke(this, detail.Length > 0
                    ? $"Claude CLI failed (exit {exitCode}): {detail}"
                    : $"Claude CLI failed (exit {exitCode}).");
            }
            else
            {
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
                    _sessionId ??= id;
                    break;

                case "assistant"
                    when root.TryGetProperty("message", out var msg)
                        && msg.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.Array:
                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                            && block.TryGetProperty("text", out var txt)
                            && txt.GetString() is { Length: > 0 } text)
                        {
                            deltas.Add(text);
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
