using System.Text;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Voice;

/// <summary>
/// The "full hybrid" backend: Claude CLI is the brain (reasoning + notes, with its workspace context),
/// and an <see cref="IReplySpeaker"/> (OpenAI realtime) is the mouth that voices Claude's replies.
/// Ears are the brain's own local-whisper hold-to-talk, so microphone audio never leaves the machine;
/// only Claude's finished reply text is sent to the speaker. Implements <see cref="IRealtimeVoiceConversation"/>
/// so it drops into the same chat/voice UI.
/// </summary>
public sealed class HybridBrainConversation : IRealtimeVoiceConversation
{
    private readonly IRealtimeVoiceConversation _brain;
    private readonly IReplySpeaker? _speaker;
    private readonly StringBuilder _reply = new();

    private CancellationTokenSource? _cts;
    private bool _speakerReady;

    public HybridBrainConversation(IRealtimeVoiceConversation brain, IReplySpeaker? speaker)
    {
        _brain = brain;
        _speaker = speaker;

        _brain.AssistantTextAvailable += OnBrainText;
        _brain.ResponseCompleted += OnBrainCompleted;
        _brain.StateChanged += (_, s) => StateChanged?.Invoke(this, s);
        _brain.ErrorOccurred += (_, e) => ErrorOccurred?.Invoke(this, e);
        _brain.UserTextCommitted += (_, t) => UserTextCommitted?.Invoke(this, t);
        _brain.UserSpeechTranscribed += (_, t) => UserSpeechTranscribed?.Invoke(this, t);

        if (_speaker is not null)
        {
            _speaker.ErrorOccurred += (_, e) => ErrorOccurred?.Invoke(this, e);
        }
    }

    public RealtimeVoiceState State => _brain.State;

    public event EventHandler<string>? AssistantTextAvailable;
    public event EventHandler<RealtimeVoiceState>? StateChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<string>? UserTextCommitted;
    public event EventHandler<string>? UserSpeechTranscribed;
    public event EventHandler? ResponseCompleted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await _brain.StartAsync(_cts.Token).ConfigureAwait(false);

        if (_speaker is not null)
        {
            try
            {
                await _speaker.StartAsync(_cts.Token).ConfigureAwait(false);
                _speakerReady = true;
            }
            catch (Exception ex)
            {
                // Degrade to captions-only rather than failing the whole assistant.
                _speakerReady = false;
                AppLog.Warn("hybrid", $"Reply voice unavailable, captions only: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"Reply voice unavailable — showing captions only ({ex.Message}).");
            }
        }
    }

    private void OnBrainText(object? sender, string text)
    {
        _reply.Append(text);
        AssistantTextAvailable?.Invoke(this, text);
    }

    private void OnBrainCompleted(object? sender, EventArgs e)
    {
        ResponseCompleted?.Invoke(this, EventArgs.Empty);

        string text = _reply.ToString().Trim();
        _reply.Clear();
        if (_speakerReady && _speaker is not null && text.Length > 0)
        {
            _ = SpeakSafeAsync(text);
        }
    }

    private async Task SpeakSafeAsync(string text)
    {
        try
        {
            await _speaker!.SpeakAsync(text, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("hybrid", $"Speak failed: {ex.Message}");
        }
    }

    public Task SendUserTextAsync(string text, CancellationToken cancellationToken = default)
    {
        _speaker?.StopSpeaking(); // a new turn interrupts the current reply
        return _brain.SendUserTextAsync(text, cancellationToken);
    }

    public void PushToTalkDown()
    {
        _speaker?.StopSpeaking();
        _brain.PushToTalkDown();
    }

    public void PushToTalkUp() => _brain.PushToTalkUp();

    public void CancelTurn()
    {
        _speaker?.StopSpeaking();
        _brain.CancelTurn();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        await _brain.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_speaker is not null)
        {
            await _speaker.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        await _brain.DisposeAsync().ConfigureAwait(false);
        if (_speaker is not null)
        {
            await _speaker.DisposeAsync().ConfigureAwait(false);
        }
        _cts?.Dispose();
    }
}
