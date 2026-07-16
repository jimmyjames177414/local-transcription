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
        // The shared ConfigService is registered by the composing host (App) so a single instance is
        // reused everywhere; don't register a second one here.
        services.AddSingleton(config);
        services.AddSingleton(sp => new SqliteDatabase(config.DatabasePath));
        services.AddSingleton<IKnownSpeakerStore>(sp =>
            new SqliteKnownSpeakerStore(sp.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<ITranscriptionEngine>(sp =>
            EngineFactory.CreateReal(config, sp.GetRequiredService<SqliteDatabase>()));
        return services;
    }
}
