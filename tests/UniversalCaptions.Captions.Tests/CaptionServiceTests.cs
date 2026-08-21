using System.Linq;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.Captions.Tests;

/// <summary>
/// Verifies <see cref="CaptionService"/> as a pure relay (ADR-0011): source captions flow in via
/// <see cref="ICaptionService.ProcessPartial"/>/<see cref="ICaptionService.ProcessFinal"/>, translated
/// captions are relayed via <see cref="ICaptionService.ProcessPartialTranslation"/>/
/// <see cref="ICaptionService.ProcessFinalTranslation"/> with the disabled/stale-target/stale-session
/// guards, and the session lifecycle/history-scrub transitions behave as documented.
/// </summary>
public sealed class CaptionServiceTests
{
    private static PartialTranscript Partial(long sequence, string text) =>
        new(text, DateTime.UtcNow, DateTime.UtcNow, sequence);

    private static FinalTranscript Final(long sequence, string text) =>
        new(text, DateTime.UtcNow, DateTime.UtcNow, sequence);

    private static PartialTranslation TPartial(long sequence, string text, string target = "tl", DateTime? emittedAtUtc = null) =>
        new(
            sourceText: null,
            translatedText: text,
            sourceLanguage: "en",
            targetLanguage: target,
            capturedAtUtc: DateTime.UtcNow,
            emittedAtUtc: emittedAtUtc ?? DateTime.UtcNow,
            sequence: sequence);

    private static FinalTranslation TFinal(long sequence, string text, string target = "tl", DateTime? emittedAtUtc = null) =>
        new(
            sourceText: null,
            translatedText: text,
            sourceLanguage: "en",
            targetLanguage: target,
            capturedAtUtc: DateTime.UtcNow,
            emittedAtUtc: emittedAtUtc ?? DateTime.UtcNow,
            sequence: sequence,
            committedAtUtc: emittedAtUtc ?? DateTime.UtcNow);

    private static CaptionService CreateService(
        string sourceLanguage = "en",
        string? targetLanguage = "tl",
        int historyCapacity = 50,
        Func<DateTime>? utcNow = null) =>
        new(new CaptionServiceOptions(sourceLanguage, targetLanguage, historyCapacity), utcNow);

    /// <summary>
    /// A deterministic clock whose value the test advances, so live-session boundaries
    /// (which gate translation-origin input) can be asserted exactly.
    /// </summary>
    private sealed class MutableClock
    {
        public DateTime Now { get; set; } = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        public DateTime UtcNow() => Now;
    }

    // ---------------------------------------------------------------------
    // Source-caption ingress
    // ---------------------------------------------------------------------

    [Fact]
    public void ProcessPartial_UpdatesActiveLine_AndRaisesEvents()
    {
        var service = CreateService();
        var events = new List<string>();
        service.ActiveLineChanged += (_, line) => events.Add($"active:{line.Text}");
        service.StateChanged += (_, state) => events.Add($"state:{state.ActiveLine?.Text}");

        service.Start();
        service.ProcessPartial(Partial(1, "hello"));

        Assert.Equal("hello", service.State.ActiveLine?.Text);
        Assert.Equal(CaptionLineState.Active, service.State.ActiveLine!.State);
        Assert.Contains("active:hello", events);
        Assert.Contains("state:hello", events);
    }

    [Fact]
    public void ProcessPartial_BeforeStart_IsIgnored()
    {
        var service = CreateService();
        service.ProcessPartial(Partial(1, "hello"));

        Assert.Null(service.State.ActiveLine);
    }

    [Fact]
    public void ProcessFinal_CommitsFinalLine_AndClearsActive()
    {
        var service = CreateService();
        service.Start();
        service.ProcessPartial(Partial(1, "hello"));

        service.ProcessFinal(Final(2, "hello world"));

        var line = Assert.Single(service.State.History);
        Assert.Equal("hello world", line.Text);
        Assert.Equal(CaptionLineState.Final, line.State);
        Assert.Equal(LineOrigin.SourceStt, line.Origin);
        Assert.Null(service.State.ActiveLine);
    }

    [Fact]
    public void ProcessFinal_BeforeStart_IsIgnored()
    {
        var service = CreateService();
        service.ProcessFinal(Final(1, "hello"));

        Assert.Empty(service.State.History);
    }

