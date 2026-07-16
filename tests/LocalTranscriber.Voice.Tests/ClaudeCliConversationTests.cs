using System.Runtime.CompilerServices;
using LocalTranscriber.Shared;
using LocalTranscriber.Voice;

namespace LocalTranscriber.Voice.Tests;

public class ClaudeCliConversationTests
{
    /// <summary>A captured stream-json process: yields the given stdout lines, then exits.</summary>
    private sealed class FakeClaudeProcess : IClaudeProcess
    {
        private readonly IReadOnlyList<string> _lines;
        private readonly int _exitCode;
        public FakeClaudeProcess(IReadOnlyList<string> lines, int exitCode)
        {
            _lines = lines;
            _exitCode = exitCode;
        }

        public string StandardError { get; init; } = "";
        public bool Killed { get; private set; }

        public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var line in _lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return line;
                await Task.Yield();
            }
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(_exitCode);
        public void Kill() => Killed = true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string[] SuccessTurn(string sessionId, params string[] textBlocks)
    {
        var lines = new List<string>
        {
            $"{{\"type\":\"rate_limit_event\",\"session_id\":\"{sessionId}\"}}",
            $"{{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"{sessionId}\",\"cwd\":\"C:\\\\ws\",\"tools\":[\"Read\"]}}"
        };
        foreach (var t in textBlocks)
        {
            lines.Add($"{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"{t}\"}}]}}}}");
        }
        lines.Add($"{{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"{string.Concat(textBlocks)}\",\"session_id\":\"{sessionId}\"}}");
        return lines.ToArray();
    }

    private static string TranscriptLine(string sessionId, string speaker, string text, int seconds)
    {
        var ts = DateTimeOffset.Parse("2026-07-14T10:00:00Z").AddSeconds(seconds).ToString("o");
        return $"{{\"sessionId\":\"{sessionId}\",\"timestamp\":\"{ts}\",\"speakerId\":\"sp1\",\"speakerName\":\"{speaker}\",\"source\":\"systemAudio\",\"text\":\"{text}\"}}";
    }

    private sealed class Harness
    {
        public List<string> Deltas { get; } = new();
        public List<string> Errors { get; } = new();
        public int Completions;
        public List<IReadOnlyList<string>> Requests { get; } = new();

        private readonly ClaudeCliConversation _conversation;
        private TaskCompletionSource _turnDone = NewTcs();

        public Harness(ClaudeCliConversationOptions options, Func<ClaudeProcessRequest, IClaudeProcess> processFactory)
        {
            _conversation = new ClaudeCliConversation(options, req =>
            {
                Requests.Add(req.Arguments);
                return processFactory(req);
            });
            _conversation.AssistantTextAvailable += (_, t) => Deltas.Add(t);
            _conversation.ErrorOccurred += (_, e) => { Errors.Add(e); _turnDone.TrySetResult(); };
            _conversation.ResponseCompleted += (_, _) => { Completions++; _turnDone.TrySetResult(); };
        }

        public ClaudeCliConversation Conversation => _conversation;

        private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunTurnAsync(string text)
        {
            _turnDone = NewTcs();
            await _conversation.StartAsync();
            await _conversation.SendUserTextAsync(text);
            await _turnDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public string LastPrompt => Requests[^1][^1]; // prompt is the final positional argument
    }

    private static ClaudeCliConversationOptions Options(string workspace, string? transcriptPath = null)
        => new()
        {
            ExecutablePath = "claude",
            WorkspaceFolder = workspace,
            TranscriptJsonlPath = transcriptPath,
            MaxTranscriptEvents = 10
        };

    [Fact]
    public async Task Turn_EmitsTextDeltas_AndSingleCompletion()
    {
        string ws = Directory.CreateTempSubdirectory().FullName;
        var harness = new Harness(Options(ws), _ => new FakeClaudeProcess(SuccessTurn("sess-1", "Hello ", "world"), 0));

        await harness.RunTurnAsync("hi");

        Assert.Equal(new[] { "Hello ", "world" }, harness.Deltas);
        Assert.Equal(1, harness.Completions);
        Assert.Empty(harness.Errors);
    }

    [Fact]
    public async Task SessionId_IsOpenedThenResumed_AcrossTwoTurns()
    {
        string ws = Directory.CreateTempSubdirectory().FullName;
        var harness = new Harness(Options(ws), _ => new FakeClaudeProcess(SuccessTurn("sess-x", "ok"), 0));

        await harness.RunTurnAsync("first");
        await harness.RunTurnAsync("second");

        var turn1 = harness.Requests[0];
        var turn2 = harness.Requests[1];

        int sidIndex = turn1.ToList().IndexOf("--session-id");
        Assert.True(sidIndex >= 0, "first turn should open a session id");
        Assert.DoesNotContain("--resume", turn1);

        // After the first turn the CLI reported session_id "sess-x" (from the fake's init line).
        // The second turn must resume with that CLI-reported id, not the GUID we proposed in turn 1.
        Assert.DoesNotContain("--session-id", turn2);
        int resumeIndex = turn2.ToList().IndexOf("--resume");
        Assert.True(resumeIndex >= 0, "second turn should resume");
        Assert.Equal("sess-x", turn2[resumeIndex + 1]);
    }

    [Fact]
    public async Task ReadOnly_PassesAllowedToolsNotBypass()
    {
        string ws = Directory.CreateTempSubdirectory().FullName;
        var opts = Options(ws) with { AllowEditsAndCommands = false };
        var harness = new Harness(opts, _ => new FakeClaudeProcess(SuccessTurn("s", "ok"), 0));

        await harness.RunTurnAsync("q");

        var args = harness.Requests[0].ToList();
        Assert.Contains("--allowedTools", args);
        Assert.Equal("Read,Grep,Glob", args[args.IndexOf("--allowedTools") + 1]);
        Assert.DoesNotContain("bypassPermissions", args);
    }

