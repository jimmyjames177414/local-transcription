using System.Text;

namespace LocalTranscriber.Shared;

public enum NoteSection
{
    Decisions,
    ActionItems,
    Risks,
    Notes
}

public sealed record NoteItem(string Text, DateTimeOffset AddedAt);

/// <summary>
/// The assistant-maintained meeting notes: plain markdown with four fixed sections. Written by
/// the app's notes service, displayed in the Meeting screen's notes panel, and consumed by the
/// minutes exporter. Lives in Shared so App and Storage can both use it without a cycle.
/// </summary>
public sealed class NotesDocument
{
    private static readonly (NoteSection Section, string Heading)[] Headings =
    {
        (NoteSection.Decisions, "Decisions"),
        (NoteSection.ActionItems, "Action items"),
        (NoteSection.Risks, "Risks"),
        (NoteSection.Notes, "Notes")
    };

    private readonly Dictionary<NoteSection, List<NoteItem>> _sections = new()
    {
        [NoteSection.Decisions] = new(),
        [NoteSection.ActionItems] = new(),
        [NoteSection.Risks] = new(),
        [NoteSection.Notes] = new()
    };

    public NotesDocument(string sessionId)
    {
        SessionId = sessionId;
    }

    public string SessionId { get; }

    public IReadOnlyList<NoteItem> this[NoteSection section] => _sections[section];

    public bool IsEmpty => _sections.Values.All(list => list.Count == 0);

    public void Add(NoteSection section, string text, DateTimeOffset? addedAt = null)
    {
        text = text.Trim();
        if (text.Length > 0)
        {
            _sections[section].Add(new NoteItem(text, addedAt ?? DateTimeOffset.Now));
        }
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Meeting notes — {SessionId}");
        foreach (var (section, heading) in Headings)
        {
            sb.AppendLine();
            sb.AppendLine($"## {heading}");
            foreach (var item in _sections[section])
            {
                sb.AppendLine($"- {item.Text}");
            }
        }

        return sb.ToString();
    }

    /// <summary>Tolerant round-trip: headings are matched case-insensitively; unknown lines are ignored.</summary>
    public static NotesDocument Parse(string markdown, string sessionId = "")
    {
        var doc = new NotesDocument(sessionId);
        NoteSection? current = null;

        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith("# ", StringComparison.Ordinal) && sessionId.Length == 0)
            {
                int dash = line.IndexOf('—');
                if (dash >= 0)
                {
                    doc = new NotesDocument(line[(dash + 1)..].Trim());
                    continue;
                }
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                string heading = line[3..].Trim();
                var match = Headings.FirstOrDefault(h => string.Equals(h.Heading, heading, StringComparison.OrdinalIgnoreCase));
                current = match.Heading is null ? null : match.Section;
                continue;
            }

            if (current is { } section && line.StartsWith("- ", StringComparison.Ordinal))
            {
                doc.Add(section, line[2..], DateTimeOffset.MinValue);
            }
        }

        return doc;
    }

    public static string HeadingFor(NoteSection section)
        => Headings.First(h => h.Section == section).Heading;

    /// <summary>Maps a tool-call section string ("decisions", "action_items"...) to a section.</summary>
    public static NoteSection? SectionFromKey(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "decisions" or "decision" => NoteSection.Decisions,
        "action_items" or "actionitems" or "action items" or "actions" => NoteSection.ActionItems,
        "risks" or "risk" => NoteSection.Risks,
        "notes" or "note" or "general" => NoteSection.Notes,
        _ => null
    };
}
