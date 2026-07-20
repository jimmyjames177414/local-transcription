using LocalTranscriber.Shared;
using LocalTranscriber.Voice;

namespace LocalTranscriber.Voice.Tests;

public class MeetingNotesGeneratorTests
{
    /// <summary>
    /// Drives the one-shot lifecycle: on SendUserTextAsync it replays a scripted sequence of events
    /// (deltas + terminal). Lets tests exercise both the success and the error/no-completion paths.
    /// </summary>
    private sealed class ScriptedConversation : IRealtimeVoiceConversation
    {
        private readonly string[] _deltas;
        private readonly string? _error;

        public ScriptedConversation(string[] deltas, string? error = null)
        {
            _deltas = deltas;
            _error = error;
        }

        public RealtimeVoiceState State { get; private set; } = RealtimeVoiceState.Idle;

        public event EventHandler<string>? AssistantTextAvailable;
        public event EventHandler<RealtimeVoiceState>? StateChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<string>? UserTextCommitted;
        public event EventHandler<string>? UserSpeechTranscribed;
        public event EventHandler? ResponseCompleted;

        public Task StartAsync(CancellationToken ct = default)
        {
            State = RealtimeVoiceState.Ready;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public Task SendUserTextAsync(string text, CancellationToken ct = default)
        {
            foreach (var delta in _deltas)
            {
                AssistantTextAvailable?.Invoke(this, delta);
            }

            if (_error is not null)
            {
                // The CLI backend raises ErrorOccurred WITHOUT ResponseCompleted on failure.
                ErrorOccurred?.Invoke(this, _error);
            }
            else
            {
                ResponseCompleted?.Invoke(this, EventArgs.Empty);
            }
            return Task.CompletedTask;
        }

        public void PushToTalkDown() { }
        public void PushToTalkUp() { }
        public void CancelTurn() { }
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            UserTextCommitted?.Invoke(this, "");
            UserSpeechTranscribed?.Invoke(this, "");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task GenerateAsync_ConcatenatesDeltas_OnResponseCompleted()
    {
        var fake = new ScriptedConversation(new[] { "# Notes\n", "body text" });
        var generator = new MeetingNotesGenerator((_, _) => new RealtimeVoiceFactory.Resolution(fake, null));

        string result = await generator.GenerateAsync("prompt", new AppConfig());

        Assert.Equal("# Notes\nbody text", result);
    }

    [Fact]
    public async Task GenerateAsync_Throws_WhenErrorOccursWithoutCompletion()
    {
        var fake = new ScriptedConversation(Array.Empty<string>(), error: "Claude CLI failed (exit 1): boom");
        var generator = new MeetingNotesGenerator((_, _) => new RealtimeVoiceFactory.Resolution(fake, null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateAsync("prompt", new AppConfig()));

        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task GenerateAsync_Throws_WhenBackendUnavailable()
    {
        var generator = new MeetingNotesGenerator((_, _) =>
            new RealtimeVoiceFactory.Resolution(null, "Voice disabled: no API key"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateAsync("prompt", new AppConfig()));

        Assert.Contains("no API key", ex.Message);
    }

    [Fact]
    public async Task GenerateAsync_Throws_WhenOutputIsEmpty()
    {
        var fake = new ScriptedConversation(new[] { "   " });
        var generator = new MeetingNotesGenerator((_, _) => new RealtimeVoiceFactory.Resolution(fake, null));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateAsync("prompt", new AppConfig()));
    }
}
