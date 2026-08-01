using System.Text;

namespace UniversalCaptions.Speech;

/// <summary>
/// The result of one commit pass: the newly finalized text and the current in-progress text.
/// </summary>
public readonly record struct CommitterResult(string FinalText, string PartialText);

/// <summary>
/// Converts repeatedly decoded windows of segments into committed (final) and in-progress
/// (partial) transcript text using a stability-based strategy.
/// </summary>
/// <remarks>
/// <para>
/// Each decode pass contributes the concatenated text of its segments — the current hypothesis
/// for the audio window. Text is classified as:
/// </para>
/// <list type="bullet">
/// <item><term>partial</term><description>the actively changing tail that has not yet been repeated enough to trust;</description></item>
/// <item><term>stable</term><description>text that has appeared unchanged across <c>stabilityWindow</c> consecutive decode passes;</description></item>
/// <item><term>final</term><description>the stable text, emitted once as a committed caption that will not be rewritten.</description></item>
/// </list>
/// <para>
/// Stability is measured as the longest common prefix of the last <c>stabilityWindow</c> window
/// transcripts, so a revision anywhere in the shared region simply stops advancing the commit
/// boundary. Text that has already been committed is never emitted again. When the audio window
/// advances past committed audio (an epoch boundary), commit memory resets automatically.
/// Deterministic and model-agnostic; works even when the decoder yields a single whole-window segment.
/// </para>
/// </remarks>
public sealed class StreamingTranscriptCommitter
{
    private readonly StringBuilder _committedText = new();
    private readonly Queue<string> _recentTexts = new();
    private readonly int _stabilityWindow;
    private DateTime _committedUntilUtc = DateTime.MinValue;
    private DateTime _lastWindowStartUtc = DateTime.MinValue;
    private int _currentEpochCommittedLength;

    /// <summary>
    /// Creates a committer that finalizes text only after it has been observed unchanged across
    /// <paramref name="stabilityWindow"/> consecutive decode passes.
    /// </summary>
    /// <param name="stabilityWindow">Consecutive identical decodes required before text becomes final. Must be at least 2.</param>
    public StreamingTranscriptCommitter(int stabilityWindow)
    {
        if (stabilityWindow < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(stabilityWindow), "StabilityWindow must be at least 2 so partials are emitted before finals.");
        }

        _stabilityWindow = stabilityWindow;
    }

    /// <summary>All text committed so far.</summary>
    public string CommittedText => _committedText.ToString();

    /// <summary>Absolute time of the last committed audio sample (estimated from segment positions).</summary>
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

        var newFinal = CommitStableText(stable);
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

        _committedText.Append(stable);
        _currentEpochCommittedLength = stable.Length;
        return stable;
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

        return current;
    }

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
            int segmentStart = cumulative;
            int segmentEnd = cumulative + segment.Text.Length;
            cumulative = segmentEnd;

            if (segmentEnd <= 0)
            {
                continue;
            }

            var segmentStartUtc = _lastWindowStartUtc + segment.Start;
            var segmentEndUtc = _lastWindowStartUtc + segment.End;

            if (_currentEpochCommittedLength >= segmentEnd)
            {
                estimated = segmentEndUtc;
            }
            else if (_currentEpochCommittedLength > segmentStart)
            {
                double fraction = segment.Text.Length > 0
                    ? (double)(_currentEpochCommittedLength - segmentStart) / segment.Text.Length
                    : 0;
                estimated = segmentStartUtc + TimeSpan.FromSeconds(
                    (segmentEndUtc - segmentStartUtc).TotalSeconds * fraction);
                break;
            }
            else
            {
                break;
            }
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

        return first.Substring(0, i);
    }
}
