using System.Text.RegularExpressions;

namespace UniversalCaptions.App.Overlay;

/// <summary>
/// Renders the live in-progress (partial) caption line with a stable/unstable word split
/// (v0.5.38 candidate): words that were already recognized identically in the immediately-previous
/// partial of the same utterance are "stable" and painted normal; the newly-appeared tail is
/// "unstable" and painted a subtle green to signal "Whisper is still working on these words". When a
/// FINAL arrives the line freezes into history as a plain white block and the green disappears.
/// Pure string logic so it can be unit-tested without WPF.
/// </summary>
public static class CaptionPartialStability
{
    /// <summary>
    /// Returns the number of leading words of <paramref name="current"/> that also appear in the
    /// same position in <paramref name="previous"/> (case-insensitive, trailing punctuation
    /// ignored). This is the "stable" head that the immediately-previous partial already confirmed;
    /// everything after it is the unstable tail. Returns 0 when there is no previous partial (the
    /// first partial of an utterance is entirely unconfirmed).
    /// </summary>
    /// <param name="previous">The immediately-previous partial text, or null for the first partial of an utterance.</param>
    /// <param name="current">The current partial text.</param>
    /// <returns>The count of stable leading words in <paramref name="current"/>.</returns>
    public static int StableWordCount(string? previous, string current)
    {
        if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(current))
        {
            return 0;
        }

        string[] prev = SplitWords(previous);
        string[] curr = SplitWords(current);
        int max = Math.Min(prev.Length, curr.Length);
        int stable = 0;
        while (stable < max && WordEquals(prev[stable], curr[stable]))
        {
            stable++;
        }

        return stable;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into a stable prefix and an unstable suffix at the given
    /// word boundary, preserving the original spacing exactly (the split lands immediately after
    /// the <paramref name="stableWordCount"/>-th word). A count of 0 returns the whole text as
    /// unstable; a count >= the word count returns the whole text as stable.
    /// </summary>
    /// <param name="text">The text to split.</param>
    /// <param name="stableWordCount">How many leading words are stable.</param>
    /// <returns>The stable prefix and the unstable suffix.</returns>
    public static (string Stable, string Unstable) SplitAtWord(string text, int stableWordCount)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (string.Empty, string.Empty);
        }

        if (stableWordCount <= 0)
        {
            return (string.Empty, text);
        }

        MatchCollection words = Regex.Matches(text, @"\S+");
        if (words.Count == 0)
        {
            return (string.Empty, text);
        }

        if (stableWordCount >= words.Count)
        {
            return (text, string.Empty);
        }

        int end = words[stableWordCount - 1].Index + words[stableWordCount - 1].Length;
        return (text[..end], text[end..]);
    }

    private static string[] SplitWords(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool WordEquals(string a, string b) =>
        string.Equals(TrimTrailingPunctuation(a), TrimTrailingPunctuation(b), StringComparison.OrdinalIgnoreCase);

    private static string TrimTrailingPunctuation(string word) =>
        word.TrimEnd('.', ',', '!', '?', ';', ':');
}
