using LocalTranscriber.Agent.OpenAI;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Agent;

/// <summary>
/// Resolves the configured provider. OpenAI providers require explicit enablement
/// AND a resolvable key; anything else falls back with a human-readable reason.
/// </summary>
public static class AgentProviderFactory
{
    public sealed record Resolution(IMeetingAgentProvider Provider, string? Notice);

    public static Resolution Create(AppConfig config, SecretsService? secrets = null)
    {
        secrets ??= new SecretsService();
        string requested = config.Agent.Provider.ToLowerInvariant();

        switch (requested)
        {
            case "openai":
            {
                if (!config.Agent.OpenAI.Enabled)
                {
                    return Fallback("OpenAI provider is not enabled (agent.openAI.enabled=false).");
                }

                var (key, reason) = secrets.ResolveOpenAIKey(config.Agent.OpenAI.ApiKeyEnvironmentVariable);
                if (key is null)
                {
                    return Fallback($"OpenAI provider disabled: {reason}");
                }

                return new Resolution(new OpenAITextMeetingAgentProvider(new OpenAITextAgentOptions
                {
                    ApiKey = key,
                    Model = config.Agent.OpenAI.Model,
                    Temperature = config.Agent.OpenAI.Temperature,
                    MaxOutputTokens = config.Agent.OpenAI.MaxOutputTokens
                }), null);
            }

            case "realtime":
            {
                if (!config.Agent.Realtime.Enabled)
                {
                    return Fallback("Realtime provider is not enabled (agent.realtime.enabled=false).");
                }

                if (config.Agent.Realtime.SendAudio)
                {
                    return Fallback("agent.realtime.sendAudio=true is not supported: raw audio is never sent to providers.");
                }

                var (key, reason) = secrets.ResolveOpenAIKey(config.Agent.Realtime.ApiKeyEnvironmentVariable);
                if (key is null)
                {
                    return Fallback($"Realtime provider disabled: {reason}");
                }

                return new Resolution(new OpenAIRealtimeMeetingAgentProvider(new RealtimeConnectionOptions
                {
                    ApiKey = key,
                    Model = config.Agent.Realtime.Model
                }), null);
            }

            case "fake":
                return new Resolution(new FakeMeetingAgentProvider(), null);

            default:
                return Fallback($"Unknown provider '{config.Agent.Provider}'.");
        }

        static Resolution Fallback(string notice)
            => new(new FakeMeetingAgentProvider(), notice + " Using the offline fake provider.");
    }
}