    [Fact]
    public void ProcessFinal_RaisesCommittedEvent()
    {
        var service = CreateService();
        var committed = new List<long>();
        service.CaptionLineCommitted += (_, line) => committed.Add(line.Sequence);

        service.Start();
        service.ProcessFinal(Final(5, "hello"));

        Assert.Equal(new long[] { 5 }, committed);
    }

    [Fact]
    public void ProcessFinal_AfterStop_IsIgnored()
    {
        var service = CreateService();
        service.Start();
        service.Stop();

        service.ProcessFinal(Final(1, "hello"));

        Assert.Empty(service.State.History);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        var service = CreateService();
        service.Start();
        service.Start();

        Assert.True(service.IsRunning);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void ProcessPartial_WithEmptyText_DoesNotClearActiveLine(string emptyText)
    {
        var service = CreateService();
        service.Start();
        service.ProcessPartial(Partial(1, "real words"));
        var activeBefore = service.State.ActiveLine!.Text;

        service.ProcessPartial(Partial(2, emptyText));

        Assert.Equal(activeBefore, service.State.ActiveLine!.Text);
    }

    [Fact]
    public void ProcessPartial_StripsBracketedNoiseFromTheLine()
    {
        var service = CreateService();
        service.Start();

        service.ProcessPartial(Partial(1, "[music] hello there"));

        Assert.Equal("hello there", service.State.ActiveLine!.Text);
    }

    [Fact]
    public void ProcessFinal_WithBracketedNoiseOnly_IsNotCommitted()
    {
        var service = CreateService();
        service.Start();

        service.ProcessFinal(Final(1, "[applause]"));
        service.ProcessFinal(Final(2, "   "));

        Assert.Empty(service.State.History);
    }

    // ---------------------------------------------------------------------
    // Final dedup / overlap handling
    // ---------------------------------------------------------------------

    [Fact]
    public void ProcessFinal_ExactDuplicateOfPrevious_IsDropped()
    {
        var service = CreateService();
        service.Start();

        service.ProcessFinal(Final(1, "Magandang umaga sa inyo."));
        service.ProcessFinal(Final(2, "magandang umaga sa inyo."));

        var line = Assert.Single(service.State.History);
        Assert.Equal(1, line.Sequence);
    }

    [Fact]
    public void ProcessFinal_TruncatedRepeatOfPrevious_IsDropped()
    {
        var service = CreateService();
        service.Start();

        service.ProcessFinal(Final(1, "So we're talking about where the space is."));
        service.ProcessFinal(Final(2, "So we're talking about where the space"));

        var line = Assert.Single(service.State.History);
        Assert.Equal(1, line.Sequence);
    }

    [Fact]
    public void ProcessFinal_ExtensionOfPrevious_StripsTheOverlap()
    {
        var service = CreateService();
        service.Start();

        service.ProcessFinal(Final(1, "Where the space"));
        service.ProcessFinal(Final(2, "where the space is, what is this whole AI shift"));

        Assert.Equal(2, service.State.History.Count);
        Assert.Equal("is, what is this whole AI shift", service.State.History[^1].Text);
    }

    [Fact]
    public void ProcessFinal_DisjointSentence_IsCommittedVerbatim()
    {
        var service = CreateService();
        service.Start();

        service.ProcessFinal(Final(1, "First sentence."));
        service.ProcessFinal(Final(2, "Second sentence entirely different."));

        Assert.Equal(2, service.State.History.Count);
        Assert.Equal("Second sentence entirely different.", service.State.History[^1].Text);
    }

    // ---------------------------------------------------------------------
    // Translation toggle scrubbing
    // ---------------------------------------------------------------------

    [Fact]
    public void SetTranslationEnabled_False_ScrubsTranslationHistoryAndActiveLine()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(true);
        service.SetLiveTranslationSession(true);

        service.ProcessFinalTranslation(TFinal(1, "Kumusta ka."));
        service.ProcessPartialTranslation(TPartial(2, "Ako ay ayos lang."));
        Assert.NotEmpty(service.State.History);
        Assert.NotNull(service.State.ActiveTranslationLine);

        service.SetTranslationEnabled(false);

        Assert.DoesNotContain(service.State.History, l => l.Origin == LineOrigin.Translation);
        Assert.Null(service.State.ActiveTranslationLine);
        Assert.False(service.State.TranslationEnabled);
    }

