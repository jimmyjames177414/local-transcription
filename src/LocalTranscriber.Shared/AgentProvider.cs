namespace LocalTranscriber.Shared;

/// <summary>
/// The assistant backend. <see cref="AgentConfig.Provider"/> stores the canonical string (kept as a
/// string so the JSON config stays stable and human-editable); this enum is the typed form used for
/// dispatch so provider identity is not stringly-typed and duplicated across the codebase.
/// </summary>
public enum AgentProvider
{
    /// <summary>OpenAI realtime voice/text (the default).</summary>
    OpenAI,

    /// <summary>Shell out to the local Claude Code CLI in a chosen workspace.</summary>
    ClaudeCli,

    /// <summary>Claude CLI brain + OpenAI realtime voice (speaks Claude's replies).</summary>
    Hybrid
}

/// <summary>Single place that maps between the config string and <see cref="AgentProvider"/>.</summary>
public static class AgentProviders
{
    public const string OpenAI = "openai";
    public const string ClaudeCli = "claude-cli";
    public const string Hybrid = "hybrid";

    /// <summary>Canonical provider ids in UI order — the source for the settings dropdown.</summary>
    public static readonly string[] All = { OpenAI, ClaudeCli, Hybrid };

    /// <summary>Parses a config provider string; unknown or empty falls back to OpenAI (the default).</summary>
    public static AgentProvider Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        ClaudeCli => AgentProvider.ClaudeCli,
        Hybrid => AgentProvider.Hybrid,
        _ => AgentProvider.OpenAI
    };

    /// <summary>True when the config string resolves to the given provider.</summary>
    public static bool Is(string? configValue, AgentProvider provider) => Parse(configValue) == provider;
}
