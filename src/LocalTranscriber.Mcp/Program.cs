using LocalTranscriber.Mcp;
using LocalTranscriber.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol; log only to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<ToolCallLogger>();
builder.Services.AddSingleton<TranscriberService>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<LocalTranscriberTools>()
    .WithTools<AgentTools>();

await builder.Build().RunAsync();
