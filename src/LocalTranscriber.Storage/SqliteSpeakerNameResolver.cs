using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage;

/// <summary>
/// Resolves speaker display names at read time via the override, alias, and known-speakers tables.
/// Precedence: per-event override > session alias > direct known-speaker id > null (unchanged).
/// Uses a short TTL cache to avoid per-event SQLite queries on the agent hot path.
/// </summary>
public sealed class SqliteSpeakerNameResolver : ISpeakerNameResolver
{
    private readonly IEventSpeakerOverrideStore? _overrides;
    private readonly ISpeakerAliasStore _aliases;
    private readonly IKnownSpeakerStore _speakers;
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, (string Name, DateTimeOffset CachedAt)> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteSpeakerNameResolver(ISpeakerAliasStore aliases, IKnownSpeakerStore speakers, TimeSpan? ttl = null)
        : this(null, aliases, speakers, ttl) { }

    public SqliteSpeakerNameResolver(IEventSpeakerOverrideStore? overrides, ISpeakerAliasStore aliases, IKnownSpeakerStore speakers, TimeSpan? ttl = null)
    {
        _overrides = overrides;
        _aliases = aliases;
        _speakers = speakers;
        _ttl = ttl ?? TimeSpan.FromSeconds(5);
    }

    public async Task<string?> ResolveDisplayNameAsync(string sessionId, string speakerId, string? eventId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(speakerId) || speakerId is "mic" or "speaker_unknown")
            return null;

        string cacheKey = $"{sessionId}|{speakerId}|{eventId}";
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

        // Per-event override wins over everything else.
        if (eventId is not null && _overrides is not null)
        {
            var overridden = await _overrides.ResolveAsync(sessionId, eventId, cancellationToken).ConfigureAwait(false);
            if (overridden is not null)
            {
                await CacheAsync(cacheKey, overridden, cancellationToken).ConfigureAwait(false);
                return overridden;
            }
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
            await CacheAsync(cacheKey, name, cancellationToken).ConfigureAwait(false);

        return name;
    }

    private async Task CacheAsync(string key, string name, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _cache[key] = (name, DateTimeOffset.Now); }
        finally { _lock.Release(); }
    }
}
