namespace LocalTranscriber.Shared;

/// <summary>
/// Validates that file paths stay inside an allowed root folder.
/// Used by the CLI and the MCP server to prevent reads/writes outside
/// the configured transcript directory.
/// </summary>
public static class SafePathValidator
{
    /// <summary>
    /// Returns the fully-resolved path if <paramref name="candidate"/> is inside
    /// <paramref name="allowedRoot"/>; otherwise returns null.
    /// </summary>
    public static string? ResolveInsideRoot(string allowedRoot, string candidate)
    {
        if (string.IsNullOrWhiteSpace(allowedRoot) || string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        string root = Path.GetFullPath(allowedRoot);
        string full = Path.IsPathRooted(candidate)
            ? Path.GetFullPath(candidate)
            : Path.GetFullPath(Path.Combine(root, candidate));

        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        bool inside = full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);

        return inside ? full : null;
    }

    public static bool IsInsideRoot(string allowedRoot, string candidate)
        => ResolveInsideRoot(allowedRoot, candidate) is not null;
}
