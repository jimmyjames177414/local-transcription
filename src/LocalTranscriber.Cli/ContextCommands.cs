using System.CommandLine;
using LocalTranscriber.Context;
using LocalTranscriber.Storage;

namespace LocalTranscriber.Cli;

public static class ContextCommands
{
    public static Command Build(ConfigService configService)
    {
        var context = new Command("context", "Project/codename context pack for the meeting agent.");
        context.AddCommand(BuildList(configService));
        context.AddCommand(BuildShow(configService));
        context.AddCommand(BuildValidate(configService));
        return context;
    }

    private static ContextPackOptions Options(ConfigService configService)
    {
        var agent = configService.Load().Agent;
        return new ContextPackOptions
        {
            ContextFolder = agent.ContextFolder,
            MaxTotalCharacters = agent.MaxContextCharacters,
            RequiredFiles = agent.RequiredContextFiles
        };
    }

    private static Command BuildList(ConfigService configService)
    {
        var cmd = new Command("list", "List context documents.");
        cmd.SetHandler(async () =>
        {
            var service = new MarkdownContextPackService();
            var docs = await service.ListDocumentsAsync(Options(configService));
            if (docs.Count == 0)
            {
                Console.WriteLine("No context documents found.");
                return;
            }
            foreach (var d in docs)
            {
                Console.WriteLine(d);
            }
        });
        return cmd;
    }

    private static Command BuildShow(ConfigService configService)
    {
        var nameArg = new Argument<string>("name", "Document file name, e.g. codename-summary.md");
        var cmd = new Command("show", "Print one context document.");
        cmd.AddArgument(nameArg);
        cmd.SetHandler(async (string name) =>
        {
            var service = new MarkdownContextPackService();
            var doc = await service.ReadDocumentAsync(Options(configService), name);
            if (doc is null)
            {
                Console.Error.WriteLine($"Not found or not allowed: {name}");
                Environment.ExitCode = 1;
                return;
            }
            Console.WriteLine(doc.Content);
        }, nameArg);
        return cmd;
    }

    private static Command BuildValidate(ConfigService configService)
    {
        var cmd = new Command("validate", "Validate the context pack (required files, budget, readability).");
        cmd.SetHandler(async () =>
        {
            var service = new MarkdownContextPackService();
            var problems = await service.ValidateAsync(Options(configService));
            if (problems.Count == 0)
            {
                Console.WriteLine("Context pack OK.");
                return;
            }
            foreach (var p in problems)
            {
                Console.WriteLine($"- {p}");
            }
            Environment.ExitCode = 1;
        });
        return cmd;
    }
}
