using LocalTranscriber.Shared;

namespace LocalTranscriber.Context;

/// <summary>
/// Loads project/codename context from Markdown files inside the configured folder only.
/// Path traversal is blocked, only .md is read, oversized documents are truncated,
/// and the combined text is budgeted for AI prompts.
/// </summary>
public sealed class MarkdownContextPackService : IContextPackService
{
    /// <summary>Priority order for the prompt budget; unknown files come after, alphabetically.</summary>
    private static readonly string[] PreferredOrder =
    {
        "codename-summary.md", "people.md", "glossary.md", "decisions.md", "risks.md", "architecture.md"
    };

    public Task<ContextPack> LoadAsync(ContextPackOptions options, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var documents = new List<ContextDocument>();

        if (!Directory.Exists(options.ContextFolder))
        {
            warnings.Add($"Context folder not found: {Path.GetFullPath(options.ContextFolder)}");
            return Task.FromResult(new ContextPack(documents, "", warnings));
        }

        foreach (string required in options.RequiredFiles)
        {
            if (SafePathValidator.ResolveInsideRoot(options.ContextFolder, required) is not string requiredPath || !File.Exists(requiredPath))
            {
                warnings.Add($"Required context file missing: {required}");
            }
        }

        var files = ListMarkdownFiles(options.ContextFolder)
            .OrderBy(f =>
            {
                int i = Array.IndexOf(PreferredOrder, Path.GetFileName(f).ToLowerInvariant());
                return i < 0 ? PreferredOrder.Length : i;
            })
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int remaining = options.MaxTotalCharacters;
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining <= 0)
            {
                warnings.Add($"Context budget exhausted; skipped {Path.GetFileName(file)} and later files.");
                break;
            }

            var doc = ReadDocumentFile(file, Math.Min(options.MaxCharactersPerDocument, remaining));
            documents.Add(doc);
            remaining -= doc.Content.Length;
            if (doc.Truncated)
            {
                warnings.Add($"Truncated {doc.FileName} to fit the context budget.");
            }
        }

        string combined = string.Join("\n\n", documents.Select(d => $"## {d.Title} ({d.FileName})\n{d.Content}"));
        return Task.FromResult(new ContextPack(documents, combined, warnings));
    }

    public Task<IReadOnlyList<string>> ListDocumentsAsync(ContextPackOptions options, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> result = Directory.Exists(options.ContextFolder)
            ? ListMarkdownFiles(options.ContextFolder)
                .Select(f => Path.GetRelativePath(options.ContextFolder, f).Replace('\\', '/'))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Array.Empty<string>();
        return Task.FromResult(result);
    }

    public Task<ContextDocument?> ReadDocumentAsync(ContextPackOptions options, string fileName, CancellationToken cancellationToken = default)
    {
        if (!IsMarkdownName(fileName))
        {
            return Task.FromResult<ContextDocument?>(null);
        }

        string? path = SafePathValidator.ResolveInsideRoot(options.ContextFolder, fileName);
        if (path is null || !File.Exists(path))
        {
            return Task.FromResult<ContextDocument?>(null);
        }

        return Task.FromResult<ContextDocument?>(ReadDocumentFile(path, options.MaxCharactersPerDocument));
    }

    public async Task<IReadOnlyList<string>> ValidateAsync(ContextPackOptions options, CancellationToken cancellationToken = default)
    {
        var pack = await LoadAsync(options, cancellationToken).ConfigureAwait(false);
        var problems = new List<string>(pack.Warnings);
        if (pack.Documents.Count == 0 && Directory.Exists(options.ContextFolder))
        {
            problems.Add("Context folder contains no .md documents.");
        }
        return problems;
    }

    private static IEnumerable<string> ListMarkdownFiles(string folder)
        => Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories)
            .Where(f => IsMarkdownName(f));

    private static bool IsMarkdownName(string name)
        => string.Equals(Path.GetExtension(name), ".md", StringComparison.OrdinalIgnoreCase);

    private static ContextDocument ReadDocumentFile(string path, int maxCharacters)
    {
        string content = File.ReadAllText(path);
        bool truncated = content.Length > maxCharacters;
        if (truncated)
        {
            content = content[..maxCharacters];
        }

        string title = content.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim()
            ?? Path.GetFileNameWithoutExtension(path);

        return new ContextDocument(Path.GetFileName(path), title, content, truncated);
    }
}
