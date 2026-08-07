namespace UniversalCaptions.Core.Captions;

/// <summary>
/// The caption state consumed by the overlay: an active STT line, an active translation line, a
/// bounded unified history of committed lines, and the session/translation configuration. Mutated
/// by the caption service.
/// </summary>
/// <remarks>
/// <para>
/// Two active-line slots coexist — one per <see cref="LineOrigin"/> — so a Whisper partial and a
/// Gemini partial arriving at the same moment do not overwrite one another. Each slot accepts
/// updates only when the replacement line matches the slot's origin AND the prior line's instance
/// identity, so a stale translation cannot clobber a newer partial.
/// </para>
/// <para>
/// Committed finals from both origins are inserted into <see cref="History"/> in
/// <see cref="CaptionLine.Sequence"/> order and the oldest lines are dropped once
/// <see cref="HistoryCapacity"/> is exceeded.
/// </para>
/// <para>
/// Instances are not thread-safe; the caption service serializes all access.
/// </para>
/// </remarks>
public sealed class CaptionState
{
    private readonly List<CaptionLine> _history = [];
    private readonly int _historyCapacity;

    /// <summary>
    /// Creates a caption state that retains at most <paramref name="historyCapacity"/> committed lines.
    /// </summary>
    /// <param name="historyCapacity">The maximum number of committed lines retained. Zero retains none.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="historyCapacity"/> is negative.</exception>
    public CaptionState(int historyCapacity)
    {
        if (historyCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(historyCapacity), historyCapacity, "HistoryCapacity must be zero or greater.");
        }

