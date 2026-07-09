using System.CommandLine;
using LocalTranscriber.Agent;
using LocalTranscriber.Shared;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Cli;

public static class AgentCommands
{
    public static Command Build(ConfigService configService)
    {
        var agent = new Command("agent", "Live meeting AI sidecar (optional; offline fake provider by default).");
        agent.AddCommand(BuildTailEvents());
        agent.AddCommand(BuildStartFake(configService));
        agent.AddCommand(BuildStart(configService));
        agent.AddCommand(BuildTestOpenAI(configService));
        agent.AddCommand(BuildStatus(configService));
        agent.AddCommand(BuildSuggestions(configService));
        agent.AddCommand(BuildDismiss(configService));
        agent.AddCommand(BuildSummary(configService));
        agent.AddCommand(BuildActionItems(configService));
        return agent;
    }

    private static SqliteAgentSuggestionStore Store(ConfigService configService)
        => new(new SqliteDatabase(configService.Load().DatabasePath));

    private static Command BuildTailEvents()
    {
        var fileOpt = new Option<string>("--file", "Transcript .jsonl file to tail.") { IsRequired = true };
        var fromStartOpt = new Option<bool>("--from-start", () => false, "Read from the beginning instead of the checkpoint.");
        var cmd = new Command("tail-events", "Tail transcript events from a live .jsonl file (Ctrl+C to stop).");
        cmd.AddOption(fileOpt);
        cmd.AddOption(fromStartOpt);
        cmd.SetHandler(async (string file, bool fromStart) =>
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            await using var tailer = new TranscriptEventTailer();
            var options = new TranscriptTailOptions
            {
                JsonlPath = file,
                FromStart = fromStart,
                CheckpointPath = Path.Combine("output", "agent", "tailer-checkpoint.json")
            };

            Console.WriteLine($"Tailing {file} (Ctrl+C to stop)...");
            try
            {
                await foreach (var e in tailer.TailAsync(options, stop.Token))
                {
                    Console.WriteLine($"[{e.Timestamp.ToLocalTime():HH:mm:ss}] {e.Speaker.DisplayName} ({e.Source}): {e.Text}");
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, fileOpt, fromStartOpt);
        return cmd;
    }

    private static Command BuildStartFake(ConfigService configService)
    {
        var transcriptOpt = new Option<string>("--transcript", "Transcript .jsonl to watch.") { IsRequired = true };
        var contextOpt = new Option<string?>("--context", () => null, "Context folder (default from config).");
        var cmd = new Command("start-fake", "Run the meeting agent with the offline fake provider (blocks until Ctrl+C).");
        cmd.AddOption(transcriptOpt);
        cmd.AddOption(contextOpt);
        cmd.SetHandler(async (string transcript, string? context) =>
        {
            var config = configService.Load();
            var sink = new CompositeAgentSuggestionSink(config.Agent.AgentOutputFolder, Store(configService));
            await using var agent = new MeetingAgent(new FakeMeetingAgentProvider(), sink: sink);

            var options = new MeetingAgentOptions
            {
                TranscriptJsonlPath = transcript,
                ContextFolder = context ?? config.Agent.ContextFolder,
                AgentOutputFolder = config.Agent.AgentOutputFolder,
                Mode = AgentMode.SilentObserver,
                RollingWindowMinutes = config.Agent.RollingWindowMinutes,
                SuggestionIntervalSeconds = Math.Max(2, config.Agent.SuggestionIntervalSeconds),
                MaxTranscriptEventsPerPrompt = config.Agent.MaxTranscriptEventsPerPrompt,
                MaxContextCharacters = config.Agent.MaxContextCharacters,
                RequiredContextFiles = config.Agent.RequiredContextFiles
            };

            await agent.StartAsync(options);
            Console.WriteLine($"Agent running (fake provider) on {transcript}. Ctrl+C to stop.");
            Console.WriteLine($"Suggestions -> {Path.Combine(config.Agent.AgentOutputFolder, "suggestions.jsonl")} + SQLite.");
            Console.WriteLine("Note: runs in-process. Cross-process agent control arrives with the UI/MCP integration.");

            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            try
            {
                await foreach (var s in agent.StreamSuggestionsAsync(stop.Token))
                {
                    Console.WriteLine($"[{s.Priority}] {s.Type}: {s.Title} — {s.Message}");
                }
            }
            catch (OperationCanceledException)
            {
            }

            await agent.StopAsync();
            var status = await agent.GetStatusAsync();
            Console.WriteLine($"Agent stopped. Events seen: {status.EventsSeen}, suggestions: {status.SuggestionsEmitted}.");
        }, transcriptOpt, contextOpt);
        return cmd;
    }

    private static Command BuildStart(ConfigService configService)
    {
        var transcriptOpt = new Option<string>("--transcript", "Transcript .jsonl to watch.") { IsRequired = true };
        var contextOpt = new Option<string?>("--context", () => null, "Context folder (default from config).");
        var cmd = new Command("start", "Run the meeting agent with the CONFIGURED provider (agent.provider). Blocks until Ctrl+C.");
        cmd.AddOption(transcriptOpt);
        cmd.AddOption(contextOpt);
        cmd.SetHandler(async (string transcript, string? context) =>
        {
            var config = configService.Load();
            var resolution = AgentProviderFactory.Create(config);
            if (resolution.Notice is not null)
            {
                Console.WriteLine(resolution.Notice);
            }

            var sink = new CompositeAgentSuggestionSink(config.Agent.AgentOutputFolder, Store(configService));
            await using var agent = new MeetingAgent(resolution.Provider, sink: sink);

            await agent.StartAsync(new MeetingAgentOptions
            {
                TranscriptJsonlPath = transcript,
                ContextFolder = context ?? config.Agent.ContextFolder,
                AgentOutputFolder = config.Agent.AgentOutputFolder,
                Mode = Enum.TryParse<AgentMode>(config.Agent.Mode, out var mode) && mode != AgentMode.Off ? mode : AgentMode.SilentObserver,
                RollingWindowMinutes = config.Agent.RollingWindowMinutes,
                SuggestionIntervalSeconds = Math.Max(2, config.Agent.SuggestionIntervalSeconds),
                MaxTranscriptEventsPerPrompt = config.Agent.MaxTranscriptEventsPerPrompt,
                MaxContextCharacters = config.Agent.MaxContextCharacters,
                RequiredContextFiles = config.Agent.RequiredContextFiles
            });

            Console.WriteLine($"Agent running (provider: {resolution.Provider.Name}) on {transcript}. Ctrl+C to stop.");

            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            try
            {
                await foreach (var s in agent.StreamSuggestionsAsync(stop.Token))
                {
                    Console.WriteLine($"[{s.Priority}] {s.Type}: {s.Title} — {s.Message}");
                }
            }
            catch (OperationCanceledException)
            {
            }

            await agent.StopAsync();
        }, transcriptOpt, contextOpt);
        return cmd;
    }

    private static Command BuildTestOpenAI(ConfigService configService)
    {
        var transcriptOpt = new Option<string>("--transcript", "Transcript .jsonl to analyze once.") { IsRequired = true };
        var contextOpt = new Option<string?>("--context", () => null, "Context folder (default from config).");
        var cmd = new Command("test-openai", "One-shot: send the transcript + context to the OpenAI text provider and print suggestions.");
        cmd.AddOption(transcriptOpt);
        cmd.AddOption(contextOpt);
        cmd.SetHandler(async (string transcript, string? context) =>
        {
            var config = configService.Load();
            config.Agent.Provider = "openai";
            config.Agent.OpenAI.Enabled = true; // explicit test command implies consent
            var resolution = AgentProviderFactory.Create(config);
            if (resolution.Provider.Name != "openai")
            {
                Console.Error.WriteLine(resolution.Notice);
                Environment.ExitCode = 1;
                return;
            }

            // Read the whole transcript once.
            var events = new List<LocalTranscriber.Shared.TranscriptEvent>();
            await using (var tailer = new TranscriptEventTailer())
            {
                await foreach (var e in tailer.TailAsync(new TranscriptTailOptions
                {
                    JsonlPath = transcript,
                    FromStart = true,
                    StopAtEndOfFile = true
                }))
                {
                    events.Add(e);
                }
            }

            if (events.Count == 0)
            {
                Console.Error.WriteLine($"No transcript events found in {transcript}");
                Environment.ExitCode = 1;
                return;
            }

            var contextService = new LocalTranscriber.Context.MarkdownContextPackService();
            var pack = await contextService.LoadAsync(new LocalTranscriber.Context.ContextPackOptions
            {
                ContextFolder = context ?? config.Agent.ContextFolder,
                MaxTotalCharacters = config.Agent.MaxContextCharacters,
                RequiredFiles = config.Agent.RequiredContextFiles
            });

            Console.WriteLine($"Sending {events.Count} transcript events + {pack.Documents.Count} context docs to model '{config.Agent.OpenAI.Model}'...");
            try
            {
                var result = await resolution.Provider.AnalyzeAsync(new AgentProviderRequest
                {
                    WindowEvents = events.TakeLast(config.Agent.MaxTranscriptEventsPerPrompt).ToArray(),
                    ContextSummary = pack.CombinedText,
                    KnownSpeakers = events.Select(e => e.Speaker.DisplayName).Distinct().ToArray()
                });

                if (result.Suggestions.Count == 0)
                {
                    Console.WriteLine("(model returned no suggestions)");
                }
                foreach (var s in result.Suggestions)
                {
                    Console.WriteLine($"[{s.Priority}] {s.Type}: {s.Title}");
                    Console.WriteLine($"    {s.Message}  (confidence: {s.Confidence:F2})");
                }
                if (result.RunningSummaryUpdate is not null)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Summary: {result.RunningSummaryUpdate}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"OpenAI call failed: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, transcriptOpt, contextOpt);
        return cmd;
    }

    private static Command BuildStatus(ConfigService configService)
    {
        var cmd = new Command("status", "Show agent output state (stored suggestions and summary).");
        cmd.SetHandler(async () =>
        {
            var store = Store(configService);
            var recent = await store.GetRecentAsync(1);
            string? summary = await store.GetRunningSummaryAsync();
            Console.WriteLine($"Config: enabled={configService.Load().Agent.Enabled}, mode={configService.Load().Agent.Mode}, provider={configService.Load().Agent.Provider}");
            Console.WriteLine($"Latest stored suggestion: {(recent.Count > 0 ? $"{recent[0].CreatedAt.ToLocalTime():HH:mm:ss} [{recent[0].Priority}] {recent[0].Title}" : "(none)")}");
            Console.WriteLine($"Running summary: {(string.IsNullOrWhiteSpace(summary) ? "(none)" : summary)}");
        });
        return cmd;
    }

    private static Command BuildSuggestions(ConfigService configService)
    {
        var countOpt = new Option<int>("--count", () => 20, "How many recent suggestions to show.");
        var allOpt = new Option<bool>("--include-dismissed", () => false, "Include dismissed suggestions.");
        var cmd = new Command("suggestions", "Show recent agent suggestions.");
        cmd.AddOption(countOpt);
        cmd.AddOption(allOpt);
        cmd.SetHandler(async (int count, bool includeDismissed) =>
        {
            var suggestions = await Store(configService).GetRecentAsync(count, includeDismissed);
            if (suggestions.Count == 0)
            {
                Console.WriteLine("No suggestions stored yet.");
                return;
            }

            foreach (var s in suggestions)
            {
                string dismissed = s.IsDismissed ? " (dismissed)" : "";
                Console.WriteLine($"{s.CreatedAt.ToLocalTime():HH:mm:ss} [{s.Priority}] {s.Type}: {s.Title}{dismissed}");
                Console.WriteLine($"    {s.Message}");
                Console.WriteLine($"    id: {s.Id}");
            }
        }, countOpt, allOpt);
        return cmd;
    }

    private static Command BuildDismiss(ConfigService configService)
    {
        var idOpt = new Option<string>("--id", "Suggestion id to dismiss.") { IsRequired = true };
        var cmd = new Command("dismiss", "Dismiss a suggestion.");
        cmd.AddOption(idOpt);
        cmd.SetHandler(async (string id) =>
        {
            if (await Store(configService).DismissAsync(id))
            {
                Console.WriteLine($"Dismissed {id}.");
            }
            else
            {
                Console.Error.WriteLine($"Suggestion not found: {id}");
                Environment.ExitCode = 1;
            }
        }, idOpt);
        return cmd;
    }

    private static Command BuildSummary(ConfigService configService)
    {
        var cmd = new Command("summary", "Show the running meeting summary.");
        cmd.SetHandler(async () =>
        {
            string? summary = await Store(configService).GetRunningSummaryAsync();
            string mdPath = Path.Combine(configService.Load().Agent.AgentOutputFolder, "meeting-summary.md");
            if (!string.IsNullOrWhiteSpace(summary))
            {
                Console.WriteLine(summary);
            }
            else if (File.Exists(mdPath))
            {
                Console.WriteLine(File.ReadAllText(mdPath));
            }
            else
            {
                Console.WriteLine("No meeting summary yet.");
            }
        });
        return cmd;
    }

    private static Command BuildActionItems(ConfigService configService)
    {
        var cmd = new Command("action-items", "Show collected action items.");
        cmd.SetHandler(() =>
        {
            string path = Path.Combine(configService.Load().Agent.AgentOutputFolder, "action-items.md");
            Console.WriteLine(File.Exists(path) ? File.ReadAllText(path) : "No action items yet.");
        });
        return cmd;
    }
}
