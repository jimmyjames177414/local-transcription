namespace LocalTranscriber.Engine;

/// <summary>
/// Removes the duplicated words that appear at window boundaries because consecutive audio
/// windows overlap (OverlapMs). The overlap tail of one window is re-transcribed as the head
/// of the next, so a new segment often repeats the end of the previous one, e.g.
///   "...I was just trying to mess"  +  "just trying to mess around to seek..."
/// <see cref="TrimOverlap"/> finds the longest word-run that is both a suffix of
/// <paramref name="previous"/> and a prefix of <paramref name="next"/> and strips it from the
/// front of <paramref name="next"/>. Matching ignores case and surrounding punctuation.
/// </summary>
internal static class TranscriptStitcher
{
    // The overlap is a fraction of a second, so only a handful of words can repeat. Capping the
    // search avoids trimming a genuine repeated phrase that sits far from the boundary.
    private const int MaxOverlapWords = 12;

    public static string TrimOverlap(string previous, string next)
    {
        if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(next))
        {
            return next;
        }

        var prev = Tokenize(previous);
        var cur = Tokenize(next);
        int max = Math.Min(Math.Min(prev.Count, cur.Count), MaxOverlapWords);

        for (int k = max; k >= 1; k--)
        {
            if (!WordsEqual(prev, prev.Count - k, cur, 0, k))
            {
                continue;
            }

            // A single-word match is ambiguous: a short word ("this", "week", "then") can also
            // legitimately begin the next sentence, and trimming it would drop real content. Only
            // trim a lone word when it is long enough (6+ chars) that a coincidental sentence-start
            // collision is implausible; genuine multi-word echoes (k >= 2) are always trimmed.
            if (k == 1 && cur[0].Normalized.Length < 6)
            {
                continue;
            }

            // Drop the first k words from the original string; keep the remainder verbatim.
            return next[cur[k - 1].EndIndex..].TrimStart();
        }

        return next;
    }

    private static bool WordsEqual(List<Token> a, int aStart, List<Token> b, int bStart, int count)
    {
        for (int i = 0; i < count; i++)
        {
            string an = a[aStart + i].Normalized;
            if (an.Length == 0 || !string.Equals(an, b[bStart + i].Normalized, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private readonly record struct Token(string Normalized, int EndIndex);

    // Splits on whitespace; each token records where it ends in the source (exclusive) and a
    // normalized form (lowercased, outer punctuation stripped) used only for matching.
    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            tokens.Add(new Token(Normalize(text[start..i]), i));
        }
        return tokens;
    }

    private static string Normalize(string word)
    {
        int a = 0, b = word.Length;
        while (a < b && !char.IsLetterOrDigit(word[a])) a++;
        while (b > a && !char.IsLetterOrDigit(word[b - 1])) b--;
        return word[a..b].ToLowerInvariant();
    }
}
