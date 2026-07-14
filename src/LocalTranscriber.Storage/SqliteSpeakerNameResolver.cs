using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage;

/// <summary>
/// Resolves speaker display names at read time via the alias table + known_speakers.
/// Uses a short TTL cache to avoid per-event SQLite queries on the agent hot path.
/// </summary>
public sealed class SqliteSpeakerNameResolver : ISpeakerNameResolver
{
    private readonly ISpeakerAliasStore _aliases;
    private readonly IKnownSpeakerStore _speakers;
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, (string Name, DateTimeOffset CachedAt)> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteSpeakerNameResolver(ISpeakerAliasStore aliases, IKnownSpeakerStore speakers, TimeSpan? ttl = null)
    {
        _aliases = aliases;
        _speakers = speakers;
        _ttl = ttl ?? TimeSpan.FromSeconds(5);
    }

    public async Task<string?> ResolveDisplayNameAsync(string sessionId, string speakerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(speakerId) || speakerId is "mic" or "speaker_unknown")
            return null;

        string cacheKey = $"{sessionId}|{speakerId}";
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.Now - cached.CachedAt < _ttl)
                return cached.Name;
        }
        finally
        {
            _lock.Release();
        }

        string? resolvedId = speakerId;

        // Session-local ids go through the alias table first.
        if (speakerId.StartsWith("session_", StringComparison.Ordinal))
        {
            resolvedId = await _aliases.ResolveAsync(sessionId, speakerId, cancellationToken).ConfigureAwait(false);
            if (resolvedId is null) return null;
        }

        // Resolve known_speaker_id → display_name.
        var allSpeakers = await _speakers.ListAsync(cancellationToken).ConfigureAwait(false);
        string? name = allSpeakers.FirstOrDefault(s => s.Id == resolvedId)?.DisplayName;

        if (name is not null)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { _cache[cacheKey] = (name, DateTimeOffset.Now); }
            finally { _lock.Release(); }
        }

        return name;
    }
}
