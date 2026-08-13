using UniversalCaptions.Core.Captions;

namespace UniversalCaptions.Captions.Tests;

/// <summary>
/// Verifies <see cref="CaptionState"/> ordering, bounded history, active-line lifecycle, translation
/// configuration, and session lifecycle in isolation from the caption service.
/// </summary>
public sealed class CaptionStateTests
{
    private static CaptionLine Final(long sequence, string text = "hello") =>
        new(text, "en", sequence, DateTime.UtcNow, CaptionLineState.Final, committedAtUtc: DateTime.UtcNow);

    private static CaptionLine Active(long sequence, string text = "hel") =>
        new(text, "en", sequence, DateTime.UtcNow, CaptionLineState.Active);

    [Fact]
    public void AddFinalLine_KeepsHistoryOrderedBySequence()
    {
        var state = new CaptionState(10);
        state.AddFinalLine(Final(5));
        state.AddFinalLine(Final(2));
        state.AddFinalLine(Final(8));

        Assert.Equal(new long[] { 2, 5, 8 }, state.History.Select(line => line.Sequence));
    }

    [Fact]
    public void AddFinalLine_DuplicateSequence_ReplacesExistingLine()
    {
        var state = new CaptionState(10);
        state.AddFinalLine(Final(3, "old"));
        state.AddFinalLine(Final(3, "new"));

        var line = Assert.Single(state.History);
        Assert.Equal("new", line.Text);
    }

    [Fact]
    public void AddFinalLine_BoundedHistory_DropsOldest()
    {
        var state = new CaptionState(2);
        state.AddFinalLine(Final(1, "one"));
        state.AddFinalLine(Final(2, "two"));
        state.AddFinalLine(Final(3, "three"));

        Assert.Equal(new long[] { 2, 3 }, state.History.Select(line => line.Sequence));
    }

    [Fact]
    public void AddFinalLine_HistoryCapacityZero_RetainsNothing()
    {
        var state = new CaptionState(0);
        state.AddFinalLine(Final(1));

        Assert.Empty(state.History);
    }

    [Fact]
    public void AddFinalLine_ActiveLine_Throws()
    {
        var state = new CaptionState(10);
        Assert.Throws<ArgumentException>(() => state.AddFinalLine(Active(1)));
    }

    [Fact]
    public void UpdateActiveLine_ReplacesActiveLine()
    {
        var state = new CaptionState(10);
        state.UpdateActiveLine(Active(1, "hel"));
        state.UpdateActiveLine(Active(1, "hello"));

        Assert.Equal("hello", state.ActiveLine?.Text);
        Assert.Empty(state.History);
    }

    [Fact]
    public void UpdateActiveLine_FinalLine_Throws()
    {
        var state = new CaptionState(10);
        Assert.Throws<ArgumentException>(() => state.UpdateActiveLine(Final(1)));
    }

    [Fact]
    public void ClearActiveLine_RemovesActiveLine()
    {
        var state = new CaptionState(10);
        state.UpdateActiveLine(Active(1));

        state.ClearActiveLine();

        Assert.Null(state.ActiveLine);
    }

    [Fact]
    public void ReplaceFinalLine_AppliesTranslation_AndReturnsTrue()
    {
        var state = new CaptionState(10);
        var line = Final(3, "hello");
        state.AddFinalLine(line);

        var translated = line.WithTranslation("kumusta", "tl");
        Assert.True(state.ReplaceFinalLine(line, translated));

        var result = Assert.Single(state.History);
        Assert.Equal("kumusta", result.TranslatedText);
        Assert.Equal("hello", result.Text);
    }

    [Fact]
    public void ReplaceFinalLine_DifferentInstanceAtSameSequence_ReturnsFalse()
    {
        var state = new CaptionState(10);
        state.AddFinalLine(Final(3, "hello"));

        // A re-delivered final with the same sequence is a different instance, so a stale
        // translation started for the earlier line must not overwrite it.
        Assert.False(state.ReplaceFinalLine(Final(3, "hello"), Final(3, "hello").WithTranslation("x", "tl")));

        var line = Assert.Single(state.History);
        Assert.Equal("hello", line.Text);
        Assert.Null(line.TranslatedText);
    }

    [Fact]
    public void ReplaceFinalLine_MissingSequence_ReturnsFalse_AndChangesNothing()
    {
        var state = new CaptionState(10);
        state.AddFinalLine(Final(3, "hello"));

        Assert.False(state.ReplaceFinalLine(Final(7), Final(7).WithTranslation("x", "tl")));

        var line = Assert.Single(state.History);
        Assert.Equal("hello", line.Text);
        Assert.Null(line.TranslatedText);
    }

    [Fact]
    public void ReplaceActiveLine_AppliesTranslation_AndReturnsTrue()
    {
        var state = new CaptionState(10);
        var line = Active(1, "hello");
        state.UpdateActiveLine(line);

        var translated = line.WithTranslation("kumusta", "tl");
        Assert.True(state.ReplaceActiveLine(line, translated));

        Assert.Equal("kumusta", state.ActiveLine?.TranslatedText);
        Assert.Equal("hello", state.ActiveLine?.Text);
    }

    [Fact]
    public void ReplaceActiveLine_DifferentInstance_ReturnsFalse_AndChangesNothing()
    {
        var state = new CaptionState(10);
        state.UpdateActiveLine(Active(1, "hello"));

        // A newer partial is a different instance, so a stale translation started for the earlier
        // partial must not overwrite it.
        Assert.False(state.ReplaceActiveLine(Active(1, "hello"), Active(1, "hello").WithTranslation("x", "tl")));

        Assert.Equal("hello", state.ActiveLine?.Text);
        Assert.Null(state.ActiveLine?.TranslatedText);
    }

