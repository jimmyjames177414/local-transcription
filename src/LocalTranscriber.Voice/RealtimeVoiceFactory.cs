using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Voice;

/// <summary>
/// Resolves a voice conversation from config. Parallel to the removed AgentProviderFactory:
/// returns a session or a human-readable notice explaining why voice is unavailable.
/// </summary>
public static class RealtimeVoiceFactory
{
    public sealed record Resolution(IRealtimeVoiceConversation? Session, string? Notice);

    public static RealtimeVoiceMode ParseMode(string? value)
        => Enum.TryParse<RealtimeVoiceMode>(value, ignoreCase: true, out var mode) ? mode : RealtimeVoiceMode.Off;

    public static Resolution Create(AppConfig config, SecretsService? secrets = null, string? transcriptJsonlPath = null)
    {
        secrets ??= new SecretsService();
        var rt = config.Agent.Realtime;
        var mode = ParseMode(rt.VoiceMode);

        if (mode == RealtimeVoiceMode.Off)
        {
            return new Resolution(null, "Voice conversation is off (agent.realtime.voiceMode=off).");
        }

        if (!rt.Enabled)
        {
            return new Resolution(null, "Realtime is not enabled (agent.realtime.enabled=false).");
        }

        if (mode is RealtimeVoiceMode.PushToTalk or RealtimeVoiceMode.Continuous && !rt.SendAudio)
        {
            return new Resolution(null,
                $"{mode} mode streams your microphone to the provider; set agent.realtime.sendAudio=true to consent (hybrid needs no audio consent).");
        }

        var (key, reason) = secrets.ResolveOpenAIKey(rt.ApiKeyEnvironmentVariable);
        if (key is null)
        {
            return new Resolution(null, $"Voice disabled: {reason}");
        }

        var agent = config.Agent;
        var options = new RealtimeVoiceOptions
        {
            ApiKey = key,
            Model = rt.Model,
            Mode = mode,
            Voice = rt.Voice,
            VadThreshold = rt.VadThreshold,
            VadSilenceMs = rt.VadSilenceMs,
            VadPrefixPaddingMs = rt.VadPrefixPaddingMs,
            InputAudioDeviceId = rt.InputAudioDeviceId,
            OutputAudioDeviceId = rt.OutputAudioDeviceId,
            SpeakReplies = rt.SpeakReplies,
            GroundingIntervalSeconds = rt.GroundingIntervalSeconds,
            TranscriptJsonlPath = transcriptJsonlPath,
            ContextFolder = agent.ContextFolder,
            MaxContextCharacters = agent.MaxContextCharacters,
            RequiredContextFiles = agent.RequiredContextFiles,
            RollingWindowMinutes = agent.RollingWindowMinutes,
            MaxTranscriptEventsPerPrompt = agent.MaxTranscriptEventsPerPrompt,
            AgentOutputFolder = agent.AgentOutputFolder,
            WhisperModelPath = config.WhisperModelPath
        };

        return new Resolution(new RealtimeVoiceSession(options), null);
    }
}
