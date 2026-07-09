using System.Text.Json;

namespace LocalTranscriber.Storage;

/// <summary>
/// Temporary JSON-file speaker store used until the SQLite store lands (Phase 6).
/// </summary>
public sealed class JsonSpeakerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public JsonSpeakerStore(string? path = null)
    {
        _path = path ?? Path.Combine("output", "speakers.json");
    }

    public sealed record StoredSpeaker(string Id, string DisplayName);

    public IReadOnlyList<StoredSpeaker> List()
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<StoredSpeaker>();
        }

        return JsonSerializer.Deserialize<List<StoredSpeaker>>(File.ReadAllText(_path), JsonOptions)
            ?? new List<StoredSpeaker>();
    }

    public bool Rename(string fromName, string toName)
    {
        var speakers = List().ToList();
        var existing = speakers.FindIndex(s => string.Equals(s.DisplayName, fromName, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            speakers[existing] = speakers[existing] with { DisplayName = toName };
        }
        else
        {
            speakers.Add(new StoredSpeaker(Guid.NewGuid().ToString("N"), toName));
        }

        Save(speakers);
        return true;
    }

    public bool Forget(string name)
    {
        var speakers = List().ToList();
        int removed = speakers.RemoveAll(s => string.Equals(s.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return false;
        }

        Save(speakers);
        return true;
    }

    private void Save(List<StoredSpeaker> speakers)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(speakers, JsonOptions));
    }
}
