namespace UniversalCaptions.Core.Captions;

/// <summary>
/// The caption state consumed by the overlay: the active in-progress line, a bounded history of
/// committed lines, and the session/translation configuration. Mutated by the caption service.
/// </summary>
/// <remarks>
/// <para>
/// There is exactly one <see cref="ActiveLine"/> fed by partial transcripts; each new partial
/// replaces it. Committed finals are inserted into <see cref="History"/> in <see cref="CaptionLine.Sequence"/>
/// order and the oldest lines are dropped once <see cref="HistoryCapacity"/> is exceeded.
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

    /// <summary>The in-progress line fed by partial transcripts, or null when none is active.</summary>
    public CaptionLine? ActiveLine { get; private set; }

    /// <summary>
    /// A snapshot of the committed lines in sequence order (oldest first). The caption service raises
    /// state events outside its lock, so consumers read this snapshot rather than a live view that a
    /// concurrent mutation could corrupt mid-enumeration.
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

    /// <summary>Ends the session and discards the active line. The committed history is retained. Idempotent.</summary>
    public void EndSession()
    {
        IsSessionActive = false;
        ActiveLine = null;
    }

    /// <summary>
    /// Replaces the active line with <paramref name="line"/>. Used when a new partial transcript arrives.
    /// </summary>
    /// <param name="line">The active line. Must be in the <see cref="CaptionLineState.Active"/> state.</param>
    /// <exception cref="ArgumentException"><paramref name="line"/> is not in the <see cref="CaptionLineState.Active"/> state.</exception>
    public void UpdateActiveLine(CaptionLine line)
    {
        RequireState(line, CaptionLineState.Active, nameof(line));
        
        // Carry over any in-progress translation so the UI doesn't hide the active line
        if (ActiveLine != null && ActiveLine.Sequence == line.Sequence && !string.IsNullOrWhiteSpace(ActiveLine.TranslatedText))
        {
            line = new CaptionLine(
                line.Text,
                line.SourceLanguage,
                line.Sequence,
                line.CapturedAtUtc,
                line.State,
                line.CommittedAtUtc,
                ActiveLine.TargetLanguage,
                ActiveLine.TranslatedText,
                line.TranslationStatus,
                line.TranslationErrorMessage,
                ActiveLine.TranslationStartedAtUtc,
                ActiveLine.TranslationCompletedAtUtc);
        }
        
        ActiveLine = line;
    }

    /// <summary>Discards the active line. Used when its utterance is committed.</summary>
    public void ClearActiveLine() => ActiveLine = null;

    /// <summary>
    /// Commits a final line into <see cref="History"/>, keeping it sorted by <see cref="CaptionLine.Sequence"/>.
    /// A line with the same sequence already in history is replaced (idempotent re-delivery). The oldest
    /// lines are dropped when the history exceeds <see cref="HistoryCapacity"/>.
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
    }

    /// <summary>
    /// Replaces the committed line that is exactly <paramref name="original"/> with
    /// <paramref name="updated"/> — used to apply a translation result or failure. The match is on
    /// instance identity as well as sequence, so a stale translation can never overwrite a newer line
    /// that was re-committed under the same sequence. Returns false when no matching line exists (for
    /// example, the history was cleared or the line was re-committed in the meantime), in which case
    /// nothing is changed.
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
        if (index < 0 || !ReferenceEquals(_history[index], original))
        {
            return false;
        }

        _history[index] = updated;
        return true;
    }

    /// <summary>
    /// Replaces the active line that is exactly <paramref name="original"/> with
    /// <paramref name="updated"/> — used to apply a translation result or failure to the in-progress
    /// line. The match is on instance identity, so a stale translation that was started for an older
    /// partial can never overwrite a newer partial that replaced it in the meantime. Returns false
    /// when the active line is no longer <paramref name="original"/> (for example, a newer partial
    /// arrived or the line was committed), in which case nothing is changed.
    /// </summary>
    /// <param name="original">The exact active line the translation was started for.</param>
    /// <param name="updated">The updated line. Must be in the <see cref="CaptionLineState.Active"/> state.</param>
    /// <returns>True when the active line was replaced.</returns>
    /// <exception cref="ArgumentException">Either line is not in the <see cref="CaptionLineState.Active"/> state.</exception>
    public bool ReplaceActiveLine(CaptionLine original, CaptionLine updated)
    {
        RequireState(original, CaptionLineState.Active, nameof(original));
        RequireState(updated, CaptionLineState.Active, nameof(updated));

        if (!ReferenceEquals(ActiveLine, original))
        {
            return false;
        }

        ActiveLine = updated;
        return true;
    }

    /// <summary>
    /// Enables or disables translation. When enabled, <paramref name="targetLanguage"/> becomes the
    /// target for newly committed lines; when disabled, the target is cleared.
    /// </summary>
    /// <param name="enabled">Whether translation is enabled.</param>
    /// <param name="targetLanguage">The ISO 639-1 target language, required when <paramref name="enabled"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="enabled"/> is true and <paramref name="targetLanguage"/> is null or empty.</exception>
    public void SetTranslation(bool enabled, string? targetLanguage)
    {
        if (enabled && string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new ArgumentException("A target language is required when translation is enabled.", nameof(targetLanguage));
        }

        TranslationEnabled = enabled;
        TargetLanguage = enabled ? targetLanguage!.Trim().ToLowerInvariant() : null;
    }

    /// <summary>Clears the active line and history, disables translation, and ends the session.</summary>
    public void Reset()
    {
        ActiveLine = null;
        _history.Clear();
        TranslationEnabled = false;
        TargetLanguage = null;
        IsSessionActive = false;
    }

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
}
