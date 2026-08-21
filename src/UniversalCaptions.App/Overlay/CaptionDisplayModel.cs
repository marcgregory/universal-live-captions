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
/// (oldest first, chronological). This is the overlay's read model of <see cref="CaptionState"/> and
/// implements the resolved Q1 display policy: the active line is the latest partial, live-translated
/// into the target language as soon as its translation completes; committed finals are history; and a
/// completed translation replaces the source text on a line (PRD FR-5/FR-14). While a partial is still
/// being translated the overlay renders no source-language text for it, so captions never flash
/// between languages.
/// </summary>
/// <param name="ActiveLine">The in-progress caption line, or null.</param>
/// <param name="History">The committed caption lines, oldest first.</param>
/// <param name="TranslationEnabled">True when translation is enabled and the overlay shows the target badge.</param>
/// <param name="TargetLanguage">The lowercase ISO 639-1 target language, or null.</param>
public sealed record CaptionDisplayModel(
    CaptionDisplayLine? ActiveLine,
    IReadOnlyList<CaptionDisplayLine> History,
    bool TranslationEnabled = false,
    string? TargetLanguage = null)
{
    /// <summary>True when there is no caption content to show.</summary>
    public bool IsEmpty => ActiveLine is null && History.Count == 0;

    /// <summary>The uppercase target-language code for the overlay badge, or null when translation is off.</summary>
    public string? LanguageBadge =>
        TranslationEnabled && !string.IsNullOrWhiteSpace(TargetLanguage)
            ? TargetLanguage!.Trim().ToUpperInvariant()
            : null;
}

/// <summary>
/// Builds the overlay read model from caption state. Pure logic so it can be tested without WPF.
/// </summary>
public static class CaptionDisplayPolicy
{
    /// <summary>
    /// Maximum total number of visible characters across all finalized history lines shown in the
    /// overlay. When the budget is exceeded the <em>oldest</em> finalized caption is removed until the
    /// total is within budget. The newest finalized caption is always kept regardless of its length,
    /// and the live interim (active) line is never counted against the budget.
    /// </summary>
    public const int MaxVisibleCharacters = 200;

