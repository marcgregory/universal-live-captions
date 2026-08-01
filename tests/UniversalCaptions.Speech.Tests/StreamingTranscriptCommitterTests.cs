using UniversalCaptions.Speech;

namespace UniversalCaptions.Speech.Tests;

/// <summary>
/// Verifies the deterministic stability-based commit/partial logic of
/// <see cref="StreamingTranscriptCommitter"/>: partial → stable → final transitions,
/// revision safety, no double-commit, and epoch resets.
/// </summary>
public sealed class StreamingTranscriptCommitterTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TranscriptSegment Seg(string text, double startSec, double endSec) =>
        new(text, TimeSpan.FromSeconds(startSec), TimeSpan.FromSeconds(endSec));

    [Fact]
    public void FirstDecode_ProducesOnlyPartial()
    {
        var committer = new StreamingTranscriptCommitter(stabilityWindow: 2);

        var result = committer.Update([Seg("hello world ", 0, 2)], Base);

        Assert.Equal(string.Empty, result.FinalText);
        Assert.Equal("hello world ", result.PartialText);
        Assert.Equal(string.Empty, committer.CommittedText);
    }

    [Fact]
    public void StableText_AcrossTwoWindows_BecomesFinal()
    {
        var committer = new StreamingTranscriptCommitter(stabilityWindow: 2);

        var first = committer.Update([Seg("hello world ", 0, 2)], Base);
        var second = committer.Update([Seg("hello world ", 0, 2)], Base);

        Assert.Equal(string.Empty, first.FinalText);
        Assert.Equal("hello world ", second.FinalText);
        Assert.Equal(string.Empty, second.PartialText);
        Assert.Equal("hello world ", committer.CommittedText);
    }

    [Fact]
    public void GrowingHypothesis_EmitsPartialsThenFinals()
    {
        var committer = new StreamingTranscriptCommitter(stabilityWindow: 2);

        var first = committer.Update([Seg("Today we're going ", 0, 2)], Base);
        var second = committer.Update([Seg("Today we're going to discuss ", 0, 2)], Base);
        var third = committer.Update([Seg("Today we're going to discuss the budget ", 0, 2)], Base);

        Assert.Equal(string.Empty, first.FinalText);
        Assert.Equal("Today we're going ", first.PartialText);

        Assert.Equal("Today we're going ", second.FinalText);
        Assert.Equal("to discuss ", second.PartialText);

        Assert.Equal("to discuss ", third.FinalText);
        Assert.Equal("the budget ", third.PartialText);
        Assert.Equal("Today we're going to discuss ", committer.CommittedText);
    }

    [Fact]
    public void ChangingPartial_DoesNotCommitPrematurely()
    {
        var committer = new StreamingTranscriptCommitter(stabilityWindow: 2);

        committer.Update([Seg("today we're going ", 0, 2)], Base);
        var revised = committer.Update([Seg("tonight we're going ", 0, 2)], Base);
        var stable = committer.Update([Seg("tonight we're going to ", 0, 2)], Base);
        var confirmed = committer.Update([Seg("tonight we're going to ", 0, 2)], Base);

        Assert.Equal(string.Empty, revised.FinalText);
        Assert.Equal("tonight we're going ", stable.FinalText);
        Assert.Equal("to ", confirmed.FinalText);
        Assert.DoesNotContain("today", committer.CommittedText);
        Assert.Equal("tonight we're going to ", committer.CommittedText);
    }

    [Fact]
    public void FinalText_IsNotEmittedTwice()
    {
        var committer = new StreamingTranscriptCommitter(stabilityWindow: 2);
        var finals = new List<string>();

        for (int i = 0; i < 4; i++)
        {
            var result = committer.Update([Seg("hello world ", 0, 2)], Base);
            if (result.FinalText.Length > 0)
            {
                finals.Add(result.FinalText);
            }
        }

        Assert.Equal(["hello world "], finals);
        Assert.Equal("hello world ", committer.CommittedText);
    }

    [Fact]
    public void EpochBoundary_ResetsStabilityMemory()
    {
        var committer = new StreamingTranscriptCommitter(stabilityWindow: 2);
        var epoch2Base = Base + TimeSpan.FromSeconds(10);

        committer.Update([Seg("first sentence ", 0, 2)], Base);
        var epoch1Final = committer.Update([Seg("first sentence ", 0, 2)], Base);
        Assert.Equal("first sentence ", epoch1Final.FinalText);

        var epoch2First = committer.Update([Seg("second utterance ", 0, 2)], epoch2Base);
        var epoch2Final = committer.Update([Seg("second utterance ", 0, 2)], epoch2Base);

        Assert.Equal(string.Empty, epoch2First.FinalText);
        Assert.Equal("second utterance ", epoch2Final.FinalText);
        Assert.Equal("first sentence second utterance ", committer.CommittedText);
    }

    [Fact]
    public void EmptyWindow_ResetsStabilityUntilHypothesisReturns()
    {
        var committer = new StreamingTranscriptCommitter(stabilityWindow: 2);

        committer.Update([Seg("hello ", 0, 2)], Base);
        committer.Update([Seg("hello ", 0, 2)], Base);
        var gap = committer.Update([], Base);
        var afterGap = committer.Update([Seg("hello world ", 0, 2)], Base);

        Assert.Equal(string.Empty, gap.FinalText);
        Assert.Equal(string.Empty, afterGap.FinalText);
        Assert.Equal("hello ", committer.CommittedText);
    }

    [Fact]
    public void CommittedUntilUtc_AdvancesToEndOfCommittedSegment()
    {
        var committer = new StreamingTranscriptCommitter(stabilityWindow: 2);
        committer.Update([Seg("hello ", 0, 2)], Base);

        var result = committer.Update([Seg("hello ", 0, 2)], Base);

        Assert.Equal("hello ", result.FinalText);
        Assert.Equal(Base + TimeSpan.FromSeconds(2), committer.CommittedUntilUtc);
    }

    [Fact]
    public void Reset_ClearsCommittedState()
    {
        var committer = new StreamingTranscriptCommitter(stabilityWindow: 2);
        committer.Update([Seg("hello ", 0, 2)], Base);
        committer.Update([Seg("hello ", 0, 2)], Base);

        committer.Reset();

        Assert.Equal(string.Empty, committer.CommittedText);
        Assert.Equal(DateTime.MinValue, committer.CommittedUntilUtc);
    }

    [Fact]
    public void StabilityWindow_BelowTwo_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamingTranscriptCommitter(stabilityWindow: 1));
    }
}
