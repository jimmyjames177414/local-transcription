using System.Text.RegularExpressions;

namespace LocalTranscriber.Context;

public sealed record ContextChunk(
    string SourceFile,
    string Heading,
    string Text
);

public sealed record ContextRetrievalResult(
    IReadOnlyList<ScoredChunk> Chunks
);

public sealed record ScoredChunk(ContextChunk Chunk, double Score);

public interface IContextRetriever
{
    Task<ContextRetrievalResult> RetrieveAsync(string query, int maxChunks = 5, CancellationToken cancellationToken = default);
}

/// <summary>
/// Splits Markdown documents into heading-scoped chunks capped at a max length.
/// </summary>
public static partial class ContextChunker
{
    [GeneratedRegex(@"^(#{1,4})\s+(.*)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    public static IReadOnlyList<ContextChunk> Chunk(ContextDocument document, int maxChunkCharacters = 1200)
    {
        var chunks = new List<ContextChunk>();
        var matches = HeadingRegex().Matches(document.Content);

        if (matches.Count == 0)
        {
            AddSplit(chunks, document.FileName, document.Title, document.Content, maxChunkCharacters);
            return chunks;
        }

        // Preamble before the first heading.
        if (matches[0].Index > 0)
        {
            AddSplit(chunks, document.FileName, document.Title, document.Content[..matches[0].Index], maxChunkCharacters);
        }

        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Index;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : document.Content.Length;
            string heading = matches[i].Groups[2].Value.Trim();
            string body = document.Content[start..end];
            AddSplit(chunks, document.FileName, heading, body, maxChunkCharacters);
        }

        return chunks;
    }

    private static void AddSplit(List<ContextChunk> chunks, string file, string heading, string text, int maxChars)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        for (int offset = 0; offset < text.Length; offset += maxChars)
        {
            string piece = text.Substring(offset, Math.Min(maxChars, text.Length - offset)).Trim();
            if (piece.Length > 0)
            {
                chunks.Add(new ContextChunk(file, heading, piece));
            }
        }
    }
}

/// <summary>
/// Keyword scoring over heading chunks. Document-type weights push decisions/risks up.
/// Deliberately not a vector DB — a handful of markdown files doesn't need one.
/// </summary>
public sealed partial class KeywordContextRetriever : IContextRetriever
{
    private static readonly Dictionary<string, double> FileWeights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["codename-summary.md"] = 1.5,
        ["decisions.md"] = 1.4,
        ["risks.md"] = 1.4,
        ["people.md"] = 1.2,
        ["glossary.md"] = 1.1,
        ["architecture.md"] = 1.0
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "is", "are", "was", "were", "be", "to", "of", "in",
        "on", "at", "for", "we", "i", "you", "it", "that", "this", "with", "as", "have", "has"
    };

    [GeneratedRegex(@"[a-zA-Z][a-zA-Z0-9_-]{2,}")]
    private static partial Regex TokenRegex();

    private readonly IContextPackService _packService;
    private readonly ContextPackOptions _options;

    public KeywordContextRetriever(IContextPackService packService, ContextPackOptions options)
    {
        _packService = packService;
        _options = options;
    }

    public async Task<ContextRetrievalResult> RetrieveAsync(string query, int maxChunks = 5, CancellationToken cancellationToken = default)
    {
        var pack = await _packService.LoadAsync(_options, cancellationToken).ConfigureAwait(false);
        var chunks = pack.Documents.SelectMany(d => ContextChunker.Chunk(d)).ToList();

        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0 || chunks.Count == 0)
        {
            return new ContextRetrievalResult(Array.Empty<ScoredChunk>());
        }

        var scored = new List<ScoredChunk>();
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkTokens = Tokenize(chunk.Text + " " + chunk.Heading);
            if (chunkTokens.Count == 0)
            {
                continue;
            }

            int hits = queryTokens.Count(t => chunkTokens.Contains(t));
            if (hits == 0)
            {
                continue;
            }

            double weight = FileWeights.TryGetValue(Path.GetFileName(chunk.SourceFile), out double w) ? w : 1.0;
            double score = hits * weight / Math.Sqrt(chunkTokens.Count);
            scored.Add(new ScoredChunk(chunk, score));
        }

        return new ContextRetrievalResult(scored
            .OrderByDescending(s => s.Score)
            .Take(maxChunks)
            .ToList());
    }

    public static HashSet<string> Tokenize(string text)
        => TokenRegex().Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(t => !StopWords.Contains(t))
            .ToHashSet();
}

/// <summary>
/// Builds the provider's context string: required summary always, then the most
/// relevant chunks for the current conversation, within budget.
/// </summary>
public sealed class ContextComposer
{
    private readonly IContextPackService _packService;
    private readonly IContextRetriever _retriever;
    private readonly ContextPackOptions _options;

    public ContextComposer(IContextPackService packService, ContextPackOptions options, IContextRetriever? retriever = null)
    {
        _packService = packService;
        _options = options;
        _retriever = retriever ?? new KeywordContextRetriever(packService, options);
    }

    public async Task<string> ComposeAsync(string conversationText, CancellationToken cancellationToken = default)
    {
        var pack = await _packService.LoadAsync(_options, cancellationToken).ConfigureAwait(false);
        var summary = pack.Documents.FirstOrDefault(d =>
            string.Equals(d.FileName, "codename-summary.md", StringComparison.OrdinalIgnoreCase));

        var result = await _retriever.RetrieveAsync(conversationText, maxChunks: 5, cancellationToken).ConfigureAwait(false);

        var sb = new System.Text.StringBuilder();
        int budget = _options.MaxTotalCharacters;

        if (summary is not null)
        {
            string text = $"## {summary.Title} (codename-summary.md)\n{summary.Content}\n\n";
            sb.Append(text);
            budget -= text.Length;
        }

        foreach (var scored in result.Chunks)
        {
            if (scored.Chunk.SourceFile.Equals("codename-summary.md", StringComparison.OrdinalIgnoreCase))
            {
                continue; // already included whole
            }

            string text = $"## Relevant: {scored.Chunk.Heading} ({scored.Chunk.SourceFile})\n{scored.Chunk.Text}\n\n";
            if (text.Length > budget)
            {
                break;
            }
            sb.Append(text);
            budget -= text.Length;
        }

        return sb.ToString().TrimEnd();
    }
}
