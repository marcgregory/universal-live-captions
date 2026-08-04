using System.Text;

namespace UniversalCaptions.Speech;

/// <summary>
/// The result of one commit pass: the newly finalized text and the current in-progress text.
/// </summary>
public readonly record struct CommitterResult(string FinalText, string PartialText);

/// <summary>
/// Converts repeatedly decoded windows of segments into committed (final) and in-progress
/// (partial) transcript text using a stability-based strategy gated on meaningful segment
/// boundaries.
/// </summary>
/// <remarks>
/// <para>
/// Each decode pass contributes the concatenated text of its segments — the current hypothesis
/// for the audio window. Text is classified as:
/// </para>
/// <list type="bullet">
/// <item><term>partial</term><description>the actively changing tail that has not yet been repeated enough to trust;</description></item>
/// <item><term>stable</term><description>text that has appeared unchanged across <c>stabilityWindow</c> consecutive decode passes;</description></item>
/// <item><term>final</term><description>a stable prefix that ALSO ends at a meaningful segment boundary, emitted once as a committed caption.</description></item>
/// </list>
/// <para>
/// Stability is the longest common prefix of the last <c>stabilityWindow</c> window transcripts
/// (a revision anywhere in the shared region simply stops advancing the commit boundary). A stable
/// prefix is committed as FINAL only when it ends exactly at a <see cref="TranscriptSegment"/>
/// boundary — never when it ends inside a segment. If no meaningful boundary appears before
/// <c>boundaryWaitBudget</c> elapses after the prefix first became stable, the current word-backed
/// stable prefix is committed as a bounded fallback (so a caption is never held indefinitely). A
/// growing stable prefix does not reset the timer; only a replacement (unrelated text) starts a
/// new stability interval. Chunker state (pending stable prefix + its timer) survives an epoch
/// (window-advance) rollover, while the segment mapping is recomputed per update. Text that already
/// committed is never re-emitted: both a strict committed prefix and a real, whole-word tail overlap
/// of the newly decoded window with committed text are stripped (TD-006/007). Deterministic and
/// model-agnostic.
/// </para>
/// </remarks>
public sealed class StreamingTranscriptCommitter
{
    private readonly StringBuilder _committedText = new();
    private readonly Queue<string> _recentTexts = new();
    private readonly int _stabilityWindow;
    private readonly TimeSpan _boundaryWaitBudget;
    private readonly Func<DateTime> _utcNow;

    private DateTime _committedUntilUtc = DateTime.MinValue;
    private DateTime _lastWindowStartUtc = DateTime.MinValue;
    private int _currentEpochCommittedLength;

    private string _pendingStable = string.Empty;
    private DateTime _pendingStableSinceUtc = DateTime.MinValue;

    /// <summary>
    /// Creates a committer that finalizes text only after it has been observed unchanged across
    /// <paramref name="stabilityWindow"/> consecutive decode passes AND has reached a meaningful
    /// segment boundary (or the wait budget). Uses <see cref="DateTime.UtcNow"/> and a default
    /// boundary wait budget of two seconds.
    /// </summary>
    /// <param name="stabilityWindow">Consecutive identical decodes required before text becomes final. Must be at least 2.</param>
    public StreamingTranscriptCommitter(int stabilityWindow)
        : this(stabilityWindow, DefaultBoundaryWaitBudget(), () => DateTime.UtcNow)
    {
    }

    /// <summary>
    /// Creates a committer with an explicit boundary-wait budget and an injectable clock, so the
    /// provisional budget can be swept after E2E latency measurement (ADR-0007).
    /// </summary>
    /// <param name="stabilityWindow">Consecutive identical decodes required before text becomes final. Must be at least 2.</param>
    /// <param name="boundaryWaitBudget">Maximum extra time a stable prefix waits for a boundary before the bounded fallback commits.</param>
    /// <param name="utcNow">Injectable clock used to compute the wait budget deterministically.</param>
    public StreamingTranscriptCommitter(int stabilityWindow, TimeSpan boundaryWaitBudget, Func<DateTime> utcNow)
    {
        if (stabilityWindow < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(stabilityWindow), "StabilityWindow must be at least 2 so partials are emitted before finals.");
        }

