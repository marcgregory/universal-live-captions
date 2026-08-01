using UniversalCaptions.Core.Captions;

namespace UniversalCaptions.App.Overlay;

/// <summary>
/// A single line of text the overlay renders.
/// </summary>
/// <param name="Text">The text to display: the translated text when a completed translation is
/// available, otherwise the source-language caption.</param>
/// <param name="Sequence">The caption line's sequence, used for ordering.</param>
/// <param name="IsTranslated">True when <paramref name="Text"/> is a completed translation.</param>
public sealed record CaptionDisplayLine(string Text, long Sequence, bool IsTranslated);

/// <summary>
/// The caption content the overlay renders: the active in-progress line plus the committed history
/// (newest first). This is the overlay's read model of <see cref="CaptionState"/> and implements the
/// resolved Q1 display policy: the active line is the verbatim latest partial, committed finals are
/// history, and a completed translation replaces the source text on a committed line (PRD FR-5/FR-14).
/// </summary>
public sealed record CaptionDisplayModel(CaptionDisplayLine? ActiveLine, IReadOnlyList<CaptionDisplayLine> History)
{
    /// <summary>True when there is no caption content to show.</summary>
    public bool IsEmpty => ActiveLine is null && History.Count == 0;
}

/// <summary>
/// Builds the overlay read model from caption state. Pure logic so it can be tested without WPF.
/// </summary>
public static class CaptionDisplayPolicy
{
    /// <summary>
    /// Converts a caption snapshot to the overlay model. History is ordered newest first for display.
    /// </summary>
    /// <param name="snapshot">The immutable caption snapshot consumed by the overlay.</param>
    /// <returns>The overlay read model.</returns>
    public static CaptionDisplayModel ToDisplayModel(CaptionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var history = new List<CaptionDisplayLine>(snapshot.History.Count);
        for (int i = snapshot.History.Count - 1; i >= 0; i--)
        {
            CaptionDisplayLine? line = ToDisplayLine(snapshot.History[i]);
            if (line is not null)
            {
                history.Add(line);
            }
        }

        return new CaptionDisplayModel(ToDisplayLine(snapshot.ActiveLine), history);
    }

    /// <summary>
    /// Converts one caption line to its overlay representation, or null for a missing line.
    /// </summary>
    /// <param name="line">The caption line, or null.</param>
    /// <returns>The overlay representation, or null.</returns>
    public static CaptionDisplayLine? ToDisplayLine(CaptionLine? line)
    {
        if (line is null)
        {
            return null;
        }

        bool translated = line.TranslationStatus == CaptionTranslationStatus.Completed
            && !string.IsNullOrWhiteSpace(line.TranslatedText);
        return new CaptionDisplayLine(translated ? line.TranslatedText! : line.Text, line.Sequence, translated);
    }
}
