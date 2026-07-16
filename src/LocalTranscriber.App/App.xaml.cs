using System.Windows;
using LocalTranscriber.App.Services;
using LocalTranscriber.App.ViewModels;
using LocalTranscriber.Engine;
using LocalTranscriber.Engine.Ipc;
using LocalTranscriber.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LocalTranscriber.App;

/// <summary>
/// Interaction logic for App.xaml. Builds the generic host that composes the shared engine, the
/// control pipe, and the window's view-models via DI — so the WPF front-end wires the engine the
/// same way the MCP server does, instead of hand-constructing it deep inside a view-model.
///
/// The screens are registered with factory lambdas that mirror the exact wiring the window used to
/// do inline (the inter-view-model links, e.g. AgentPanel reading the live transcript path from the
/// session, resolve the singleton view-models lazily). Bindings and XAML are unchanged: the window
/// still exposes the same view-model properties and sets DataContext = this.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var configService = new ConfigService();
        var config = configService.Load();

        var builder = Host.CreateApplicationBuilder();
        // Register the bootstrap ConfigService instance so every view-model shares one, rather than
        // each constructing its own (AddTranscriptionCore no longer registers one).
        builder.Services.AddSingleton(configService);
        builder.Services.AddTranscriptionCore(config);

        // The control pipe is owned by the host and drives the same engine singleton the UI uses.
        builder.Services.AddSingleton(sp => new EngineIpcServer(sp.GetRequiredService<ITranscriptionEngine>()));

        builder.Services.AddSingleton(sp => new MainWindowViewModel(
            sp.GetRequiredService<ITranscriptionEngine>(), sp.GetRequiredService<ConfigService>()));
        builder.Services.AddSingleton(sp => new SettingsViewModel(sp.GetRequiredService<ConfigService>()));
        builder.Services.AddSingleton(sp => new SpeakerManagementViewModel(
            store: sp.GetRequiredService<IKnownSpeakerStore>(),
            configService: sp.GetRequiredService<ConfigService>()));
        builder.Services.AddSingleton(sp => new AgentPanelViewModel(
            configService: sp.GetRequiredService<ConfigService>(),
            currentTranscriptPath: () => sp.GetRequiredService<MainWindowViewModel>().GroundingJsonlPath));
        builder.Services.AddSingleton(sp => new SessionsViewModel(
            configService: sp.GetRequiredService<ConfigService>(),
            isRecording: () => sp.GetRequiredService<MainWindowViewModel>().IsRecording));
        builder.Services.AddSingleton(sp => new NotesService(
            () => sp.GetRequiredService<AgentPanelViewModel>().OutputFolder));
        builder.Services.AddSingleton(sp => new NotesPanelViewModel(sp.GetRequiredService<NotesService>()));
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        // Start the host so any IHostedService gets its StartAsync (none registered today, so this
        // is a near no-op, but it makes the host contract correct). OnStartup is synchronous, so
        // block on it rather than awaiting.
        _host.StartAsync().GetAwaiter().GetResult();
        _host.Services.GetRequiredService<EngineIpcServer>().Start();

        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stop before dispose so hosted services get a graceful StopAsync. Async disposal: the engine
        // and the IPC server are IAsyncDisposable, so a synchronous container dispose would throw.
        // Blocking here is fine at teardown (engine work runs on ConfigureAwait(false), so it does
        // not need the UI thread).
        _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (_host is IAsyncDisposable asyncHost)
        {
            asyncHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        else
        {
            _host?.Dispose();
        }

        base.OnExit(e);
    }
}
