namespace LocalTranscriber.Agent;

public sealed record AgentResponseDecision(
    bool Store,
    bool Show,
    bool Speak,
    string? Reason = null
)
{
    public static readonly AgentResponseDecision Suppressed = new(false, false, false, "suppressed");
}

public sealed record AgentPolicyOptions
{
    public AgentSuggestionPriority MinimumPriorityToSpeak { get; init; } = AgentSuggestionPriority.High;
    public IReadOnlyList<AgentMode> SpeakOnlyInModes { get; init; } = new[] { AgentMode.PrivateCoach, AgentMode.InterruptWhenImportant };
    public TimeSpan CooldownPerType { get; init; } = TimeSpan.FromSeconds(45);
    public double MinimumConfidenceToShow { get; init; } = 0.3;
    public bool VoiceEnabled { get; init; }
}

/// <summary>
/// Per-type cooldown so the agent cannot machine-gun the same kind of suggestion.
/// </summary>
public sealed class AgentCooldownPolicy
{
    private readonly TimeSpan _cooldown;
    private readonly Dictionary<AgentSuggestionType, DateTimeOffset> _lastShown = new();
    private readonly object _lock = new();

    public AgentCooldownPolicy(TimeSpan cooldown)
    {
        _cooldown = cooldown;
    }

    /// <summary>True when this type is out of cooldown; records the hit when allowed.</summary>
    public bool TryPass(AgentSuggestionType type, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_lastShown.TryGetValue(type, out var last) && now - last < _cooldown)
            {
                return false;
            }

            _lastShown[type] = now;
            return true;
        }
    }
}

/// <summary>
/// Session-wide duplicate suppression by normalized type+title, plus dismissed-title memory.
/// </summary>
public sealed class AgentSuggestionDeduplicator
{
    private readonly HashSet<string> _seen = new();
    private readonly HashSet<string> _dismissedTitles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public bool IsNew(AgentSuggestion suggestion)
    {
        lock (_lock)
        {
            return _seen.Add($"{suggestion.Type}|{suggestion.Title.Trim().ToLowerInvariant()}");
        }
    }

    public void MarkDismissed(string title)
    {
        lock (_lock)
        {
            _dismissedTitles.Add(title.Trim());
        }
    }

    public bool WasDismissed(AgentSuggestion suggestion)
    {
        lock (_lock)
        {
            return _dismissedTitles.Contains(suggestion.Title.Trim());
        }
    }
}

/// <summary>
/// Decides what happens to each suggestion given the mode: stored, shown live, spoken.
/// The goal is useful-without-annoying — silence beats noise.
/// </summary>
public sealed class AgentResponsePolicy
{
    private readonly AgentPolicyOptions _options;
    private readonly AgentCooldownPolicy _cooldown;
    private readonly AgentSuggestionDeduplicator _dedup;

    public AgentResponsePolicy(AgentPolicyOptions? options = null, AgentCooldownPolicy? cooldown = null, AgentSuggestionDeduplicator? dedup = null)
    {
        _options = options ?? new AgentPolicyOptions();
        _cooldown = cooldown ?? new AgentCooldownPolicy(_options.CooldownPerType);
        _dedup = dedup ?? new AgentSuggestionDeduplicator();
    }

    public AgentSuggestionDeduplicator Deduplicator => _dedup;

    public AgentResponseDecision Decide(AgentSuggestion suggestion, AgentMode mode, bool isAskResponse = false, DateTimeOffset? now = null)
    {
        now ??= DateTimeOffset.Now;

        if (mode == AgentMode.Off)
        {
            return AgentResponseDecision.Suppressed with { Reason = "mode is Off" };
        }

        // Explicit asks are always delivered (the user requested them).
        if (isAskResponse)
        {
            return new AgentResponseDecision(Store: true, Show: true, Speak: false, "ask response");
        }

        if (!_dedup.IsNew(suggestion))
        {
            return AgentResponseDecision.Suppressed with { Reason = "duplicate" };
        }

        if (_dedup.WasDismissed(suggestion))
        {
            return new AgentResponseDecision(Store: true, Show: false, Speak: false, "previously dismissed");
        }

        if (suggestion.Confidence is double c && c < _options.MinimumConfidenceToShow)
        {
            return new AgentResponseDecision(Store: true, Show: false, Speak: false, "low confidence");
        }

        bool importantEnoughToShow = mode switch
        {
            AgentMode.SilentObserver => true,
            AgentMode.PrivateCoach => true,
            AgentMode.HotkeyOnly => false, // collect silently; user pulls with ask
            AgentMode.InterruptWhenImportant => suggestion.Priority >= AgentSuggestionPriority.High,
            _ => false
        };

        if (importantEnoughToShow && !_cooldown.TryPass(suggestion.Type, now.Value))
        {
            return new AgentResponseDecision(Store: true, Show: false, Speak: false, "cooldown");
        }

        bool speak = _options.VoiceEnabled
            && importantEnoughToShow
            && _options.SpeakOnlyInModes.Contains(mode)
            && suggestion.Priority >= _options.MinimumPriorityToSpeak;

        return new AgentResponseDecision(Store: true, Show: importantEnoughToShow, Speak: speak);
    }
}
