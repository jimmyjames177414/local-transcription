using LocalTranscriber.Shared;
using LocalTranscriber.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LocalTranscriber.Engine;

/// <summary>
/// One shared registration for the transcription core so a DI-hosted front-end composes the engine
/// the same way instead of hand-wiring it. The WPF app uses this today; the MCP server and CLI can
/// adopt it as they move onto the generic host. Reuses <see cref="EngineFactory.CreateReal"/>
/// as the single place that knows how to build the real engine — this only exposes it to a DI
/// container. The engine is registered as a singleton; the container owns its (async) disposal, so
/// hosts that resolve it must be disposed asynchronously.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTranscriptionCore(this IServiceCollection services, AppConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton<ConfigService>();
        services.AddSingleton<ITranscriptionEngine>(_ => EngineFactory.CreateReal(config));
        return services;
    }
}
