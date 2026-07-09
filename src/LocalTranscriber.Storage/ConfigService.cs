using System.Text.Json;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string ConfigPath { get; }

    public ConfigService(string? configPath = null)
    {
        ConfigPath = configPath ?? Path.Combine("output", "config.json");
    }

    public AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return new AppConfig();
        }

        string json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
    }

    public void Save(AppConfig config)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(ConfigPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    /// <summary>
    /// Sets a config property by its camelCase JSON name (e.g. "transcriptFolder").
    /// Returns false when the key does not exist or the value cannot be parsed.
    /// </summary>
    public bool TrySet(AppConfig config, string key, string value)
    {
        var property = typeof(AppConfig).GetProperties()
            .FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
        if (property is null || !property.CanWrite)
        {
            return false;
        }

        try
        {
            object parsed = property.PropertyType switch
            {
                var t when t == typeof(string) => value,
                var t when t == typeof(bool) => bool.Parse(value),
                var t when t == typeof(int) => int.Parse(value),
                var t when t == typeof(double) => double.Parse(value),
                _ => throw new NotSupportedException($"Unsupported config type {property.PropertyType.Name}")
            };
            property.SetValue(config, parsed);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
