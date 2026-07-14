using System.Text;
using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage;

/// <summary>
/// Writes a finished session as a markdown file in the format the "minutes" tool
/// (github.com/silverstein/minutes) indexes: YAML frontmatter (title/type/date/duration/
/// action_items/decisions) + ## Summary + ## Transcript. Pure file output — running minutes'
/// own indexer/MCP is the user's environment.
/// </summary>
public static class MinutesExporter
{
    /// <summary>Writes the file and returns its path. Folder is created; "~" expands to the user profile.</summary>
    public static string Export(
        SessionRecord session,
        IReadOnlyList<TranscriptEvent> events,
        NotesDocument? notes,
        string minutesFolder,
        string? title = null)
    {
        string folder = ResolveFolder(minutesFolder);
        Directory.CreateDirectory(folder);

        var startedLocal = session.StartedAt.ToLocalTime();
        string path = UniquePath(folder, $"{startedLocal:yyyy-MM-dd}-meeting-{session.Id}.md");
        File.WriteAllText(path, Render(session, events, notes, title));
        return path;
    }

    internal static string Render(
        SessionRecord session,
        IReadOnlyList<TranscriptEvent> events,
        NotesDocument? notes,
        string? title)
    {
        var startedLocal = session.StartedAt.ToLocalTime();
        var sb = new StringBuilder();

        sb.AppendLine("---");
        sb.AppendLine($"title: {Quote(title ?? $"Meeting {startedLocal:yyyy-MM-dd HH:mm}")}");
        sb.AppendLine("type: meeting");
        sb.AppendLine($"date: {startedLocal:yyyy-MM-ddTHH:mm:ss}");
        sb.AppendLine($"duration: {DurationMinutes(session, events)}m");

        var actionItems = notes?[NoteSection.ActionItems] ?? (IReadOnlyList<NoteItem>)Array.Empty<NoteItem>();
        if (actionItems.Count == 0)
        {
            sb.AppendLine("action_items: []");
        }
        else
        {
            sb.AppendLine("action_items:");
            foreach (var item in actionItems)
            {
                var (assignee, task) = SplitActionItem(item.Text);
                sb.AppendLine($"  - assignee: {Quote(assignee)}");
                sb.AppendLine($"    task: {Quote(task)}");
                sb.AppendLine("    status: open");
            }
        }

        var decisions = notes?[NoteSection.Decisions] ?? (IReadOnlyList<NoteItem>)Array.Empty<NoteItem>();
        if (decisions.Count == 0)
        {
            sb.AppendLine("decisions: []");
        }
        else
        {
            sb.AppendLine("decisions:");
            foreach (var item in decisions)
            {
                sb.AppendLine($"  - text: {Quote(item.Text)}");
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        var summaryItems = new List<string>();
        if (notes is not null)
        {
            summaryItems.AddRange(notes[NoteSection.Risks].Select(i => $"- Risk: {i.Text}"));
            summaryItems.AddRange(notes[NoteSection.Notes].Select(i => $"- {i.Text}"));
        }
        if (summaryItems.Count > 0)
        {
            summaryItems.ForEach(line => sb.AppendLine(line));
        }
        else
        {
            sb.AppendLine("_No summary was generated for this session._");
        }

        sb.AppendLine();
        sb.AppendLine("## Transcript");
        sb.AppendLine();
        foreach (var e in events)
        {
            sb.AppendLine($"[{e.Speaker.DisplayName} {Offset(session, e)}] {e.Text}");
        }

        return sb.ToString();
    }

    private static int DurationMinutes(SessionRecord session, IReadOnlyList<TranscriptEvent> events)
    {
        DateTimeOffset end = session.EndedAt
            ?? (events.Count > 0 ? events[^1].Timestamp : session.StartedAt);
        double minutes = (end - session.StartedAt).TotalMinutes;
        return Math.Max(0, (int)Math.Round(minutes));
    }

    /// <summary>Transcript line offset "m:ss" from session start; falls back to StartMs when timestamps degenerate.</summary>
    private static string Offset(SessionRecord session, TranscriptEvent e)
    {
        TimeSpan offset = e.Timestamp - session.StartedAt;
        if (offset < TimeSpan.Zero && e.StartMs is { } startMs)
        {
            offset = TimeSpan.FromMilliseconds(startMs);
        }
        if (offset < TimeSpan.Zero)
        {
            offset = TimeSpan.Zero;
        }
        return $"{(int)offset.TotalMinutes}:{offset.Seconds:00}";
    }

    /// <summary>"Owner — task" (em dash convention) splits into assignee + task; otherwise assignee is empty.</summary>
    internal static (string Assignee, string Task) SplitActionItem(string text)
    {
        int dash = text.IndexOf('—');
        if (dash > 0 && dash < text.Length - 1)
        {
            return (text[..dash].Trim(), text[(dash + 1)..].Trim());
        }
        return ("", text.Trim());
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\"";

    /// <summary>Expands "~" to the user profile. Shared by export, deletion, and sync-state checks.</summary>
    public static string ResolveFolder(string folder)
        => folder.StartsWith("~", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folder.TrimStart('~', '/', '\\'))
            : folder;

    /// <summary>All minutes files for a session in the resolved folder (UniquePath may create -2.md variants).</summary>
    public static string[] FindExportedFiles(string minutesFolder, string sessionId)
    {
        string folder = ResolveFolder(minutesFolder);
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, $"*-meeting-{sessionId}*.md")
            : Array.Empty<string>();
    }

    private static string UniquePath(string folder, string fileName)
    {
        string path = Path.Combine(folder, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(folder, $"{stem}-{i}.md");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
