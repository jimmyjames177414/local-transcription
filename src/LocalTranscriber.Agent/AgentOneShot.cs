using LocalTranscriber.Context;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Agent;

/// <summary>
/// One-shot agent operations that need no running MeetingAgent: used by
/// `agent ask` (CLI) and `agent_ask` (MCP) from any process.
/// </summary>
public static class AgentOneShot
{
    /// <summary>Newest .jsonl in the transcript folder, or null.</summary>
    public static string? FindLatestTranscript(AppConfig config)
        => Directory.Exists(config.TranscriptFolder)
            ? Directory.EnumerateFiles(config.TranscriptFolder, "*.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

    public static async Task<(IReadOnlyList<AgentSuggestion> Suggestions, string? Notice)> AskAsync(
        AppConfig config,
        string question,
        string? transcriptPath = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = AgentProviderFactory.Create(config);

        transcriptPath ??= FindLatestTranscript(config);
        var events = new List<TranscriptEvent>();
        if (transcriptPath is not null && File.Exists(transcriptPath))
        {
            await using var tailer = new TranscriptEventTailer();
            await foreach (var e in tailer.TailAsync(new TranscriptTailOptions
            {
                JsonlPath = transcriptPath,
                FromStart = true,
                StopAtEndOfFile = true
            }, cancellationToken))
            {
                events.Add(e);
            }
        }

        var contextOptions = new ContextPackOptions
        {
            ContextFolder = config.Agent.ContextFolder,
            MaxTotalCharacters = config.Agent.MaxContextCharacters,
            RequiredFiles = config.Agent.RequiredContextFiles
        };
        var packService = new MarkdownContextPackService();
        string contextText;
        try
        {
            contextText = await new ContextComposer(packService, contextOptions)
                .ComposeAsync(question, cancellationToken);
        }
        catch (Exception)
        {
            contextText = "";
        }

        var result = await resolution.Provider.AnalyzeAsync(new AgentProviderRequest
        {
            WindowEvents = events.TakeLast(config.Agent.MaxTranscriptEventsPerPrompt).ToArray(),
            ContextSummary = contextText,
            KnownSpeakers = events.Select(e => e.Speaker.DisplayName).Distinct().ToArray(),
            UserQuestion = question
        }, cancellationToken);

        if (resolution.Provider is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }

        return (result.Suggestions, resolution.Notice);
    }
}