    /// <summary>
    /// Converts a caption snapshot to the overlay model. History preserves the snapshot's
    /// chronological (oldest-first) order, so the overlay renders oldest captions at the top and the
    /// newest caption at the bottom.
    /// </summary>
    /// <param name="snapshot">The immutable caption snapshot consumed by the overlay.</param>
    /// <returns>The overlay read model.</returns>
    public static CaptionDisplayModel ToDisplayModel(CaptionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // The common translation state/UX layer is the source of truth: TranslationEnabled +
        // TargetLanguage reflect the user's Translate toggle, so the badge and the
        // source-vs-translation display key off it — never off line origins. Origins only decide
        // WHICH lines are the display in a live-translation session.
        bool translationOn = snapshot.TranslationEnabled && !string.IsNullOrWhiteSpace(snapshot.TargetLanguage);

        // Build the full display list. The overlay is a fixed-height ScrollViewer that
        // auto-scrolls to the bottom, so all history is passed through — the visual window
        // shows only the last 2-3 lines, exactly like Chrome's Live Caption.
        //
        // Live-translation session (Gemini): the live engine owns display, so the overlay is
        // target-language-only — source transcription finals are hidden once the session produces any
        // translation content; their Gemini translation is the display. The mode is DRIVEN BY THE
        // ACTUAL SESSION STATE (CaptionSnapshot.IsLiveTranslationSession, set by the pipeline when a
        // live engine is active), never inferred from history content: an engine swap must not
        // flash the previous session's untranslated source just because stale translation
        // content was cleared. When translation is toggled OFF the translation-origin events are
        // gated and the scrub clears old content, so the overlay returns to the source captions.
        bool liveTranslationSession = translationOn && snapshot.IsLiveTranslationSession;

        var allHistory = new List<CaptionDisplayLine>(snapshot.History.Count);
        foreach (CaptionLine caption in snapshot.History)
        {
            // Tagalog-only display: a committed final whose translation is still pending is not shown,
            // so its English source never flashes. The previous committed (Tagalog) caption remains
            // visible until the new final's translation completes; CaptionService then raises
            // CaptionLineUpdated and this entry re-renders as the new translated Tagalog text.
            if (translationOn &&
                caption.TranslationStatus == CaptionTranslationStatus.Pending)
            {
                continue;
            }

            // Live-translation session: hide the raw source-STT finals — the overlay is Tagalog-only.
            if (liveTranslationSession && caption.Origin == LineOrigin.SourceStt)
            {
                continue;
            }

            CaptionDisplayLine? line = ToDisplayLine(caption);
            if (line is not null)
            {
                allHistory.Add(line);
            }
        }

        IReadOnlyList<CaptionDisplayLine> history = allHistory;

        // The active line. In a live-translation session (Gemini) the overlay is target-language-only:
        // the translation-origin line is the display, and while no translation is in progress the
        // source line must NOT flash in the source language — so it is never used as a fallback.
        // When translation is off, the source line is the display.
        CaptionLine? active = liveTranslationSession ? snapshot.ActiveTranslationLine : snapshot.ActiveLine;

        CaptionDisplayLine? display = null;
        if (active is not null)
        {
            bool shouldHide = translationOn &&
                (active.TranslationStatus == CaptionTranslationStatus.NotRequested ||
                 active.TranslationStatus == CaptionTranslationStatus.Pending) &&
                 string.IsNullOrWhiteSpace(active.TranslatedText);

            if (!shouldHide)
            {
                display = ToDisplayLine(active);
            }
        }

        // Strip leading overlap: if the active line's source text begins with the same
        // words as the last committed history entry, those words are already visible in
        // history and should not be repeated at the bottom. We compare against the
        // SOURCE text of the history entry (snapshot.History, not the display list which
        // may be translated), and only strip from an untranslated active line so we never
        // corrupt a Tagalog translation with an English word-removal pass.
        if (display is not null
            && active is not null
            && !display.IsTranslated
            && snapshot.History.Count > 0)
        {
            string lastSourceText = snapshot.History[^1].Text;
            string strippedText = StripLeadingOverlap(lastSourceText, active.Text);
            if (!string.IsNullOrWhiteSpace(strippedText) && strippedText.Length < active.Text.Length)
            {
                display = new CaptionDisplayLine(strippedText, display.Sequence, false);
            }
            // If strippedText is empty (the entire partial was a repeat) keep the
            // original active so the user always sees something at the bottom.
        }

        // The badge comes straight from the common translation state — TranslationEnabled +
        // TargetLanguage reflect the user's toggle for every provider, so no origin inference is
        // needed (and toggling off clears the badge immediately).
        string? badgeTarget = translationOn ? snapshot.TargetLanguage : null;

        return new CaptionDisplayModel(
            display,
            history,
            translationOn,
            badgeTarget);
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

        // A translation-origin line's Text IS the target-language translation (the live Gemini engine
        // emits final text, not source + translated fields). Its TranslationStatus stays NotRequested
        // because the caption service never re-translates it, so flag it as translated by origin so
        // the overlay treats it like a completed translation (and never runs the English overlap-strip
        // over Tagalog text).
        bool translated = (line.Origin == LineOrigin.Translation && !string.IsNullOrWhiteSpace(line.Text))
            || (line.TranslationStatus == CaptionTranslationStatus.Completed
                && !string.IsNullOrWhiteSpace(line.TranslatedText));
        return new CaptionDisplayLine(translated ? line.TranslatedText ?? line.Text : line.Text, line.Sequence, translated);
    }

    /// <summary>
    /// Strips any word-level suffix of <paramref name="prev"/> that appears as a leading prefix
    /// of <paramref name="next"/>, returning the remaining tail of <paramref name="next"/>.
    /// Words are compared case-insensitively and with trailing punctuation ignored so minor
    /// transcription differences between epochs don't defeat the deduplication.
    /// If no overlap is found, <paramref name="next"/> is returned unchanged.
    /// </summary>
    private static string StripLeadingOverlap(string prev, string next)
    {
        if (string.IsNullOrWhiteSpace(prev) || string.IsNullOrWhiteSpace(next))
        {
            return next;
        }

        string[] prevWords = prev.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] nextWords = next.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (prevWords.Length == 0 || nextWords.Length == 0)
        {
            return next;
        }

        int maxOverlap = Math.Min(prevWords.Length, nextWords.Length);

        for (int overlap = maxOverlap; overlap >= 1; overlap--)
        {
            bool match = true;
            for (int i = 0; i < overlap; i++)
            {
                string pw = prevWords[prevWords.Length - overlap + i].TrimEnd('.', ',', '!', '?', ';', ':');
                string nw = nextWords[i].TrimEnd('.', ',', '!', '?', ';', ':');
                if (!string.Equals(pw, nw, StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                string[] remaining = nextWords[overlap..];
                return remaining.Length == 0 ? string.Empty : string.Join(' ', remaining);
            }
        }

        return next;
    }
}