        _historyCapacity = historyCapacity;
    }

    /// <summary>
    /// The in-progress STT line fed by speech partials, or null when none is active. Backwards-
    /// compatible alias for the active line in pre-translation-origin code.
    /// </summary>
    public CaptionLine? ActiveLine => _sourceActiveLine;

    /// <summary>The in-progress translation line fed by the live-translation engine, or null when none is active.</summary>
    public CaptionLine? ActiveTranslationLine => _translationActiveLine;

    /// <summary>
    /// Returns the active line for the requested origin. Returns <c>null</c> when no line of that
    /// origin is active. Use this accessor when a consumer needs to read either slot.
    /// </summary>
    public CaptionLine? ActiveLineFor(LineOrigin origin) => origin switch
    {
        LineOrigin.SourceStt => _sourceActiveLine,
        LineOrigin.Translation => _translationActiveLine,
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown line origin."),
    };

    /// <summary>
    /// A snapshot of the committed lines in sequence order (oldest first). The caption service raises
    /// state events outside its lock, so consumers read this snapshot rather than a live view that a
    /// concurrent mutation could corrupt mid-enumeration. Both origins are present; each line's
    /// <see cref="CaptionLine.Origin"/> identifies which pipeline produced it.
    /// </summary>
    public IReadOnlyList<CaptionLine> History => _history.ToArray();

    /// <summary>The maximum number of committed lines retained.</summary>
    public int HistoryCapacity => _historyCapacity;

    /// <summary>The number of committed lines currently retained.</summary>
    public int HistoryCount => _history.Count;

    /// <summary>True when translation is enabled for newly committed lines.</summary>
    public bool TranslationEnabled { get; private set; }

    /// <summary>The language newly committed lines are translated into, when translation is enabled.</summary>
    public string? TargetLanguage { get; private set; }

    /// <summary>True from <see cref="BeginSession"/> until <see cref="EndSession"/> or <see cref="Reset"/>.</summary>
    public bool IsSessionActive { get; private set; }

    /// <summary>Starts a session. Idempotent.</summary>
    public void BeginSession() => IsSessionActive = true;

    /// <summary>Ends the session and discards both active lines. The committed history is retained. Idempotent.</summary>
    public void EndSession()
    {
        IsSessionActive = false;
        _sourceActiveLine = null;
        _translationActiveLine = null;
    }

    /// <summary>
    /// Replaces the STT active line with <paramref name="line"/>. Used when a new partial transcript
    /// arrives. Requires <paramref name="line"/>.Origin == <see cref="LineOrigin.SourceStt"/>.
    /// </summary>
    /// <param name="line">The active line. Must be in the <see cref="CaptionLineState.Active"/> state.</param>
    /// <exception cref="ArgumentException"><paramref name="line"/> is not in the <see cref="CaptionLineState.Active"/> state or has the wrong origin.</exception>
    public void UpdateActiveLine(CaptionLine line)
    {
        RequireOrigin(line, LineOrigin.SourceStt, nameof(line));
        UpdateActiveLineForOrigin(line, isStt: true);
    }

    /// <summary>
    /// Replaces the translation active line with <paramref name="line"/>. Used when the live
    /// translation engine emits a partial. Requires <paramref name="line"/>.Origin ==
    /// <see cref="LineOrigin.Translation"/>.
    /// </summary>
    /// <param name="line">The active line. Must be in the <see cref="CaptionLineState.Active"/> state.</param>
    /// <exception cref="ArgumentException"><paramref name="line"/> is not in the <see cref="CaptionLineState.Active"/> state or has the wrong origin.</exception>
    public void UpdateTranslationActiveLine(CaptionLine line)
    {
        RequireOrigin(line, LineOrigin.Translation, nameof(line));
        UpdateActiveLineForOrigin(line, isStt: false);
    }

    private void UpdateActiveLineForOrigin(CaptionLine line, bool isStt)
    {
        RequireState(line, CaptionLineState.Active, nameof(line));

        if (isStt)
        {
            // Carry over any in-progress translation so the UI doesn't hide the active line
            if (_sourceActiveLine != null
                && _sourceActiveLine.Sequence == line.Sequence
                && !string.IsNullOrWhiteSpace(_sourceActiveLine.TranslatedText))
            {
                line = new CaptionLine(
                    line.Text,
                    line.SourceLanguage,
                    line.Sequence,
                    line.CapturedAtUtc,
                    line.State,
                    line.CommittedAtUtc,
                    _sourceActiveLine.TargetLanguage,
                    _sourceActiveLine.TranslatedText,
                    line.TranslationStatus,
                    line.TranslationErrorMessage,
                    _sourceActiveLine.TranslationStartedAtUtc,
                    _sourceActiveLine.TranslationCompletedAtUtc,
                    line.Origin);
            }

            _sourceActiveLine = line;
        }
        else
        {
            _translationActiveLine = line;
        }
    }

    /// <summary>Discards both active lines. Used when the session ends or resets.</summary>
    public void ClearActiveLine()
    {
        _sourceActiveLine = null;
        _translationActiveLine = null;
    }

    /// <summary>Discards only the STT active line.</summary>
    public void ClearSourceActiveLine() => _sourceActiveLine = null;

    /// <summary>Discards only the translation active line.</summary>
    public void ClearTranslationActiveLine() => _translationActiveLine = null;

    /// <summary>
    /// Commits a final line into <see cref="History"/>, keeping it sorted by
    /// <see cref="CaptionLine.Sequence"/>. A line with the same sequence already in history is replaced
    /// (idempotent re-delivery). The oldest lines are dropped when the history exceeds
    /// <see cref="HistoryCapacity"/>. The active line matching <paramref name="line"/>.Origin is
    /// cleared if it is no longer newer than the committed line.
    /// </summary>
    /// <param name="line">The committed line. Must be in the <see cref="CaptionLineState.Final"/> state.</param>
    /// <exception cref="ArgumentException"><paramref name="line"/> is not in the <see cref="CaptionLineState.Final"/> state.</exception>
    public void AddFinalLine(CaptionLine line)
    {
        RequireState(line, CaptionLineState.Final, nameof(line));

        int existingIndex = FindIndex(line.Sequence);
        if (existingIndex >= 0)
        {
            _history[existingIndex] = line;
            return;
        }

        int insertIndex = FindInsertIndex(line.Sequence);
        _history.Insert(insertIndex, line);
        Trim();

        // Clear the matching active slot when the committed line is at least as new as the active
        // line for the same origin; otherwise the active line is a newer utterance the speaker
        // started while the previous one was still being finalised and must stay on screen.
        if (line.Origin == LineOrigin.SourceStt)
        {
            if (_sourceActiveLine is null || line.Sequence >= _sourceActiveLine.Sequence)
            {
                _sourceActiveLine = null;
            }
        }
        else
        {
            if (_translationActiveLine is null || line.Sequence >= _translationActiveLine.Sequence)
            {
                _translationActiveLine = null;
            }
        }
    }

    /// <summary>
    /// Replaces the committed line that is exactly <paramref name="original"/> with
    /// <paramref name="updated"/> — used to apply a translation result or failure. The match is on
    /// instance identity as well as sequence and origin, so a stale translation can never overwrite
    /// a newer line that was re-committed under the same sequence or that belongs to the other
    /// lineage. Returns false when no matching line exists (for example, the history was cleared or
    /// the line was re-committed in the meantime), in which case nothing is changed.
    /// </summary>
    /// <param name="original">The exact committed line the translation was started for.</param>
    /// <param name="updated">The updated line. Must be in the <see cref="CaptionLineState.Final"/> state.</param>
    /// <returns>True when the matching line was replaced.</returns>
    /// <exception cref="ArgumentException">Either line is not in the <see cref="CaptionLineState.Final"/> state.</exception>
    public bool ReplaceFinalLine(CaptionLine original, CaptionLine updated)
    {
        RequireState(original, CaptionLineState.Final, nameof(original));
        RequireState(updated, CaptionLineState.Final, nameof(updated));

        int index = FindIndex(original.Sequence);
        if (index < 0 || !ReferenceEquals(_history[index], original) || _history[index].Origin != original.Origin)
        {
            return false;
        }

        _history[index] = updated;
        return true;
    }

    /// <summary>
    /// Replaces the active line of the matching origin that is exactly <paramref name="original"/>
    /// with <paramref name="updated"/> — used to apply a translation result or failure to the
    /// in-progress line. The match is on instance identity, so a stale translation that was started
    /// for an older partial can never overwrite a newer partial that replaced it in the meantime.
    /// Returns false when no matching active line exists (for example, a newer partial arrived, the
    /// line was committed, or the origin does not match), in which case nothing is changed.
    /// </summary>
    /// <param name="original">The exact active line the translation was started for.</param>
    /// <param name="updated">The updated line. Must be in the <see cref="CaptionLineState.Active"/> state.</param>
    /// <returns>True when the matching active line was replaced.</returns>
    /// <exception cref="ArgumentException">Either line is not in the <see cref="CaptionLineState.Active"/> state.</exception>
    public bool ReplaceActiveLine(CaptionLine original, CaptionLine updated)
    {
        RequireState(original, CaptionLineState.Active, nameof(original));
        RequireState(updated, CaptionLineState.Active, nameof(updated));

        if (original.Origin == LineOrigin.SourceStt)
        {
            if (!ReferenceEquals(_sourceActiveLine, original))
            {
                return false;
            }

            _sourceActiveLine = updated;
            return true;
        }

        if (!ReferenceEquals(_translationActiveLine, original))
        {
            return false;
        }

        _translationActiveLine = updated;
        return true;
    }

    /// <summary>
    /// Enables or disables translation. When enabled, <paramref name="targetLanguage"/> becomes the
    /// target for newly committed lines; when disabled, the target is cleared.
    /// </summary>
    /// <param name="enabled">Whether translation is enabled.</param>
    /// <param name="targetLanguage">The ISO 639-1 target language, required when <paramref name="enabled"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="enabled"/> is true and <paramref name="targetLanguage"/> is null or empty.</param>
    public void SetTranslation(bool enabled, string? targetLanguage)
    {
        if (enabled && string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new ArgumentException("A target language is required when translation is enabled.", nameof(targetLanguage));
        }

        TranslationEnabled = enabled;
        TargetLanguage = enabled ? targetLanguage!.Trim().ToLowerInvariant() : null;
    }

    /// <summary>Clears both active lines and the history, disables translation, and ends the session.</summary>
    public void Reset()
    {
        _sourceActiveLine = null;
        _translationActiveLine = null;
        _history.Clear();
        TranslationEnabled = false;
        TargetLanguage = null;
        IsSessionActive = false;
    }

    private CaptionLine? _sourceActiveLine;
    private CaptionLine? _translationActiveLine;

    private int FindIndex(long sequence)
    {
        for (int i = 0; i < _history.Count; i++)
        {
            if (_history[i].Sequence == sequence)
            {
                return i;
            }
        }

        return -1;
    }

    private int FindInsertIndex(long sequence)
    {
        int low = 0;
        int high = _history.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (_history[mid].Sequence < sequence)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private void Trim()
    {
        while (_history.Count > _historyCapacity)
        {
            _history.RemoveAt(0);
        }
    }

    private static void RequireState(CaptionLine line, CaptionLineState expected, string paramName)
    {
        if (line.State != expected)
        {
            throw new ArgumentException($"A caption line in the {expected} state is required.", paramName);
        }
    }

    private static void RequireOrigin(CaptionLine line, LineOrigin expected, string paramName)
    {
        if (line.Origin != expected)
        {
            throw new ArgumentException(
                $"A caption line with origin {expected} is required (got {line.Origin}).",
                paramName);
        }
    }
}
