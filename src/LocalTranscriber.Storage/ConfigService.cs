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
        ConfigPath = configPath ?? AppPaths.ConfigPath;
    }

    public AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return AppPaths.CreateDefaultConfig();
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
    /// Sets a config property by its camelCase JSON name. Dot paths reach nested
    /// objects, e.g. "agent.mode" or "agent.openAI.model".
    /// Returns false when the key does not exist or the value cannot be parsed.
    /// </summary>
    public bool TrySet(AppConfig config, string key, string value)
    {
        object target = config;
        string[] parts = key.Split('.');

        for (int i = 0; i < parts.Length - 1; i++)
        {
            var nested = target.GetType().GetProperties()
                .FirstOrDefault(p => string.Equals(p.Name, parts[i], StringComparison.OrdinalIgnoreCase));
            if (nested is null || nested.GetValue(target) is not object child)
            {
                return false;
            }
            target = child;
        }

        var property = target.GetType().GetProperties()
            .FirstOrDefault(p => string.Equals(p.Name, parts[^1], StringComparison.OrdinalIgnoreCase));
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
            property.SetValue(target, parsed);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
