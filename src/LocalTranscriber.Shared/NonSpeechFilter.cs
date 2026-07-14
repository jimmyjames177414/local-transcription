using System.Text.RegularExpressions;

namespace LocalTranscriber.Shared;

/// <summary>
/// Strips non-speech sound annotations that whisper emits, e.g. "[Music]", "(engine revving)",
/// "*laughs*", or musical-note markers. Returns the remaining spoken text, or an empty string
/// when the segment was entirely a sound effect.
/// </summary>
public static class NonSpeechFilter
{
    private static readonly Regex AnnotationRegex =
        new(@"[\(\[\*][^\)\]\*]*[\)\]\*]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CollapseWhitespace =
        new(@"\s{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string StripAnnotations(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        string cleaned = AnnotationRegex.Replace(text, " ").Replace("♪", " ");
        return CollapseWhitespace.Replace(cleaned, " ").Trim();
    }
}
