namespace LocalTranscriber.Shared;

/// <summary>
/// Resolves current display names for speakers by ID at read time,
/// so renames propagate retroactively without modifying transcript files.
/// </summary>
public interface ISpeakerNameResolver
{
    /// <summary>
    /// Returns the current display name for the given speaker, or null when unknown/unchanged.
    /// For session-local IDs (e.g. "session_speaker_1"), uses the alias table.
    /// For enrolled speaker IDs, returns display name directly.
    /// Returns null for "mic" and "speaker_unknown" (no override needed).
    /// </summary>
    Task<string?> ResolveDisplayNameAsync(string sessionId, string speakerId, CancellationToken cancellationToken = default);
}