        if (boundaryWaitBudget < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(boundaryWaitBudget), "BoundaryWaitBudget must be non-negative.");
        }

        _stabilityWindow = stabilityWindow;
        _boundaryWaitBudget = boundaryWaitBudget;
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>Default boundary wait budget: two seconds (ADR-0007 provisional candidate).</summary>
    internal static TimeSpan DefaultBoundaryWaitBudget() => TimeSpan.FromSeconds(2);

    /// <summary>All text committed so far.</summary>
    public string CommittedText => _committedText.ToString();

    /// <summary>The stable prefix currently held for a boundary (the live in-progress caption).</summary>
    public string PendingStable => _pendingStable;

    /// <summary>Absolute time of the last committed audio sample (estimated from committed segment positions).</summary>
    public DateTime CommittedUntilUtc => _committedUntilUtc;

    /// <summary>
    /// Advances the committed boundary for a freshly decoded window.
    /// </summary>
    /// <param name="segments">Segments with <see cref="TranscriptSegment.Start"/> and <see cref="TranscriptSegment.End"/> relative to <paramref name="windowStartUtc"/>.</param>
    /// <param name="windowStartUtc">Absolute time of the first sample of the window.</param>
    public CommitterResult Update(IReadOnlyList<TranscriptSegment> segments, DateTime windowStartUtc)
    {
        var current = ConcatSegments(segments);

        if (windowStartUtc != _lastWindowStartUtc)
        {
            _recentTexts.Clear();
            _currentEpochCommittedLength = 0;
        }

        _lastWindowStartUtc = windowStartUtc;
        _recentTexts.Enqueue(current);
        while (_recentTexts.Count > _stabilityWindow)
        {
            _recentTexts.Dequeue();
        }

        var stable = _recentTexts.Count >= _stabilityWindow
            ? CommonPrefix(_recentTexts)
            : string.Empty;

        var now = _utcNow();
        UpdatePendingStable(stable, current, now);

        string newFinal = string.Empty;
        if (_pendingStable.Length > 0)
        {
            // Rule 1: stable prefix ends exactly at a real segment boundary → commit the boundary.
            if (EndsAtSegmentBoundary(_pendingStable, segments))
            {
                newFinal = CommitStableText(_pendingStable);
                ClearPending();
            }
            else if (BudgetExpired(now))
            {
                // Rules 3/4: the hard cap is a bounded decision point, never permission to
                // manufacture an artificial caption boundary.
                int completed = LastCompletedBoundaryLength(_pendingStable, segments);
                if (completed > 0)
                {
                    // Rule 3: commit only the latest text backed by a fully completed segment
                    // boundary; the untouched tail stays the in-progress caption (I-1 preserved).
                    newFinal = CommitStableText(_pendingStable.Substring(0, completed));
                    _pendingStable = _pendingStable.Substring(completed);
                    _pendingStableSinceUtc = now;
                }
                // Rule 4: no completed boundary exists (stable sits entirely inside a still-open
                // segment) → keep partial; never emit an interior word-backed FINAL.
            }
        }

        AdvanceCommittedUntil(segments);
        var partial = ComputePartial(current);

        return new CommitterResult(newFinal, partial);
    }

    /// <summary>Resets all committed state. Used when starting a new session.</summary>
    public void Reset()
    {
        _committedText.Clear();
        _committedUntilUtc = DateTime.MinValue;
        _recentTexts.Clear();
        _lastWindowStartUtc = DateTime.MinValue;
        _currentEpochCommittedLength = 0;
        _pendingStable = string.Empty;
        _pendingStableSinceUtc = DateTime.MinValue;
    }

    private string CommitStableText(string stable)
    {
        if (stable.Length == 0)
        {
            return string.Empty;
        }

        var committed = _committedText.ToString();
        if (stable.StartsWith(committed, StringComparison.Ordinal))
        {
            var added = stable.Substring(committed.Length);
            _committedText.Append(added);
            _currentEpochCommittedLength += added.Length;
            return added;
        }

        if (committed.StartsWith(stable, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        // The stable window overlaps the committed text (sliding-window re-decode) instead of being
        // a strict continuation: commit only the part beyond the whole-word tail overlap, joined by
        // a single separator so the committed transcript never repeats or doubles a space.
        int overlap = FindTailOverlap(committed, stable);
        string remaining = stable.Substring(overlap).TrimStart();
        if (remaining.Length > 0 && committed.Length > 0 && !char.IsWhiteSpace(committed[^1]))
        {
            remaining = " " + remaining;
        }

        _committedText.Append(remaining);
        _currentEpochCommittedLength = remaining.Length;
        return remaining;
    }

    private string ComputePartial(string current)
    {
        var committed = _committedText.ToString();
        if (committed.Length == 0)
        {
            return current;
        }

        if (current.StartsWith(committed, StringComparison.Ordinal))
        {
            return current.Substring(committed.Length);
        }

        if (committed.StartsWith(current, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        int overlap = FindTailOverlap(committed, current);
        if (overlap > 0)
        {
            return current.Substring(overlap).TrimStart();
        }

        return current;
    }

    /// <summary>
    /// Classifies the new stable prefix relative to the pending one and updates state/timer:
    /// unchanged, extension, and regression (retraction) all preserve the timer; a replacement
    /// (unrelated text) starts a new interval; an empty stable prefix keeps a pending that is
    /// still consistent with the current decode (a fresh epoch may transiently have no stable text
    /// without abandoning an in-flight caption) and drops one that is no longer part of the current
    /// hypothesis — a superseded pending must never be committed against unrelated segments.
    /// </summary>
    private void UpdatePendingStable(string stable, string current, DateTime now)
    {
        if (stable.Length == 0)
        {
            if (_pendingStable.Length > 0
                && !current.StartsWith(_pendingStable, StringComparison.Ordinal)
                && !_pendingStable.StartsWith(current, StringComparison.Ordinal))
            {
                ClearPending();
            }

            return;
        }

        if (_pendingStable.Length == 0)
        {
            _pendingStable = stable;
            _pendingStableSinceUtc = now;
            return;
        }

        bool extensionOrRegression =
            stable.StartsWith(_pendingStable, StringComparison.Ordinal) ||
            _pendingStable.StartsWith(stable, StringComparison.Ordinal);

        if (extensionOrRegression)
        {
            // Unchanged / extends / retracts: the surviving core was continuously stable, so the
            // timer keeps running from the original convergence.
            _pendingStable = stable;
            return;
        }

        // Replacement: genuinely different text, so a fresh wait begins.
        _pendingStable = stable;
        _pendingStableSinceUtc = now;
    }

    private bool BudgetExpired(DateTime now)
    {
        if (_pendingStableSinceUtc == DateTime.MinValue)
        {
            return false;
        }

        return now - _pendingStableSinceUtc >= _boundaryWaitBudget;
    }

    private void ClearPending()
    {
        _pendingStable = string.Empty;
        _pendingStableSinceUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Returns true when the stable prefix ends exactly at the cumulative character end of a real
    /// segment — a structural boundary decision with no timestamp interpolation.
    /// </summary>
    private bool EndsAtSegmentBoundary(string stable, IReadOnlyList<TranscriptSegment> segments)
    {
        int stableLength = stable.Length;
        int cumulative = 0;
        foreach (var segment in segments)
        {
            int segmentEnd = cumulative + segment.Text.Length;
            if (stableLength == segmentEnd)
            {
                return true;
            }

            if (stableLength < segmentEnd)
            {
                return false;
            }

            cumulative = segmentEnd;
        }

        return false;
    }

    /// <summary>
    /// Returns the length of the deepest fully completed segment boundary the stable prefix has
    /// reached: the largest cumulative character end <c>E</c> across segments with
    /// <c>E &lt;= stable.Length</c>. Returns 0 when the stable prefix sits entirely inside a
    /// still-open segment (no completed boundary). The returned length is a real structural
    /// boundary — never an interpolated interior position (ADR-0007, Option B).
    /// </summary>
    private static int LastCompletedBoundaryLength(string stable, IReadOnlyList<TranscriptSegment> segments)
    {
        int stableLength = stable.Length;
        int cumulative = 0;
        int lastCompleted = 0;
        foreach (var segment in segments)
        {
            int segmentEnd = cumulative + segment.Text.Length;
            if (segmentEnd > stableLength)
            {
                break;
            }

            lastCompleted = segmentEnd;
            cumulative = segmentEnd;
        }

        return lastCompleted;
    }

    /// <summary>
    /// Returns the length of the longest whole-word overlap where the tail of
    /// <paramref name="candidate"/> equals the head of <paramref name="committed"/> — the classic
    /// sliding-window re-emission in which a newly decoded window re-includes the tail of the text
    /// already committed. Returns 0 when there is no reliable overlap. The overlap is backed off to a
    /// whole-word boundary so a partially recognized word is never consumed, and overlaps shorter
    /// than a full word are ignored as coincidental.
    /// </summary>
    private static int FindTailOverlap(string committed, string candidate)
    {
        string committedTail = committed.TrimEnd();
        string candidateHead = candidate.TrimStart();
        if (committedTail.Length == 0 || candidateHead.Length == 0)
        {
            return 0;
        }

        int max = Math.Min(committedTail.Length, candidateHead.Length);
        for (int length = max; length > 0; length--)
        {
            if (!committedTail.EndsWith(candidateHead.Substring(0, length), StringComparison.Ordinal))
            {
                continue;
            }

            // Back off to a whole-word boundary so the overlap ends at the end of a complete word.
            int end = length;
            while (end > 0 && end < candidateHead.Length && !char.IsWhiteSpace(candidateHead[end]))
            {
                end--;
            }

            if (end < 4)
            {
                // Too short to be a reliable re-emission (a common short word shared by two
                // consecutive utterances would otherwise be stripped).
                continue;
            }

            // Map the overlap back to the original candidate, including any trimmed leading space.
            return end + (candidate.Length - candidateHead.Length);
        }

        return 0;
    }

    /// <summary>
    /// Advances <see cref="CommittedUntilUtc"/> to the end of the LAST FULLY COMMITTED segment the
    /// current epoch's committed length reaches. If the committed length falls strictly inside a
    /// segment, it is not advanced past the previous fully committed boundary (backward snap). This
    /// never estimates/interpolates inside a segment, so uncommitted audio is never claimed committed.
    /// </summary>
    private void AdvanceCommittedUntil(IReadOnlyList<TranscriptSegment> segments)
    {
        if (_currentEpochCommittedLength == 0)
        {
            return;
        }

        int cumulative = 0;
        DateTime? estimated = null;
        foreach (var segment in segments)
        {
            int segmentEnd = cumulative + segment.Text.Length;

            if (_currentEpochCommittedLength >= segmentEnd)
            {
                estimated = _lastWindowStartUtc + segment.End;
                cumulative = segmentEnd;
                continue;
            }

            if (_currentEpochCommittedLength > cumulative)
            {
                // Committed inside this segment: do not advance past the previous full segment
                // (backward snap); the segment's tail remains uncommitted audio.
                break;
            }

            break;
        }

        if (estimated.HasValue && estimated.Value > _committedUntilUtc)
        {
            _committedUntilUtc = estimated.Value;
        }
    }

    private static string ConcatSegments(IReadOnlyList<TranscriptSegment> segments)
    {
        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            sb.Append(segment.Text);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns the longest common prefix of all given window texts, backed off to a word boundary
    /// so a partially recognized word is never committed.
    /// </summary>
    private static string CommonPrefix(IEnumerable<string> texts)
    {
        using var enumerator = texts.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return string.Empty;
        }

        var first = enumerator.Current;
        if (first.Length == 0)
        {
            return string.Empty;
        }

        int length = first.Length;
        foreach (var text in texts)
        {
            if (text.Length == 0)
            {
                return string.Empty;
            }

            if (text.Length < length)
            {
                length = text.Length;
            }
        }

        int i = 0;
        while (i < length)
        {
            char c = first[i];
            foreach (var text in texts)
            {
                if (text[i] != c)
                {
                    goto matched;
                }
            }

            i++;
        }

    matched:
        while (i > 0 && i < length && !char.IsWhiteSpace(first[i]))
        {
            i--;
        }

        // Preserve the whitespace run that follows the last complete word so the stable prefix ends
        // at a whole-word position that can still align with a segment char boundary (ADR-0007).
        // Dropping it (the pre-ADR behavior) would shift the prefix one word inside its segment and
        // never satisfy the boundary check.
        while (i < length && char.IsWhiteSpace(first[i]))
        {
            i++;
        }

        return first.Substring(0, i);
    }
}