    [Fact]
    public void SetTranslationEnabled_TargetChangeWhileOn_ScrubsPreviousTargetHistory()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(true);
        service.SetLiveTranslationSession(true);
        service.ProcessFinalTranslation(TFinal(1, "Kumusta ka.", target: "tl"));

        service.SetTranslationEnabled(true, "ja");

        Assert.DoesNotContain(service.State.History, l => l.Origin == LineOrigin.Translation);
        Assert.Equal("ja", service.State.TargetLanguage);
    }

    [Fact]
    public void SetTranslationEnabled_SameTargetAgain_PreservesHistory()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(true);
        service.SetLiveTranslationSession(true);
        service.ProcessFinalTranslation(TFinal(1, "Kumusta ka.", target: "tl"));

        service.SetTranslationEnabled(true, "tl");

        Assert.Contains(service.State.History, l => l.Origin == LineOrigin.Translation);
    }

    [Fact]
    public void SetTranslationEnabled_False_PreservesSourceHistory()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(true);
        service.SetLiveTranslationSession(true);
        service.ProcessFinal(Final(1, "Source sentence."));
        service.ProcessFinalTranslation(TFinal(2, "Salin."));

        service.SetTranslationEnabled(false);

        Assert.Contains(service.State.History, l => l.Text == "Source sentence.");
    }

    // ---------------------------------------------------------------------
    // Translation relay guards
    // ---------------------------------------------------------------------

    [Fact]
    public void ProcessPartialTranslation_WhenEnabled_UpdatesActiveTranslationLine()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(true);
        service.SetLiveTranslationSession(true);

        service.ProcessPartialTranslation(TPartial(1, "Kumusta"));

        Assert.Equal("Kumusta", service.State.ActiveTranslationLine?.Text);
        Assert.Equal(LineOrigin.Translation, service.State.ActiveTranslationLine!.Origin);
    }

    [Fact]
    public void ProcessFinalTranslation_WhenEnabled_CommitsTranslatedLine()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(true);
        service.SetLiveTranslationSession(true);

        service.ProcessFinalTranslation(TFinal(1, "Kumusta ka."));

        var line = Assert.Single(service.State.History);
        Assert.Equal("Kumusta ka.", line.Text);
        Assert.Equal(LineOrigin.Translation, line.Origin);
        Assert.Equal("tl", line.TargetLanguage);
    }

    [Fact]
    public void ProcessPartialTranslation_WhenDisabled_IsRejected()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(false);

        service.ProcessPartialTranslation(TPartial(1, "Kumusta"));

        Assert.Null(service.State.ActiveTranslationLine);
    }

    [Fact]
    public void ProcessFinalTranslation_WhenDisabled_IsRejected()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(false);

        service.ProcessFinalTranslation(TFinal(1, "Kumusta ka."));

        Assert.Empty(service.State.History);
    }

    [Fact]
    public void ProcessPartialTranslation_StaleTarget_IsRejected()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(true, "tl");
        service.SetLiveTranslationSession(true);

        // An event from an engine still configured for the previous target must not bleed in.
        service.ProcessPartialTranslation(TPartial(1, "Kumusta", target: "ja"));

        Assert.Null(service.State.ActiveTranslationLine);
    }

    [Fact]
    public void ProcessFinalTranslation_BeforeLiveSessionBoundary_IsRejected()
    {
        var clock = new MutableClock();
        var service = CreateService(utcNow: clock.UtcNow);
        service.Start();
        service.SetTranslationEnabled(true);

        // The old session's final arrives just BEFORE the new live session starts.
        DateTime oldEmission = clock.Now.AddSeconds(-5);
        service.SetLiveTranslationSession(true); // boundary = clock.Now

        service.ProcessFinalTranslation(TFinal(1, "Lumang salin.", emittedAtUtc: oldEmission));

        Assert.Empty(service.State.History);
    }

    [Fact]
    public void ProcessFinalTranslation_AfterLiveSessionBoundary_IsAccepted()
    {
        var clock = new MutableClock();
        var service = CreateService(utcNow: clock.UtcNow);
        service.Start();
        service.SetTranslationEnabled(true);
        service.SetLiveTranslationSession(true); // boundary = clock.Now

        clock.Now = clock.Now.AddSeconds(5);
        service.ProcessFinalTranslation(TFinal(1, "Bagong salin.", emittedAtUtc: clock.Now));

        var line = Assert.Single(service.State.History);
        Assert.Equal("Bagong salin.", line.Text);
    }

    [Fact]
    public void ProcessPartialTranslation_EmptyText_DoesNotClearActiveLine()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(true);
        service.SetLiveTranslationSession(true);
        service.ProcessPartialTranslation(TPartial(1, "May laman"));

        service.ProcessPartialTranslation(TPartial(2, "   "));

        Assert.Equal("May laman", service.State.ActiveTranslationLine?.Text);
    }

    // ---------------------------------------------------------------------
    // Live-translation active-line clearing
    // ---------------------------------------------------------------------

    [Fact]
    public void ClearLiveTranslationActiveLine_RemovesTheActiveTranslationLine()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(true);
        service.SetLiveTranslationSession(true);
        service.ProcessPartialTranslation(TPartial(1, "Kumusta"));

        service.ClearLiveTranslationActiveLine();

        Assert.Null(service.State.ActiveTranslationLine);
    }

    [Fact]
    public void ClearLiveTranslationActiveLine_WhenDisabled_IsNoOp()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(false);

        service.ClearLiveTranslationActiveLine();

        Assert.Null(service.State.ActiveTranslationLine);
    }

    // ---------------------------------------------------------------------
    // Content resets
    // ---------------------------------------------------------------------

    [Fact]
    public void ResetTranslatedContent_ClearsTranslationContentButKeepsSource()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(true);
        service.SetLiveTranslationSession(true);
        service.ProcessFinal(Final(1, "Source stays."));
        service.ProcessFinalTranslation(TFinal(2, "Salin nawawala."));
        service.ProcessPartialTranslation(TPartial(3, "Aktibong salin."));

        service.ResetTranslatedContent();

        Assert.Contains(service.State.History, l => l.Text == "Source stays.");
        Assert.DoesNotContain(service.State.History, l => l.Origin == LineOrigin.Translation);
        Assert.Null(service.State.ActiveTranslationLine);
    }

    [Fact]
    public void ClearTranslationHistory_RemovesOnlyTranslationEntries()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(true);
        service.SetLiveTranslationSession(true);
        service.ProcessFinal(Final(1, "Source stays."));
        service.ProcessFinalTranslation(TFinal(2, "Salin nawawala."));

        service.ClearTranslationHistory();

        var remaining = Assert.Single(service.State.History);
        Assert.Equal("Source stays.", remaining.Text);
    }

    [Fact]
    public void ClearCaptionContent_ClearsEverything()
    {
        var service = CreateService();
        service.Start();
        service.SetTranslationEnabled(true);
        service.SetLiveTranslationSession(true);
        service.ProcessFinal(Final(1, "Source."));
        service.ProcessFinalTranslation(TFinal(2, "Salin."));
        service.ProcessPartial(Partial(3, "aktibo"));

        service.ClearCaptionContent();

        Assert.Empty(service.State.History);
        Assert.Null(service.State.ActiveLine);
        Assert.Null(service.State.ActiveTranslationLine);
    }

    [Fact]
    public void GetSnapshot_ReflectsLiveSessionFlag()
    {
        var service = CreateService();
        service.Start();

        service.SetLiveTranslationSession(true);
        Assert.True(service.GetSnapshot().IsLiveTranslationSession);

        service.SetLiveTranslationSession(false);
        Assert.False(service.GetSnapshot().IsLiveTranslationSession);
    }

    [Fact]
    public void Stop_ClearsActiveLine_AndRaisesStateChanged()
    {
        var service = CreateService();
        var states = new List<CaptionState>();
        service.StateChanged += (_, state) => states.Add(state);
        service.Start();
        service.ProcessPartial(Partial(1, "hello"));

        service.Stop();

        Assert.Null(service.State.ActiveLine);
        Assert.False(service.IsRunning);
        Assert.NotEmpty(states);
    }

    [Fact]
    public void Dispose_StopsAcceptingInput()
    {
        var service = CreateService();
        service.Start();
        service.Dispose();

        service.ProcessFinal(Final(1, "after dispose"));

        Assert.Empty(service.State.History);
        Assert.False(service.IsRunning);
    }
}
