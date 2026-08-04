using UniversalCaptions.Speech;

namespace UniversalCaptions.Speech.Tests;

/// <summary>
/// Verifies the deterministic boundary-aware commit/partial logic of
/// <see cref="StreamingTranscriptCommitter"/>: stability + segment-boundary gating, the bounded
/// budget fallback, timer classification (extension/regression/replacement), epoch resets, and
/// the backward-snapped <see cref="StreamingTranscriptCommitter.CommittedUntilUtc"/>.
/// </summary>
/// <remarks>
/// A stable prefix that equals the entire window text necessarily ends AT the last segment boundary
/// and commits immediately (ADR-0007 clean case). To observe the held-inside-segment path, windows
/// must have DIVERGING tails so the stable prefix is strictly shorter than its containing segment.
/// </remarks>
public sealed class StreamingTranscriptCommitterTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TranscriptSegment Seg(string text, double startSec, double endSec) =>
        new(text, TimeSpan.FromSeconds(startSec), TimeSpan.FromSeconds(endSec));

    /// <summary>A controllable clock so budget/fallback timing is deterministic in tests.</summary>
    private sealed class TestClock
    {
        public DateTime Now = Base;
        public DateTime GetNow() => Now;
    }

    private static (StreamingTranscriptCommitter, TestClock) NewCommitter(
        int stabilityWindow = 2,
        TimeSpan? budget = null)
    {
        var clock = new TestClock { Now = Base };
        var committer = new StreamingTranscriptCommitter(
            stabilityWindow,
            budget ?? StreamingTranscriptCommitter.DefaultBoundaryWaitBudget(),
            clock.GetNow);
        return (committer, clock);
    }

    // -------------------------------------------------------------------------------------------
    // Basic transitions
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void FirstDecode_ProducesOnlyPartial()
    {
        var (committer, _) = NewCommitter();
        var result = committer.Update(new[] { Seg("hello world ", 0, 2) }, Base);

        Assert.Equal(string.Empty, result.FinalText);
        Assert.Equal("hello world ", result.PartialText);
        Assert.Equal(string.Empty, committer.CommittedText);
    }

    [Fact]
    public void StableText_EndingAtSegmentBoundary_BecomesFinal()
    {
        var (committer, _) = NewCommitter();

        var first = committer.Update(new[] { Seg("hello world ", 0, 2) }, Base);
        var second = committer.Update(new[] { Seg("hello world ", 0, 2) }, Base);

        Assert.Equal(string.Empty, first.FinalText);
        Assert.Equal("hello world ", second.FinalText);
        Assert.Equal(string.Empty, second.PartialText);
        Assert.Equal("hello world ", committer.CommittedText);
    }

    [Fact]
    public void GrowingHypothesis_EmitsPartialsThenFinals_AtBoundaries()
    {
        var (committer, _) = NewCommitter();

        var first = committer.Update(new[] { Seg("hello world ", 0, 2) }, Base);
        var second = committer.Update(new[] { Seg("hello world ", 0, 2), Seg("foo bar ", 2, 4) }, Base);
        var third = committer.Update(new[] { Seg("hello world ", 0, 2), Seg("foo bar ", 2, 4), Seg("baz ", 4, 5) }, Base);

        // Stable prefix "hello world " ends exactly at segment 1's end → FINAL.
        Assert.Equal(string.Empty, first.FinalText);
        Assert.Equal("hello world ", second.FinalText);
        Assert.Equal("foo bar ", second.PartialText);

        // Continues: "foo bar " now stable and at segment 2's end => FINAL.
        Assert.Equal("foo bar ", third.FinalText);
        Assert.Equal("baz ", third.PartialText);
        Assert.Equal("hello world foo bar ", committer.CommittedText);
    }

    [Fact]
    public void FinalText_IsNotEmittedTwice()
    {
        var (committer, _) = NewCommitter();
        var finals = new List<string>();

        for (int i = 0; i < 4; i++)
        {
            var result = committer.Update(new[] { Seg("hello world ", 0, 2) }, Base);
            if (result.FinalText.Length > 0)
            {
                finals.Add(result.FinalText);
            }
        }

        Assert.Single(finals);
        Assert.Equal("hello world ", finals[0]);
        Assert.Equal("hello world ", committer.CommittedText);
    }

    // -------------------------------------------------------------------------------------------
    // Boundary gating
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void StablePrefix_InsideSegment_DoesNotFinalize()
    {
        // Diverging tails: the stable prefix "hello " is strictly inside the single segment.
        var (committer, _) = NewCommitter();
        committer.Update(new[] { Seg("hello alpha ", 0, 3) }, Base);
        var second = committer.Update(new[] { Seg("hello beta ", 0, 3) }, Base);

        Assert.Equal(string.Empty, second.FinalText);
        Assert.Equal(string.Empty, committer.CommittedText);
        Assert.Equal("hello beta ", second.PartialText);
    }

    [Fact]
    public void BoundaryWithoutStability_DoesNotCommitNewText()
    {
        // A segment boundary exists, but only "first thing " has been seen twice (stable + boundary).
        // "second thing " is not stable yet and must not be committed.
        var (committer, _) = NewCommitter();
        committer.Update(new[] { Seg("first thing ", 0, 2) }, Base);

        var second = committer.Update(new[] { Seg("first thing ", 0, 2), Seg("second thing ", 2, 4) }, Base);

        Assert.Equal("first thing ", second.FinalText);
        Assert.DoesNotContain("second", committer.CommittedText);
    }

    [Fact]
    public void BoundaryArrivingLater_CommitsTheStablePrefix()
    {
        var (committer, _) = NewCommitter();

        // Held inside a single segment (diverging tails).
        committer.Update(new[] { Seg("tonight we're going zzz ", 0, 3) }, Base);
        committer.Update(new[] { Seg("tonight we're going ddd ", 0, 3) }, Base);
        Assert.Equal(string.Empty, committer.CommittedText);

        // Now the segment structure closes exactly after the stable prefix → boundary → FINAL.
        var result = committer.Update(new[] { Seg("tonight we're going ", 0, 2), Seg("to the ballpark ", 2, 4) }, Base);

        Assert.Equal("tonight we're going ", result.FinalText);
        Assert.Equal("to the ballpark ", result.PartialText);
        Assert.Equal("tonight we're going ", committer.CommittedText);
    }

    [Fact]
    public void ChangingPartial_StablePrefixStaysHeld_UntilBoundaryOrBudget()
    {
        var (committer, _) = NewCommitter(budget: TimeSpan.FromSeconds(10));

        // "tonight we're going " is the common prefix; each window's single segment continues past
        // it, so it is never at a boundary and must not FINAL.
        committer.Update(new[] { Seg("tonight we're going to the ballpark ", 0, 4) }, Base);
        committer.Update(new[] { Seg("tonight we're going to the beach ", 0, 4) }, Base);

        Assert.DoesNotContain("today", committer.CommittedText);
        Assert.DoesNotContain("tonight", committer.CommittedText);
    }

    // -------------------------------------------------------------------------------------------
    // Budget fallback (ADR-0007 Option B: boundary-preserving fallback, no manufactured FINAL)
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void BudgetExpiry_WithStableEntirelyInsideOpenSegment_DoesNotManufactureFinal()
    {
        // Rule 4: the stable prefix "hello world i am still going " sits entirely inside a
        // still-open single segment. When the budget expires there is NO completed boundary, so the
        // fragment must NOT be manufactured as a FINAL (this was the pre-fix `country can do for`).
        var (committer, clock) = NewCommitter(budget: TimeSpan.FromSeconds(2));
        committer.Update(new[] { Seg("hello world i am still going zzz ", 0, 5) }, Base);
        committer.Update(new[] { Seg("hello world i am still going ddd ", 0, 5) }, Base);

        Assert.Equal(string.Empty, committer.CommittedText); // held inside the sole segment

        clock.Now = Base.AddSeconds(2);
        committer.Update(new[] { Seg("hello world i am still going eee ", 0, 5) }, Base);

        Assert.Equal(string.Empty, committer.CommittedText);
    }

    [Fact]
    public void BudgetExpiry_CommitsOnlyLastCompletedBoundary_AndKeepsTailPartial()
    {
        // Rule 3: stable "hello world i am still going " overpasses the completed boundary of
        // segment 0 ("hello world ") but ends inside segment 1. At budget expiry the fallback
        // commits ONLY the completed boundary, never the interior stable prefix.
        var (committer, clock) = NewCommitter(budget: TimeSpan.FromSeconds(2));
        committer.Update(new[] { Seg("hello world ", 0, 2), Seg("i am still going zzz ", 2, 5) }, Base);
        committer.Update(new[] { Seg("hello world ", 0, 2), Seg("i am still going ddd ", 2, 5) }, Base);

        Assert.Equal(string.Empty, committer.CommittedText);

        clock.Now = Base.AddSeconds(2);
        committer.Update(new[] { Seg("hello world ", 0, 2), Seg("i am still going eee ", 2, 5) }, Base);

        Assert.Equal("hello world ", committer.CommittedText);
        Assert.Equal("i am still going ", committer.PendingStable);
    }

    [Fact]
    public void BudgetExpiry_CommitsOnlyLastCompletedBoundary_WhenBoundaryExistsInsideStable()
    {
        // Rule 3 with the boundary strictly inside the stable prefix: commits text up to the
        // completed segment end only; the over-passed tail stays pending for its own wait window.
        var (committer, clock) = NewCommitter(budget: TimeSpan.FromSeconds(3));
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h zzz ", 2, 4) }, Base);
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h ddd ", 2, 4) }, Base);

        clock.Now = Base.AddSeconds(3);
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h eee ", 2, 4) }, Base);

        Assert.Equal("a b c d ", committer.CommittedText);
        Assert.Equal("e f g h ", committer.PendingStable);
    }

    [Fact]
    public void Extension_DoesNotResetTheTimer_AndDeadlineIsBounded()
    {
        var (committer, clock) = NewCommitter(budget: TimeSpan.FromSeconds(3));
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h zzz ", 2, 4) }, Base);
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h ddd ", 2, 4) }, Base);

        Assert.Equal(string.Empty, committer.CommittedText);

        // Stable extends to "a b c d e f g h i j k " — extension must NOT reset the timer.
        clock.Now = Base.AddSeconds(1);
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h i j k zzz ", 2, 4) }, Base);
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h i j k ddd ", 2, 4) }, Base);

        // 3 s after FIRST convergence → fallback fires at the original deadline (timer not reset).
        clock.Now = Base.AddSeconds(3);
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h i j k eee ", 2, 4) }, Base);

        Assert.Equal("a b c d ", committer.CommittedText);
    }

    [Fact]
    public void Unchanged_DoesNotResetTheTimer()
    {
        var (committer, clock) = NewCommitter(budget: TimeSpan.FromSeconds(3));
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h zzz ", 2, 4) }, Base);
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h ddd ", 2, 4) }, Base);

        clock.Now = Base.AddSeconds(1);
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h eee ", 2, 4) }, Base); // unchanged stable

        clock.Now = Base.AddSeconds(3);
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h fff ", 2, 4) }, Base);

        Assert.Equal("a b c d ", committer.CommittedText);
    }

    [Fact]
    public void Regression_DoesNotResetTheTimer()
    {
        var (committer, clock) = NewCommitter(budget: TimeSpan.FromSeconds(3));
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h i j zzz ", 2, 4) }, Base);
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h i j ddd ", 2, 4) }, Base);

        // New hypothesis retracts the tail: "a b c d e f g h " (regression) → timer continues.
        clock.Now = Base.AddSeconds(1);
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h zzz ", 2, 4) }, Base);
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h ddd ", 2, 4) }, Base);

        clock.Now = Base.AddSeconds(3);
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h eee ", 2, 4) }, Base);

        Assert.Equal("a b c d ", committer.CommittedText);
    }

    [Fact]
    public void Replacement_ResetsTheTimer_AndDropsOldText()
    {
        var (committer, clock) = NewCommitter(budget: TimeSpan.FromSeconds(3));
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h zzz ", 2, 4) }, Base);
        committer.Update(new[] { Seg("a b c d ", 0, 2), Seg("e f g h ddd ", 2, 4) }, Base);

        // Replacement: entirely different text starts a new interval; the old content is dropped.
        clock.Now = Base.AddSeconds(1);
        committer.Update(new[] { Seg("x y z ", 0, 1), Seg("w q r zzz ", 1, 3) }, Base);
        committer.Update(new[] { Seg("x y z ", 0, 1), Seg("w q r ddd ", 1, 3) }, Base);

        clock.Now = Base.AddSeconds(1.5); // 0.5 s after replacement, < 3 s budget
        committer.Update(new[] { Seg("x y z ", 0, 1), Seg("w q r eee ", 1, 3) }, Base);

        Assert.Equal(string.Empty, committer.CommittedText);
    }

    // -------------------------------------------------------------------------------------------
    // Overlap / sliding-window re-emission (TD-006/007), unchanged behavior
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void OverlappingWindow_StripsCommittedTailFromPartial()
    {
        var (committer, _) = NewCommitter();
        committer.Update(new[] { Seg("Today we're going ", 0, 2) }, Base);
        committer.Update(new[] { Seg("Today we're going ", 0, 2) }, Base);
        Assert.Equal("Today we're going ", committer.CommittedText);

        var result = committer.Update(new[] { Seg("going to discuss ", 0, 2) }, Base);

        Assert.Equal(string.Empty, result.FinalText);
        Assert.Equal("to discuss ", result.PartialText);
    }

    [Fact]
    public void OverlappingWindow_CommitsOnlyTheNonOverlappingPart()
    {
        var (committer, _) = NewCommitter();
        committer.Update(new[] { Seg("Today we're going ", 0, 2) }, Base);
        committer.Update(new[] { Seg("Today we're going ", 0, 2) }, Base);
        committer.Update(new[] { Seg("going to discuss ", 0, 2) }, Base);

        var result = committer.Update(new[] { Seg("going to discuss ", 0, 2) }, Base);

        Assert.Equal("to discuss ", result.FinalText);
        Assert.Equal("Today we're going to discuss ", committer.CommittedText);
    }

    [Fact]
    public void NoOverlap_WhenWindowDoesNotReemitCommittedTail()
    {
        var (committer, _) = NewCommitter();
        committer.Update(new[] { Seg("first sentence ", 0, 2) }, Base);
        committer.Update(new[] { Seg("first sentence ", 0, 2) }, Base);

        var result = committer.Update(new[] { Seg("second utterance ", 0, 2) }, Base);

        Assert.Equal("second utterance ", result.PartialText);
    }

    [Fact]
    public void ShortCoincidentalOverlap_IsNotStripped()
    {
        var (committer, _) = NewCommitter();
        committer.Update(new[] { Seg("We saw the ", 0, 2) }, Base);
        committer.Update(new[] { Seg("We saw the ", 0, 2) }, Base);

        var result = committer.Update(new[] { Seg("the end is near ", 0, 2) }, Base);

        Assert.Equal("the end is near ", result.PartialText);
    }

    // -------------------------------------------------------------------------------------------
    // Epoch rollover / state reset
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void EpochBoundary_ResetsStabilityMemory()
    {
        var (committer, _) = NewCommitter();
        var epoch2Base = Base + TimeSpan.FromSeconds(10);

        committer.Update(new[] { Seg("first sentence ", 0, 2) }, Base);
        var epoch1Final = committer.Update(new[] { Seg("first sentence ", 0, 2) }, Base);
        Assert.Equal("first sentence ", epoch1Final.FinalText);

        var epoch2First = committer.Update(new[] { Seg("second utterance ", 0, 2) }, epoch2Base);
        var epoch2Final = committer.Update(new[] { Seg("second utterance ", 0, 2) }, epoch2Base);

        Assert.Equal(string.Empty, epoch2First.FinalText);
        Assert.Equal("second utterance ", epoch2Final.FinalText);
        Assert.Equal("first sentence second utterance ", committer.CommittedText);
    }

    [Fact]
    public void PendingStable_Survives_EpochRollover()
    {
        // Timer measured from the ORIGINAL convergence (epoch 1), not a reset at epoch 2. At
        // epoch2Base+2 budget(5 s since Base) has expired → rule 3 commits the completed boundary
        // even though the pending stable was carried across the epoch roll.
        var (committer, clock) = NewCommitter(budget: TimeSpan.FromSeconds(5));
        var epoch2Base = Base + TimeSpan.FromSeconds(10);

        // Hold a stable prefix that overpasses segment 0's completed boundary but ends inside seg 1.
        committer.Update(new[] { Seg("hello ", 0, 1), Seg("world zzz ", 1, 3) }, Base);
        committer.Update(new[] { Seg("hello ", 0, 1), Seg("world ddd ", 1, 3) }, Base);
        Assert.Equal(string.Empty, committer.CommittedText);

        // New epoch, same pending => still held, timer continues from epoch 1.
        committer.Update(new[] { Seg("hello ", 0, 1), Seg("world eee ", 1, 3) }, epoch2Base);
        committer.Update(new[] { Seg("hello ", 0, 1), Seg("world fff ", 1, 3) }, epoch2Base);
        Assert.Equal(string.Empty, committer.CommittedText);

        // Only 2 s after the epoch-2 convergence (Base+12). If the timer had reset at epoch 2, the
        // 5 s budget would NOT be met; committing proves the timer survived the rollover.
        clock.Now = epoch2Base.AddSeconds(2);
        committer.Update(new[] { Seg("hello ", 0, 1), Seg("world ggg ", 1, 3) }, epoch2Base);

        Assert.Equal("hello ", committer.CommittedText);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var (committer, _) = NewCommitter();
        committer.Update(new[] { Seg("hello ", 0, 2) }, Base);
        committer.Update(new[] { Seg("hello ", 0, 2) }, Base);

        committer.Reset();

        Assert.Equal(string.Empty, committer.CommittedText);
        Assert.Equal(DateTime.MinValue, committer.CommittedUntilUtc);
    }

    // -------------------------------------------------------------------------------------------
    // CommittedUntilUtc (backward snap, ADR-0007)
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void CommittedUntilUtc_BackwardSnaps_ToLastFullyCommittedBoundary()
    {
        var (committer, _) = NewCommitter();

        // Only "hello " (segment 0) becomes stable and ends at its boundary; "world zzz"/"warp ddd"
        // diverge, so the commit stops at segment 0. Backward snap = segment 0's end (1 s).
        committer.Update(new[] { Seg("hello ", 0, 1), Seg("world zzz ", 1, 3) }, Base);
        var second = committer.Update(new[] { Seg("hello ", 0, 1), Seg("warp ddd ", 1, 3) }, Base);

        Assert.Equal("hello ", second.FinalText);
        Assert.Equal(Base.AddSeconds(1), committer.CommittedUntilUtc);
    }

    [Fact]
    public void CommittedUntilUtc_DoesNotAdvance_WhenNoCompletedBoundaryExists()
    {
        // Rule 4: the stable prefix "hello world " ends inside the sole segment and no completed
        // boundary exists, so nothing is committed and CommittedUntilUtc stays put (I-1).
        var (committer, clock) = NewCommitter(budget: TimeSpan.FromSeconds(2));
        committer.Update(new[] { Seg("hello world zzz ", 0, 3) }, Base);
        committer.Update(new[] { Seg("hello world ddd ", 0, 3) }, Base);

        clock.Now = Base.AddSeconds(2);
        committer.Update(new[] { Seg("hello world eee ", 0, 3) }, Base);

        Assert.Equal(string.Empty, committer.CommittedText);
        Assert.Equal(DateTime.MinValue, committer.CommittedUntilUtc);
    }

    [Fact]
    public void CommittedUntilUtc_SnapsToLastCompletedBoundary_OnRule3Fallback()
    {
        // Rule 3: the fallback commits up to the last completed boundary (segment 0 "hello ", End =
        // 1 s) and CommittedUntilUtc snaps to that real boundary, never an interpolated interior.
        var (committer, clock) = NewCommitter(budget: TimeSpan.FromSeconds(2));
        committer.Update(new[] { Seg("hello ", 0, 1), Seg("world zzz ", 1, 3) }, Base);
        committer.Update(new[] { Seg("hello ", 0, 1), Seg("world ddd ", 1, 3) }, Base);

        clock.Now = Base.AddSeconds(2);
        committer.Update(new[] { Seg("hello ", 0, 1), Seg("world eee ", 1, 3) }, Base);

        Assert.Equal("hello ", committer.CommittedText);
        Assert.Equal(Base.AddSeconds(1), committer.CommittedUntilUtc);
    }

    // -------------------------------------------------------------------------------------------
    // Construction guards
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void StabilityWindow_BelowTwo_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamingTranscriptCommitter(stabilityWindow: 1));
    }

    [Fact]
    public void NegativeBudget_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StreamingTranscriptCommitter(2, TimeSpan.FromSeconds(-1), () => DateTime.UtcNow));
    }

    [Fact]
    public void NullClock_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new StreamingTranscriptCommitter(2, TimeSpan.FromSeconds(2), null!));
    }
}
