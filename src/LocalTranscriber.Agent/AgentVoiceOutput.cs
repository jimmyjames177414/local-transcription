using LocalTranscriber.Shared;

namespace LocalTranscriber.Agent;

/// <summary>
/// Speaks suggestions PRIVATELY to the user through the local default audio output
/// (e.g. headphones). Never routed into the meeting.
/// </summary>
public interface IAgentVoiceOutput : IDisposable
{
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);
}

public sealed class NoOpAgentVoiceOutput : IAgentVoiceOutput
{
    public Task SpeakAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Dispose()
    {
    }
}

/// <summary>
/// Windows TTS via System.Speech. Output goes to the system default audio device —
/// set headphones as default to keep the voice private. Windows-only at runtime.
/// </summary>
public sealed class WindowsTtsAgentVoiceOutput : IAgentVoiceOutput
{
    private System.Speech.Synthesis.SpeechSynthesizer? _synthesizer;
    private readonly object _lock = new();

    public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            try
            {
                lock (_lock)
                {
                    _synthesizer ??= CreateSynthesizer();
                    _synthesizer.Speak(text);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("agent", $"Voice output failed: {ex.Message}");
            }
        }, cancellationToken);
    }

    private static System.Speech.Synthesis.SpeechSynthesizer CreateSynthesizer()
    {
        var synthesizer = new System.Speech.Synthesis.SpeechSynthesizer();
        synthesizer.SetOutputToDefaultAudioDevice();
        synthesizer.Rate = 1;
        return synthesizer;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _synthesizer?.Dispose();
            _synthesizer = null;
        }
    }
}
