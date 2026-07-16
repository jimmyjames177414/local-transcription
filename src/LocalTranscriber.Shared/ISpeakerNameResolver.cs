namespace LocalTranscriber.Shared;

/// <summary>
/// Resolves current display names for speakers by ID at read time,
/// so renames propagate retroactively without modifying transcript files.
/// </summary>
public interface ISpeakerNameResolver
{
    /// <summary>
    /// Returns the current display name for the given speaker, or null when unknown/unchanged.
    /// When <paramref name="eventId"/> is supplied, a per-event override ("just this line") is
    /// checked first and takes precedence over the alias/known-speaker chain.
    /// Returns null for "mic" and "speaker_unknown" (no override needed).
    /// </summary>
    Task<string?> ResolveDisplayNameAsync(string sessionId, string speakerId, string? eventId = null, CancellationToken cancellationToken = default);
}
