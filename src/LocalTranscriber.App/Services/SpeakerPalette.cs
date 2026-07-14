using System.Windows;
using System.Windows.Media;

namespace LocalTranscriber.App.Services;

/// <summary>
/// Assigns each speaker a sticky color for the current session: "Me" (microphone track) is
/// always Speaker.Me; other speakers cycle through Brush.Speaker.1-6 in order of first
/// appearance. Reset at session start so colors are per-session stable.
/// </summary>
public static class SpeakerPalette
{
    private const int CycleSize = 6;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Assigned = new();

    private static readonly Brush Fallback = new SolidColorBrush(Color.FromRgb(0xC9, 0xCE, 0xD6));

    public static void Reset()
    {
        lock (Gate)
        {
            Assigned.Clear();
        }
    }

    public static Brush GetBrush(string speakerId, bool isMe)
    {
        if (isMe)
        {
            return Lookup("Brush.Speaker.Me");
        }

        int index;
        lock (Gate)
        {
            if (!Assigned.TryGetValue(speakerId, out index))
            {
                index = (Assigned.Count % CycleSize) + 1;
                Assigned[speakerId] = index;
            }
        }

        return Lookup($"Brush.Speaker.{index}");
    }

    /// <summary>
    /// Deterministic color for a speaker NAME, independent of session state — used by the
    /// Sessions list where the per-session first-appearance palette doesn't apply. FNV-1a
    /// because string.GetHashCode is randomized per process.
    /// </summary>
    public static Brush GetBrushForName(string name)
    {
        if (name is "Me" || name.EndsWith("(Me)", StringComparison.Ordinal))
        {
            return Lookup("Brush.Speaker.Me");
        }

        uint hash = 2166136261;
        foreach (char c in name)
        {
            hash = (hash ^ c) * 16777619;
        }

        return Lookup($"Brush.Speaker.{(hash % CycleSize) + 1}");
    }

    private static Brush Lookup(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Fallback;
}
