namespace LocalTranscriber.Context;

public sealed record ContextDocument(
    string FileName,
    string Title,
    string Content,
    bool Truncated
);

public sealed record ContextPack(
    IReadOnlyList<ContextDocument> Documents,
    string CombinedText,
    IReadOnlyList<string> Warnings
)
{
    public static ContextPack Empty { get; } = new(Array.Empty<ContextDocument>(), "", Array.Empty<string>());
}

public sealed record ContextPackOptions
{
    public required string ContextFolder { get; init; }
    public int MaxTotalCharacters { get; init; } = 20000;
    public int MaxCharactersPerDocument { get; init; } = 8000;
    public IReadOnlyList<string> RequiredFiles { get; init; } = new[] { "codename-summary.md" };
}

public interface IContextPackService
{
    Task<ContextPack> LoadAsync(ContextPackOptions options, CancellationToken cancellationToken = default);

    /// <summary>Lists loadable documents without reading full contents.</summary>
    Task<IReadOnlyList<string>> ListDocumentsAsync(ContextPackOptions options, CancellationToken cancellationToken = default);

    /// <summary>Reads one named document, restricted to the context folder. Null when blocked/missing.</summary>
    Task<ContextDocument?> ReadDocumentAsync(ContextPackOptions options, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Validates the pack: folder exists, required files present, extensions sane.</summary>
    Task<IReadOnlyList<string>> ValidateAsync(ContextPackOptions options, CancellationToken cancellationToken = default);
}
