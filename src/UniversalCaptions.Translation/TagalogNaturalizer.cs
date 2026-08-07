using System.Text;

namespace UniversalCaptions.Translation;

/// <summary>
/// A deterministic, rule-based post-processor that rewrites recurring Argos en→tl phrasings into
/// more conversational Tagalog while preserving meaning, names, numbers, and caption brevity.
///
/// The rewrite table is made of reusable phrase/word rules (word-boundary aware, case-insensitive,
/// case-preserving), not sentence templates: rules fire on the same recurring constructions
/// wherever they appear, so the layer generalizes across caption text instead of matching whole
/// lines. It never introduces new information and only changes meaning in the narrow sense of
/// choosing the natural conversational construction over the literal one (e.g. the reciprocal
/// "magkikita tayo ulit" for "we will see you", the idiomatic "maligayang pagdating" for
/// "malugod na tanggapin").
///
/// This is a pure, stateless transform: it owns no state, performs no I/O, and is safe to call
/// from any thread. It is intentionally not wired into the frozen production pipeline; it exists
/// as the deterministic layer to benchmark against the Gemini live-translate reference.
/// </summary>
public static class TagalogNaturalizer
{
    // Ordered most-specific-first: a longer phrase must be tried before any shorter rule that is a
    // substring of it (e.g. "dakilang gawa ang lahat" before "dakilang gawa"), otherwise the
    // shorter rule would consume the span first and the specific construction could not fire.
    private static readonly (string From, string To)[] Rules =
    {
        // "that is the end of the current practice session" → natural end-of-session phrasing
        ("wakas ng kasalukuyang sesyon ng pagsasanay", "katapusan ng ating sesyon sa pagsasanay"),

        // "great work everyone" (literal "great deed, all") → the natural praise construction
        ("dakilang gawa ang lahat", "magandang trabaho sa inyong lahat"),
        ("dakilang gawa", "magandang trabaho"),

        // "cordially welcome" → the idiomatic welcome phrase
        ("malugod na tanggapin", "maligayang pagdating"),

        // formal "kindly open" → the everyday polite imperative "pakibuksan"
        ("pakisuyong buksan", "pakibuksan"),

        // literal "we will see you" → the natural reciprocal "we'll see each other again"
        ("makikita ka namin", "magkikita tayo ulit"),

        // English greeting kept by Argos ("Hello and ...") → the natural Filipino greeting
        ("hello at", "kamusta at"),

        // slightly formal "as of now, we will..." → the plain conversational opening
        ("sa ngayon ay", "ngayon ay"),

        // "we will train" → the natural "we will practice"
        ("magsasanay tayo", "mag-eensayo tayo"),

        // "pambungad" (opening/foreword) is too literary for "introductions"
        ("pambungad", "pagpapakilala"),

        // STT/Argos spacing artifact on reduplication ("nag - uusap - usap") → the clean verb
        ("nag - uusap - usap", "nakikipag-usap-usap"),

        // STT misspellings carried into the translation
        ("conversional", "conversational"),
        ("tangalog", "tagalog"),
    };

    /// <summary>
    /// Rewrites <paramref name="text"/> by applying every rule in order. Unmatched text is left
    /// untouched; a null input is rejected (consistent with the rest of the translation layer).
    /// </summary>
    public static string Naturalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string result = text;
        foreach ((string from, string to) in Rules)
        {
            result = Apply(result, from, to);
        }

        return result;
    }

    private static string Apply(string text, string from, string to)
    {
        if (from.Length == 0 || text.Length < from.Length)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 16);
        int i = 0;
        while (i <= text.Length - from.Length)
        {
            int next = text.IndexOf(from, i, StringComparison.OrdinalIgnoreCase);
            if (next < 0)
            {
                break;
            }

            // The match must sit on word boundaries so phrases never fire mid-word
            // (e.g. "pambungad" inside "pagpambungad" or "tagalog" inside "Katangalog").
            bool boundaryOk =
                (next == 0 || !char.IsLetter(text[next - 1])) &&
                (next + from.Length == text.Length || !char.IsLetter(text[next + from.Length]));

            if (!boundaryOk)
            {
                sb.Append(text, i, next + 1 - i);
                i = next + 1;
                continue;
            }

            sb.Append(text, i, next - i);
            sb.Append(AdjustCase(text.Substring(next, from.Length), to));
            i = next + from.Length;
        }

        sb.Append(text, i, text.Length - i);
        return sb.ToString();
    }

    private static string AdjustCase(string source, string replacement)
    {
        bool hasLetter = false;
        bool allUpper = true;
        foreach (char c in source)
        {
            if (char.IsLetter(c))
            {
                hasLetter = true;
                if (!char.IsUpper(c))
                {
                    allUpper = false;
                    break;
                }
            }
        }

        if (hasLetter && allUpper)
        {
            return replacement.ToUpperInvariant();
        }

        if (source.Length > 0 && char.IsUpper(source[0]))
        {
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }

        return replacement;
    }
}