    [Fact]
    public void ReplaceActiveLine_AfterClear_ReturnsFalse()
    {
        var state = new CaptionState(10);
        var line = Active(1, "hello");
        state.UpdateActiveLine(line);
        state.ClearActiveLine();

        Assert.False(state.ReplaceActiveLine(line, line.WithTranslation("x", "tl")));
        Assert.Null(state.ActiveLine);
    }

    [Fact]
    public void ReplaceActiveLine_FinalLine_Throws()
    {
        var state = new CaptionState(10);
        Assert.Throws<ArgumentException>(() => state.ReplaceActiveLine(Final(1), Final(1).WithTranslation("x", "tl")));
    }

    [Fact]
    public void SetTranslation_EnabledSetsTarget_DisabledClearsTarget()
    {
        var state = new CaptionState(10);
        state.SetTranslation(true, "tl");

        Assert.True(state.TranslationEnabled);
        Assert.Equal("tl", state.TargetLanguage);

        state.SetTranslation(false, null);

        Assert.False(state.TranslationEnabled);
        Assert.Null(state.TargetLanguage);
    }

    [Fact]
    public void SetTranslation_EnabledWithoutTarget_Throws()
    {
        var state = new CaptionState(10);
        Assert.Throws<ArgumentException>(() => state.SetTranslation(true, null));
    }

    [Fact]
    public void BeginEndSession_TogglesSessionLifecycle()
    {
        var state = new CaptionState(10);
        Assert.False(state.IsSessionActive);

        state.BeginSession();
        Assert.True(state.IsSessionActive);
        state.UpdateActiveLine(Active(1));

        state.EndSession();
        Assert.False(state.IsSessionActive);
        Assert.Null(state.ActiveLine);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var state = new CaptionState(10);
        state.BeginSession();
        state.UpdateActiveLine(Active(1));
        state.AddFinalLine(Final(2));
        state.SetTranslation(true, "tl");

        state.Reset();

        Assert.Null(state.ActiveLine);
        Assert.Empty(state.History);
        Assert.False(state.TranslationEnabled);
        Assert.Null(state.TargetLanguage);
        Assert.False(state.IsSessionActive);
    }

    [Fact]
    public void Constructor_NegativeCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptionState(-1));
    }

    [Fact]
    public void ClearTranslationHistory_RemovesOnlyTranslationOriginEntries()
    {
        // Language-agnostic history scrub for the Translate-OFF path: every LineOrigin.Translation
        // entry is removed regardless of target language; every LineOrigin.SourceStt entry stays.
        // The returned count lets the service decide whether StateChanged is worth raising.
        var state = new CaptionState(20);
        state.AddFinalLine(Final(1, "english source"));                                         // SourceStt
        state.AddFinalLine(new CaptionLine("tagalog", "en", 2, DateTime.UtcNow,
            CaptionLineState.Final, committedAtUtc: DateTime.UtcNow, origin: LineOrigin.Translation)); // Translation
        state.AddFinalLine(Final(3, "another english source"));                                 // SourceStt
        state.AddFinalLine(new CaptionLine("japanese", "en", 4, DateTime.UtcNow,
            CaptionLineState.Final, committedAtUtc: DateTime.UtcNow, origin: LineOrigin.Translation)); // Translation

        int removed = state.ClearTranslationHistory();

        Assert.Equal(2, removed);
        Assert.Equal(new long[] { 1, 3 }, state.History.Select(line => line.Sequence));
        Assert.All(state.History, line => Assert.Equal(LineOrigin.SourceStt, line.Origin));
    }

    [Fact]
    public void ClearTranslationHistory_WhenNoTranslationEntries_ReturnsZero_AndKeepsSource()
    {
        // No-op semantics: calling clear when nothing matches does not destroy source history. The
        // service relies on the zero-return to skip StateChanged (the overlay would re-render for no
        // reason).
        var state = new CaptionState(10);
        state.AddFinalLine(Final(1, "english only"));

        int removed = state.ClearTranslationHistory();

        Assert.Equal(0, removed);
        Assert.Single(state.History);
        Assert.Equal("english only", state.History[0].Text);
    }

    [Fact]
    public void ClearTranslationHistory_DoesNotTouchActiveTranslationLine()
    {
        // The history scrub is scoped to the committed history. The active translation line is a
        // separate slot owned by ClearTranslationActiveLine / SetTranslationEnabled(false); mixing
        // the two responsibilities here would let a future caller break either contract.
        var state = new CaptionState(10);
        state.UpdateTranslationActiveLine(new CaptionLine("active tagalog partial", "en", 1,
            DateTime.UtcNow, CaptionLineState.Active, origin: LineOrigin.Translation));
        // No AddFinalLine here: a committed final at the same sequence would clear the active line
        // by CaptionState's own commit semantics, which would mask what this test is checking.
        state.AddFinalLine(new CaptionLine("unrelated english", "en", 99, DateTime.UtcNow,
            CaptionLineState.Final, committedAtUtc: DateTime.UtcNow));

        state.ClearTranslationHistory();

        Assert.NotNull(state.ActiveTranslationLine);
        Assert.Equal("active tagalog partial", state.ActiveTranslationLine!.Text);
        Assert.Single(state.History);
        Assert.Equal("unrelated english", state.History[0].Text);
    }
}
