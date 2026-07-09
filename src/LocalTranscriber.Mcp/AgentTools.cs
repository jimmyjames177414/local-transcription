using System.ComponentModel;
using System.Text.Json;
using LocalTranscriber.Agent;
using LocalTranscriber.Context;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;
using ModelContextProtocol.Server;

namespace LocalTranscriber.Mcp;

/// <summary>
/// MCP tools for the meeting agent. Everything reads local stores/files only:
/// suggestions from SQLite/agent output, context from the configured context folder.
/// No arbitrary filesystem access, no shell execution.
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

    private SqliteAgentSuggestionStore Store()
        => new(new SqliteDatabase(Config.DatabasePath));

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

    [McpServerTool(Name = "agent_get_status"), Description("Get meeting-agent configuration and the latest stored activity.")]
    public async Task<string> GetStatus()
    {
        _logger.Log("agent_get_status");
        var agent = Config.Agent;
        var recent = await Store().GetRecentAsync(1);
        string? summary = await Store().GetRunningSummaryAsync();
        return JsonSerializer.Serialize(new
        {
            enabled = agent.Enabled,
            mode = agent.Mode,
            provider = agent.Provider,
            contextFolder = agent.ContextFolder,
            agentOutputFolder = agent.AgentOutputFolder,
            latestSuggestionAt = recent.Count > 0 ? recent[0].CreatedAt.ToString("o") : null,
            hasRunningSummary = !string.IsNullOrWhiteSpace(summary)
        }, AgentJson.Options);
    }

    [McpServerTool(Name = "agent_get_suggestions"), Description("Get recent agent suggestions (newest first).")]
    public async Task<string> GetSuggestions(
        [Description("How many to return (default 20).")] int count = 20,
        [Description("Include dismissed suggestions.")] bool includeDismissed = false)
    {
        _logger.Log("agent_get_suggestions", $"count={count}");
        var suggestions = await Store().GetRecentAsync(Math.Clamp(count, 1, 200), includeDismissed);
        return suggestions.Count == 0 ? "No suggestions stored yet." : JsonSerializer.Serialize(suggestions, AgentJson.Options);
    }

    [McpServerTool(Name = "agent_get_latest_suggestion"), Description("Get the single most recent agent suggestion.")]
    public async Task<string> GetLatestSuggestion()
    {
        _logger.Log("agent_get_latest_suggestion");
        var suggestions = await Store().GetRecentAsync(1);
        return suggestions.Count == 0 ? "No suggestions stored yet." : JsonSerializer.Serialize(suggestions[0], AgentJson.Options);
    }

    [McpServerTool(Name = "agent_get_summary"), Description("Get the running meeting summary.")]
    public async Task<string> GetSummary()
    {
        _logger.Log("agent_get_summary");
        string? summary = await Store().GetRunningSummaryAsync();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }

        string? path = SafePathValidator.ResolveInsideRoot(Config.Agent.AgentOutputFolder, "meeting-summary.md");
        return path is not null && File.Exists(path) ? File.ReadAllText(path) : "No meeting summary yet.";
    }

    [McpServerTool(Name = "agent_get_action_items"), Description("Get collected action items from the current/last meeting.")]
    public string GetActionItems()
    {
        _logger.Log("agent_get_action_items");
        string? path = SafePathValidator.ResolveInsideRoot(Config.Agent.AgentOutputFolder, "action-items.md");
        return path is not null && File.Exists(path) ? File.ReadAllText(path) : "No action items yet.";
    }

    [McpServerTool(Name = "agent_dismiss_suggestion"), Description("Dismiss a suggestion by id so it stops being shown.")]
    public async Task<string> DismissSuggestion([Description("Suggestion id.")] string id)
    {
        _logger.Log("agent_dismiss_suggestion", id);
        return await Store().DismissAsync(id) ? $"Dismissed {id}." : $"Suggestion not found: {id}";
    }

    [McpServerTool(Name = "agent_set_mode"), Description("Set the agent response mode (Off, SilentObserver, PrivateCoach, HotkeyOnly, InterruptWhenImportant).")]
    public string SetMode([Description("Mode name.")] string mode)
    {
        _logger.Log("agent_set_mode", mode);
        if (!Enum.TryParse<AgentMode>(mode, ignoreCase: true, out var parsed) || parsed == AgentMode.ExperimentalMeetingParticipant)
        {
            return $"Invalid or unavailable mode: {mode}";
        }

        var config = Config;
        config.Agent.Mode = parsed.ToString();
        _configService.Save(config);
        return $"Agent mode set to {parsed}.";
    }

    [McpServerTool(Name = "agent_get_mode"), Description("Get the current agent response mode.")]
    public string GetMode()
    {
        _logger.Log("agent_get_mode");
        return Config.Agent.Mode;
    }

    [McpServerTool(Name = "agent_ask"), Description("Ask the agent a question about the current meeting. Uses the latest transcript + local context with the configured provider.")]
    public async Task<string> Ask([Description("The question to ask.")] string question)
    {
        _logger.Log("agent_ask", question.Length > 60 ? question[..60] : question);
        if (string.IsNullOrWhiteSpace(question))
        {
            return "Question is required.";
        }

        try
        {
            var (suggestions, notice) = await AgentOneShot.AskAsync(Config, question);
            if (suggestions.Count == 0)
            {
                return notice ?? "(no answer produced)";
            }

            var lines = suggestions.Select(s => $"[{s.Priority}] {s.Type}: {s.Title}\n{s.Message}");
            return (notice is null ? "" : notice + "\n\n") + string.Join("\n\n", lines);
        }
        catch (Exception ex)
        {
            return $"Ask failed: {ex.Message}";
        }
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
