using LocalTranscriber.Shared;

namespace LocalTranscriber.Storage;

public static class TranscriptFormatting
{
    /// <summary>
    /// Confidence below which a known speaker is rendered as "possibly Name".
    /// Matches the default speakerMatchThreshold in config.
    /// </summary>
    public const double DefaultUncertainBelow = 0.72;

    public static string FormatSpeakerName(SpeakerLabel speaker, double uncertainBelow = DefaultUncertainBelow)
    {
        if (speaker.IsKnown && speaker.Confidence is double c && c < uncertainBelow)
        {
            return $"possibly {speaker.DisplayName}";
        }

        return speaker.DisplayName;
    }

    public static string FormatLine(TranscriptEvent e, double uncertainBelow = DefaultUncertainBelow)
    {
        string time = e.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        return $"[{time}] {FormatSpeakerName(e.Speaker, uncertainBelow)}: {e.Text}";
    }
}
