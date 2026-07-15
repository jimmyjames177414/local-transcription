using LocalTranscriber.Shared;
using LocalTranscriber.Voice;

namespace LocalTranscriber.Voice.Tests;

public class HybridBrainConversationTests
{
    /// <summary>A brain stand-in that lets tests drive the IRealtimeVoiceConversation events.</summary>
    private sealed class FakeBrain : IRealtimeVoiceConversation
    {
        public RealtimeVoiceState State { get; private set; } = RealtimeVoiceState.Idle;
        public int StartCalls;
        public int CancelCalls;
        public List<string> Sent { get; } = new();

        public event EventHandler<string>? AssistantTextAvailable;
        public event EventHandler<RealtimeVoiceState>? StateChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<string>? UserTextCommitted;
        public event EventHandler<string>? UserSpeechTranscribed;
        public event EventHandler? ResponseCompleted;

        public Task StartAsync(CancellationToken ct = default) { StartCalls++; State = RealtimeVoiceState.Ready; return Task.CompletedTask; }
        public Task SendUserTextAsync(string text, CancellationToken ct = default) { Sent.Add(text); return Task.CompletedTask; }
        public void PushToTalkDown() { }
        public void PushToTalkUp() { }
        public void CancelTurn() => CancelCalls++;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void EmitText(string t) => AssistantTextAvailable?.Invoke(this, t);
        public void EmitCompleted() => ResponseCompleted?.Invoke(this, EventArgs.Empty);
        public void EmitError(string e) => ErrorOccurred?.Invoke(this, e);

        // Unused-event suppression.
        public void TouchEvents() { StateChanged?.Invoke(this, State); UserTextCommitted?.Invoke(this, ""); UserSpeechTranscribed?.Invoke(this, ""); }
    }

    private sealed class FakeSpeaker : IReplySpeaker
    {
        public List<string> Spoken { get; } = new();
        public int StopCalls;
        public int StartCalls;
        public bool FailStart;

        public event EventHandler<string>? ErrorOccurred;

        public Task StartAsync(CancellationToken ct = default)
        {
            StartCalls++;
            if (FailStart) throw new InvalidOperationException("no connect");
            return Task.CompletedTask;
        }

        public Task SpeakAsync(string text, CancellationToken ct = default) { Spoken.Add(text); return Task.CompletedTask; }
        public void StopSpeaking() => StopCalls++;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaiseError(string e) => ErrorOccurred?.Invoke(this, e);
    }

    [Fact]
    public async Task SpeaksBufferedReply_OnBrainCompletion()
    {
        var brain = new FakeBrain();
        var speaker = new FakeSpeaker();
        await using var hybrid = new HybridBrainConversation(brain, speaker);

        var deltas = new List<string>();
        int completions = 0;
        hybrid.AssistantTextAvailable += (_, t) => deltas.Add(t);
        hybrid.ResponseCompleted += (_, _) => completions++;

        await hybrid.StartAsync();
        brain.EmitText("Hello ");
        brain.EmitText("there");
        brain.EmitCompleted();

        Assert.Equal(new[] { "Hello ", "there" }, deltas);   // captions forwarded
        Assert.Equal(1, completions);
        Assert.Equal(new[] { "Hello there" }, speaker.Spoken); // full reply spoken once
    }

    [Fact]
    public async Task EachTurnSpeaksOnlyItsOwnReply()
    {
        var brain = new FakeBrain();
        var speaker = new FakeSpeaker();
        await using var hybrid = new HybridBrainConversation(brain, speaker);
        await hybrid.StartAsync();

        brain.EmitText("first");
        brain.EmitCompleted();
        brain.EmitText("second");
        brain.EmitCompleted();

        Assert.Equal(new[] { "first", "second" }, speaker.Spoken);
    }

    [Fact]
    public async Task NewTurnAndCancel_InterruptSpeech()
    {
        var brain = new FakeBrain();
        var speaker = new FakeSpeaker();
        await using var hybrid = new HybridBrainConversation(brain, speaker);
        await hybrid.StartAsync();

        await hybrid.SendUserTextAsync("hi");
        hybrid.PushToTalkDown();
        hybrid.CancelTurn();

        Assert.Equal(3, speaker.StopCalls);          // barge-in on send, PTT-down, and cancel
        Assert.Equal(new[] { "hi" }, brain.Sent);
        Assert.Equal(1, brain.CancelCalls);
    }

    [Fact]
    public async Task SpeakerStartFailure_DegradesToCaptionsOnly()
    {
        var brain = new FakeBrain();
        var speaker = new FakeSpeaker { FailStart = true };
        await using var hybrid = new HybridBrainConversation(brain, speaker);

        var errors = new List<string>();
        hybrid.ErrorOccurred += (_, e) => errors.Add(e);

        await hybrid.StartAsync();
        brain.EmitText("hello");
        brain.EmitCompleted();

        Assert.Single(errors);                 // surfaced the degrade notice
        Assert.Empty(speaker.Spoken);          // nothing spoken since the mouth failed to start
    }

    [Fact]
    public async Task NoSpeaker_WorksAsCaptionsOnly()
    {
        var brain = new FakeBrain();
        await using var hybrid = new HybridBrainConversation(brain, speaker: null);
        int completions = 0;
        hybrid.ResponseCompleted += (_, _) => completions++;

        await hybrid.StartAsync();
        brain.EmitText("x");
        brain.EmitCompleted();

        Assert.Equal(1, completions);
    }
}
