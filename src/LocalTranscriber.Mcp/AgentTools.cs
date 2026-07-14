using System.ComponentModel;
using System.Text.Json;
using LocalTranscriber.Context;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;
using ModelContextProtocol.Server;

namespace LocalTranscriber.Mcp;

/// <summary>
/// MCP tools for the meeting agent. Reads local context files only — no arbitrary
/// filesystem access, no shell execution. The real-time voice conversation is an
/// interactive, machine-local feature and is not exposed over MCP.
/// </summary>
[McpServerToolType]
public sealed class AgentTools
{
    private readonly ConfigService _configService;
    private readonly ToolCallLogger _logger;

    public AgentTools(ConfigService configService, ToolCallLogger logger)
    {
        _configService = configService;
        _logger = logger;
    }

    private AppConfig Config => _configService.Load();

    private ContextPackOptions ContextOptions()
    {
        var agent = Config.Agent;
        return new ContextPackOptions
        {
            ContextFolder = agent.ContextFolder,
            MaxTotalCharacters = agent.MaxContextCharacters,
            RequiredFiles = agent.RequiredContextFiles
        };
    }

    [McpServerTool(Name = "agent_get_status"), Description("Get meeting-agent configuration (enabled state, realtime voice mode, context folder).")]
    public string GetStatus()
    {
        _logger.Log("agent_get_status");
        var agent = Config.Agent;
        return JsonSerializer.Serialize(new
        {
            enabled = agent.Enabled,
            voiceMode = agent.Realtime.VoiceMode,
            realtimeEnabled = agent.Realtime.Enabled,
            contextFolder = agent.ContextFolder,
            agentOutputFolder = agent.AgentOutputFolder
        });
    }

    [McpServerTool(Name = "context_list_documents"), Description("List context pack documents available to the agent.")]
    public async Task<string> ContextListDocuments()
    {
        _logger.Log("context_list_documents");
        var docs = await new MarkdownContextPackService().ListDocumentsAsync(ContextOptions());
        return docs.Count == 0 ? "No context documents found." : string.Join("\n", docs);
    }

    [McpServerTool(Name = "context_read_document"), Description("Read one context document by name. Only .md files inside the configured context folder are allowed.")]
    public async Task<string> ContextReadDocument([Description("Document file name, e.g. codename-summary.md")] string name)
    {
        _logger.Log("context_read_document", name);
        var doc = await new MarkdownContextPackService().ReadDocumentAsync(ContextOptions(), name);
        return doc is null
            ? "Access denied or not found (only .md files inside the context folder can be read)."
            : doc.Content;
    }

    [McpServerTool(Name = "context_validate"), Description("Validate the context pack: required files present, folder readable, budget sane.")]
    public async Task<string> ContextValidate()
    {
        _logger.Log("context_validate");
        var problems = await new MarkdownContextPackService().ValidateAsync(ContextOptions());
        return problems.Count == 0 ? "Context pack OK." : string.Join("\n", problems.Select(p => $"- {p}"));
    }
}