    [Fact]
    public async Task FullAgent_PassesBypassPermissions()
    {
        string ws = Directory.CreateTempSubdirectory().FullName;
        var opts = Options(ws) with { AllowEditsAndCommands = true };
        var harness = new Harness(opts, _ => new FakeClaudeProcess(SuccessTurn("s", "ok"), 0));

        await harness.RunTurnAsync("q");

        var args = harness.Requests[0].ToList();
        Assert.Contains("--permission-mode", args);
        Assert.Equal("bypassPermissions", args[args.IndexOf("--permission-mode") + 1]);
        Assert.DoesNotContain("--allowedTools", args);
    }

    [Fact]
    public async Task NotesMaintenance_WhenFullAgent_AddsDirAndSystemPrompt()
    {
        string ws = Directory.CreateTempSubdirectory().FullName;
        string notes = Path.Combine(Path.GetTempPath(), "lt-notes", "notes-x.md");
        var opts = Options(ws) with { AllowEditsAndCommands = true, NotesFilePath = notes };
        var harness = new Harness(opts, _ => new FakeClaudeProcess(SuccessTurn("s", "ok"), 0));

        await harness.RunTurnAsync("q");

        var args = harness.Requests[0].ToList();
        Assert.Contains("--add-dir", args);
        Assert.Equal(Path.GetDirectoryName(notes), args[args.IndexOf("--add-dir") + 1]);
        Assert.Contains("--append-system-prompt", args);
        Assert.Contains(notes, args[args.IndexOf("--append-system-prompt") + 1]);
    }

    [Fact]
    public async Task NotesMaintenance_Skipped_WhenReadOnly()
    {
        string ws = Directory.CreateTempSubdirectory().FullName;
        string notes = Path.Combine(Path.GetTempPath(), "lt-notes", "notes-y.md");
        var opts = Options(ws) with { AllowEditsAndCommands = false, NotesFilePath = notes };
        var harness = new Harness(opts, _ => new FakeClaudeProcess(SuccessTurn("s", "ok"), 0));

        await harness.RunTurnAsync("q");

        var args = harness.Requests[0].ToList();
        Assert.DoesNotContain("--add-dir", args);
        Assert.DoesNotContain("--append-system-prompt", args);
    }

    [Fact]
    public async Task NonZeroExit_RaisesError_NotCompletion()
    {
        string ws = Directory.CreateTempSubdirectory().FullName;
        var harness = new Harness(Options(ws),
            _ => new FakeClaudeProcess(new[] { "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\"}" }, exitCode: 1)
            { StandardError = "boom" });

        await harness.RunTurnAsync("hi");

        Assert.Equal(0, harness.Completions);
        var error = Assert.Single(harness.Errors);
        Assert.Contains("boom", error);
    }

    [Fact]
    public async Task ErrorResult_RaisesError()
    {
        string ws = Directory.CreateTempSubdirectory().FullName;
        string[] lines =
        {
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\"}",
            "{\"type\":\"result\",\"subtype\":\"error\",\"is_error\":true,\"result\":\"nope\"}"
        };
        var harness = new Harness(Options(ws), _ => new FakeClaudeProcess(lines, 0));

        await harness.RunTurnAsync("hi");

        Assert.Equal(0, harness.Completions);
        Assert.Single(harness.Errors);
    }

    [Fact]
    public async Task TranscriptGrounding_PrependsOnlyNewEvents()
    {
        string ws = Directory.CreateTempSubdirectory().FullName;
        string transcript = Path.Combine(ws, "transcript.jsonl");
        File.WriteAllLines(transcript, new[]
        {
            TranscriptLine("t1", "Alex", "kickoff remarks", 0),
            TranscriptLine("t1", "Sam", "budget question", 5)
        });

        var harness = new Harness(Options(ws, transcript),
            _ => new FakeClaudeProcess(SuccessTurn("s", "ok"), 0));

        // Turn 1: both existing lines are new → grounded.
        await harness.RunTurnAsync("summarise");
        string prompt1 = harness.LastPrompt;
        Assert.Contains("[Recent transcript]", prompt1);
        Assert.Contains("kickoff remarks", prompt1);
        Assert.Contains("budget question", prompt1);
        Assert.EndsWith("User: summarise", prompt1);

        // Turn 2: nothing new → prompt is just the user text (dedup suppressed the seen lines).
        await harness.RunTurnAsync("again");
        Assert.Equal("again", harness.LastPrompt);

        // Turn 3: a fresh line appears → only it is prepended.
        File.AppendAllText(transcript, "\n" + TranscriptLine("t1", "Alex", "new decision made", 10) + "\n");
        await harness.RunTurnAsync("what changed");
        string prompt3 = harness.LastPrompt;
        Assert.Contains("new decision made", prompt3);
        Assert.DoesNotContain("kickoff remarks", prompt3);
        Assert.DoesNotContain("budget question", prompt3);
    }

    [Fact]
    public async Task MissingWorkspace_RaisesErrorNotCrash()
    {
        string missing = Path.Combine(Path.GetTempPath(), "lt-no-such-" + Guid.NewGuid().ToString("N"));
        var harness = new Harness(Options(missing), _ => new FakeClaudeProcess(SuccessTurn("s", "ok"), 0));

        await harness.RunTurnAsync("hi");

        Assert.Equal(0, harness.Completions);
        Assert.Single(harness.Errors);
        Assert.Empty(harness.Requests); // never spawned
    }
}
