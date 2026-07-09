using System.Text.Json;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage;

/// <summary>
/// Resolves the OpenAI API key without ever putting it in the repo or logs.
/// Order: environment variable (name from config) -> secrets.json next to config.
/// secrets.json lives under the data root (dev: ./output/, packaged: %AppData%),
/// which is gitignored.
/// </summary>
public sealed class SecretsService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string SecretsPath { get; }

    public SecretsService(string? secretsPath = null)
    {
        SecretsPath = secretsPath ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(AppPaths.ConfigPath)) ?? "output",
            "secrets.json");
    }

    private sealed class SecretsFile
    {
        public string? OpenAIApiKey { get; set; }
    }

    /// <summary>Returns the API key, or null with a reason when unavailable.</summary>
    public (string? Key, string? DisabledReason) ResolveOpenAIKey(string environmentVariableName)
    {
        string? fromEnv = Environment.GetEnvironmentVariable(environmentVariableName);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return (fromEnv.Trim(), null);
        }

        if (File.Exists(SecretsPath))
        {
            try
            {
                var secrets = JsonSerializer.Deserialize<SecretsFile>(File.ReadAllText(SecretsPath), Json);
                if (!string.IsNullOrWhiteSpace(secrets?.OpenAIApiKey))
                {
                    return (secrets.OpenAIApiKey.Trim(), null);
                }
            }
            catch (JsonException)
            {
                return (null, $"secrets file is malformed: {SecretsPath}");
            }
        }

        return (null, $"no API key: set the {environmentVariableName} environment variable or add openAIApiKey to {SecretsPath}");
    }

    public void SaveOpenAIKey(string key)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(SecretsPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(SecretsPath, JsonSerializer.Serialize(new SecretsFile { OpenAIApiKey = key }, Json));
    }
}
