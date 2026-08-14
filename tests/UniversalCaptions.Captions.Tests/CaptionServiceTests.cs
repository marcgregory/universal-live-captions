using System.Linq;
using UniversalCaptions.Captions.Tests.Support;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.Captions.Tests;

/// <summary>
/// Verifies <see cref="CaptionService"/> partial/final handling, history commits, translation wiring,
/// translation-failure preservation, session lifecycle, and cancellation through deterministic fake
/// translation engines.
/// </summary>
public sealed class CaptionServiceTests
{
    private static PartialTranscript Partial(long sequence, string text) =>
        new(text, DateTime.UtcNow, DateTime.UtcNow, sequence);

    private static FinalTranscript Final(long sequence, string text) =>
        new(text, DateTime.UtcNow, DateTime.UtcNow, sequence);

    private static CaptionService CreateService(
        ITranslationEngine? engine = null,
        string sourceLanguage = "en",
        string? targetLanguage = "tl",
        int historyCapacity = 50,
        Func<DateTime>? utcNow = null,
        TimeSpan? stopDrainBudget = null) =>
        new(new CaptionServiceOptions(sourceLanguage, targetLanguage, historyCapacity), engine, utcNow, stopDrainBudget);

    /// <summary>
    /// A deterministic clock whose value the test advances, so translation start/completion stamps
    /// (which measure end-to-end latency) can be asserted exactly.
    /// </summary>
    private sealed class MutableClock
    {
        public DateTime Now { get; set; } = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        public DateTime UtcNow() => Now;
    }

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
        var service = CreateService(engine: null);
        service.Start();
        service.ProcessPartial(Partial(1, "hello"));

        service.ProcessFinal(Final(2, "hello world"));

        var line = Assert.Single(service.State.History);
        Assert.Equal("hello world", line.Text);
        Assert.Equal(CaptionLineState.Final, line.State);
        Assert.Equal(CaptionTranslationStatus.NotRequested, line.TranslationStatus);
        Assert.Null(service.State.ActiveLine);
    }

    [Fact]
    public void ProcessFinal_BeforeStart_IsIgnored()
    {
        var service = CreateService(engine: null);
        service.ProcessFinal(Final(1, "hello"));

        Assert.Empty(service.State.History);
    }

    [Fact]
    public void ProcessFinal_RaisesCommittedEvent()
    {
        var service = CreateService(engine: null);
        var committed = new List<long>();
        service.CaptionLineCommitted += (_, line) => committed.Add(line.Sequence);

        service.Start();
        service.ProcessFinal(Final(5, "hello"));

        Assert.Equal(new long[] { 5 }, committed);
    }

    [Fact]
    public void ProcessFinal_AfterStop_IsIgnored()
    {
        var service = CreateService(engine: null);
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

    [Fact]
    public async Task ProcessFinal_WithTranslationEnabled_TranslatesLine()
    {
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessFinal(Final(2, "hello world"));
        await service.FlushAsync();

        var line = Assert.Single(service.State.History);
        Assert.Equal("hello world", line.Text);
        Assert.Equal("hello world!", line.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus);
        Assert.Equal("tl", line.TargetLanguage);

        var request = Assert.Single(engine.Requests);
        Assert.Equal("hello world", request.Text);
        Assert.Equal("tl", request.Target);
    }

    [Fact]
    public async Task SetTranslationEnabled_ExplicitTargetOverridesOptions()
    {
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.Start();
        service.SetTranslationEnabled(true, "ja");

        service.ProcessFinal(Final(2, "hello"));
        await service.FlushAsync();

        var line = Assert.Single(service.State.History);
        Assert.Equal("ja", line.TargetLanguage);
        Assert.Equal("ja", Assert.Single(engine.Requests).Target);
    }

    [Fact]
    public void SetTranslationEnabled_WithoutConfiguredTarget_Throws()
    {
        var service = CreateService(targetLanguage: null);
        service.Start();

        Assert.Throws<ArgumentException>(() => service.SetTranslationEnabled(true));
    }

    [Fact]
    public void SetTranslationEnabled_NormalizesExplicitTarget()
    {
        var service = CreateService(engine: null, targetLanguage: null);
        service.Start();

        service.SetTranslationEnabled(true, " TL ");

        Assert.True(service.State.TranslationEnabled);
        Assert.Equal("tl", service.State.TargetLanguage);
    }

    [Fact]
    public void ProcessFinal_TranslationDisabled_MakesNoTranslationRequest()
    {
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.Start();

        service.ProcessFinal(Final(2, "hello world"));

        Assert.Empty(engine.Requests);
        var line = Assert.Single(service.State.History);
        Assert.Equal(CaptionTranslationStatus.NotRequested, line.TranslationStatus);
        Assert.Null(line.TranslatedText);
    }

    [Fact]
    public void ProcessFinal_TranslationEnabledWithoutEngine_LeavesLineNotRequested()
    {
        var service = CreateService(engine: null);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessFinal(Final(2, "hello world"));

        var line = Assert.Single(service.State.History);
        Assert.Equal(CaptionTranslationStatus.NotRequested, line.TranslationStatus);
        Assert.Equal("hello world", line.Text);
    }

    [Fact]
    public async Task ProcessFinal_TranslationFailure_PreservesSourceText()
    {
        var engine = StubTranslationEngine.Failure(TranslationErrorKind.EngineUnavailable, "python missing");
        var service = CreateService(engine);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessFinal(Final(2, "hello world"));
        await service.FlushAsync();

        var line = Assert.Single(service.State.History);
        Assert.Equal("hello world", line.Text);
        Assert.Null(line.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.Failed, line.TranslationStatus);
        Assert.Contains("python missing", line.TranslationErrorMessage);
    }

    [Fact]
    public async Task ProcessFinal_UnexpectedEngineException_DoesNotBreakPipeline()
    {
        var engine = StubTranslationEngine.Unexpected(new InvalidOperationException("boom"));
        var service = CreateService(engine);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessFinal(Final(2, "hello"));
        await service.FlushAsync();

        var line = Assert.Single(service.State.History);
        Assert.Equal("hello", line.Text);
        Assert.Equal(CaptionTranslationStatus.Failed, line.TranslationStatus);
        Assert.Contains("boom", line.TranslationErrorMessage);
    }

    [Fact]
    public async Task ProcessFinal_DelayedTranslation_AppliesWhenComplete()
    {
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessFinal(Final(2, "hello"));

        var pending = Assert.Single(service.State.History);
        Assert.Equal(CaptionTranslationStatus.Pending, pending.TranslationStatus);

        engine.CompleteLatest("kumusta", "tl");
        await service.FlushAsync();

        var line = Assert.Single(service.State.History);
        Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus);
        Assert.Equal("kumusta", line.TranslatedText);
    }

    [Fact]
    public async Task ProcessFinal_StaleTranslationResult_DoesNotOverwriteReDeliveredLine()
    {
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessFinal(Final(2, "first"));
        service.ProcessFinal(Final(2, "second"));

        Assert.Equal(2, engine.RequestCount);
        engine.Complete(0, "first!", "tl");
        engine.Complete(1, "second!", "tl");
        await service.FlushAsync();

        var line = Assert.Single(service.State.History);
        Assert.Equal("second", line.Text);
        Assert.Equal("second!", line.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus);
    }

    [Fact]
    public async Task ProcessFinal_TranslationCompletion_RaisesUpdatedEvent()
    {
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        var updated = new List<CaptionTranslationStatus>();
        service.CaptionLineUpdated += (_, line) => updated.Add(line.TranslationStatus);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessFinal(Final(5, "hello"));
        engine.CompleteLatest("kumusta", "tl");
        await service.FlushAsync();

        Assert.Equal(CaptionTranslationStatus.Completed, Assert.Single(updated));
    }

    [Fact]
    public async Task Stop_WithInFlightCommittedFinal_IsDrainedAndApplied()
    {
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        service.Start();
        service.SetTranslationEnabled(true);
        service.ProcessFinal(Final(2, "hello"));

        // Stop returns immediately and does not cancel the already-committed final: it is applied once
        // its translation completes, so captions recognized just before the stop are not dropped.
        service.Stop();
        engine.Complete(0, "kumusta", "tl");
        await service.FlushAsync();

        Assert.False(service.IsRunning);
        var line = Assert.Single(service.State.History);
        Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus);
        Assert.Equal("kumusta", line.TranslatedText);
    }

    [Fact]
    public async Task Stop_WithCommittedTranslationBeyondDrainBudget_ForceCancelsRemaining()
    {
        // A gated engine that never returns, plus a tiny drain budget, means the background drain
        // must force-cancel the remaining in-flight work rather than wait on it forever.
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine, stopDrainBudget: TimeSpan.FromMilliseconds(50));
        service.Start();
        service.SetTranslationEnabled(true);
        service.ProcessFinal(Final(2, "hello"));

        service.Stop();
        await service.FlushAsync();

        Assert.False(service.IsRunning);
        Assert.Equal(CaptionTranslationStatus.Pending, Assert.Single(service.State.History).TranslationStatus);
    }

    [Fact]
    public async Task Reset_CancelsInFlightTranslation_AndClearsState()
    {
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        service.Start();
        service.SetTranslationEnabled(true);
        service.ProcessPartial(Partial(1, "hello"));
        service.ProcessFinal(Final(2, "hello world"));

        service.Reset();
        await service.FlushAsync();

        Assert.False(service.IsRunning);
        Assert.Null(service.State.ActiveLine);
        Assert.Empty(service.State.History);
        Assert.False(service.State.TranslationEnabled);
    }

    [Fact]
    public void Dispose_StopsTheService()
    {
        var service = CreateService();
        service.Start();
        Assert.True(service.IsRunning);

        service.Dispose();

        Assert.False(service.IsRunning);
        service.ProcessFinal(Final(1, "hello"));
        Assert.Empty(service.State.History);
    }

    [Fact]
    public async Task ProcessFinal_HistoryBounded_KeepsNewest()
    {
        var service = CreateService(engine: null, historyCapacity: 2);
        service.Start();
        service.ProcessFinal(Final(1, "one"));
        service.ProcessFinal(Final(2, "two"));
        service.ProcessFinal(Final(3, "three"));
        await service.FlushAsync();

        Assert.Equal(new long[] { 2, 3 }, service.State.History.Select(line => line.Sequence));
    }

    [Fact]
    public async Task ProcessPartial_WithTranslationEnabled_TranslatesActiveLine()
    {
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessPartial(Partial(1, "hello"));
        await service.FlushAsync();

        var line = service.State.ActiveLine;
        Assert.NotNull(line);
        Assert.Equal("hello", line.Text);
        Assert.Equal("hello!", line.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus);
        Assert.Equal("tl", line.TargetLanguage);
        Assert.Equal("tl", Assert.Single(engine.Requests).Target);
    }

    [Fact]
    public async Task ProcessPartial_WithTranslationEnabled_DoesNotPublishSourcePartial()
    {
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        var activeLineStatuses = new List<CaptionTranslationStatus>();
        service.ActiveLineChanged += (_, line) => activeLineStatuses.Add(line.TranslationStatus);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessPartial(Partial(1, "hello"));
        await service.FlushAsync();

        // ActiveLineChanged must never surface the raw-English partial when translation is on:
        // the event is suppressed entirely until the translation completes. The overlay must
        // never flash the source language.
        Assert.Empty(activeLineStatuses);

        // The state itself carries the completed translation.
        var line = service.State.ActiveLine;
        Assert.NotNull(line);
        Assert.Equal("hello", line.Text);
        Assert.Equal("hello!", line.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus);
    }

    [Fact]
    public async Task ProcessPartial_TranslationDisabled_StillPublishesSourcePartial()
    {
        var service = CreateService(engine: null);
        var published = new List<string?>();
        service.ActiveLineChanged += (_, line) => published.Add(line.Text);
        service.Start();

        service.ProcessPartial(Partial(1, "hello"));

        Assert.Contains(published, text => text == "hello");
    }

    [Fact]
    public async Task ProcessPartial_TranslationDisabled_MakesNoActiveLineRequest()
    {
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.Start();

        service.ProcessPartial(Partial(1, "hello"));
        await service.FlushAsync();

        Assert.Empty(engine.Requests);
        var line = service.State.ActiveLine;
        Assert.NotNull(line);
        Assert.Equal(CaptionTranslationStatus.NotRequested, line.TranslationStatus);
        Assert.Null(line.TranslatedText);
    }

    [Fact]
    public async Task ProcessPartial_ActiveLineTranslationFailure_PreservesSource()
    {
        var engine = StubTranslationEngine.Failure(TranslationErrorKind.EngineUnavailable, "python missing");
        var service = CreateService(engine);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessPartial(Partial(1, "hello"));
        await service.FlushAsync();

        var line = service.State.ActiveLine;
        Assert.NotNull(line);
        Assert.Equal("hello", line.Text);
        Assert.Null(line.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.Failed, line.TranslationStatus);
        Assert.Contains("python missing", line.TranslationErrorMessage);
    }

    [Fact]
    public async Task ProcessPartial_SingleSlot_NewerPartialTranslatedAfterSlotCompletes()
    {
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessPartial(Partial(1, "hel"));
        service.ProcessPartial(Partial(2, "hello"));

        // The first request is still in flight, so the newer partial is not translated yet.
        Assert.Equal(1, engine.RequestCount);

        engine.Complete(0, "hel!", "tl");
        await WaitForAsync(() => engine.RequestCount == 2);
        engine.CompleteLatest("hello!", "tl");
        await service.FlushAsync();

        Assert.Equal(2, engine.RequestCount);
        var line = service.State.ActiveLine;
        Assert.NotNull(line);
        Assert.Equal("hello", line.Text);
        Assert.Equal("hello!", line.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus);
    }

    [Fact]
    public async Task ProcessPartial_StaleActiveLineResult_IsDiscarded()
    {
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        var updated = new List<string?>();
        service.CaptionLineUpdated += (_, line) => updated.Add(line.TranslatedText);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessPartial(Partial(1, "hel"));
        service.ProcessPartial(Partial(2, "hello"));

        // The stale result for the older partial is discarded and never surfaced; only the newer
        // partial's own translation is applied.
        engine.Complete(0, "hel!", "tl");
        await WaitForAsync(() => engine.RequestCount == 2);
        engine.CompleteLatest("hello!", "tl");
        await service.FlushAsync();

        Assert.Equal(new string?[] { "hello!" }, updated);
        var line = service.State.ActiveLine;
        Assert.NotNull(line);
        Assert.Equal("hello", line.Text);
        Assert.Equal("hello!", line.TranslatedText);
    }

    [Fact]
    public async Task ProcessPartial_TranslationCompleted_ActiveLineClearedOnCommit_StaleDiscarded()
    {
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessPartial(Partial(1, "hello"));
        service.ProcessFinal(Final(2, "hello world"));

        // The active-line translation (request 0) completes after the line was committed, so its
        // result is stale and must be discarded; the committed line's own translation still applies.
        engine.Complete(0, "kumusta!", "tl");
        engine.Complete(1, "magandang mundo", "tl");
        await service.FlushAsync();

        Assert.Null(service.State.ActiveLine);
        var final = Assert.Single(service.State.History);
        Assert.Equal("hello world", final.Text);
        Assert.Equal("magandang mundo", final.TranslatedText);
    }

    [Fact]
    public async Task ProcessPartial_TranslationDisabledWhileInFlight_DiscardsResult()
    {
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessPartial(Partial(1, "hello"));
        service.SetTranslationEnabled(false);

        engine.CompleteLatest("kumusta", "tl");
        await service.FlushAsync();

        var line = service.State.ActiveLine;
        Assert.NotNull(line);
        Assert.Equal("hello", line.Text);
        Assert.Null(line.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.NotRequested, line.TranslationStatus);
    }

    [Fact]
    public async Task SetTranslationEnabled_WithActiveLinePresent_TranslatesIt()
    {
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.Start();

        service.ProcessPartial(Partial(1, "hello"));
        Assert.Empty(engine.Requests);

        service.SetTranslationEnabled(true);
        await service.FlushAsync();

        var line = service.State.ActiveLine;
        Assert.NotNull(line);
        Assert.Equal("hello!", line.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus);
    }

    [Fact]
    public async Task ProcessPartial_ActiveLineTranslationCompletion_RaisesUpdatedEvent()
    {
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        var updated = new List<CaptionTranslationStatus>();
        service.CaptionLineUpdated += (_, line) => updated.Add(line.TranslationStatus);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessPartial(Partial(1, "hello"));
        engine.CompleteLatest("kumusta", "tl");
        await service.FlushAsync();

        Assert.Equal(CaptionTranslationStatus.Completed, Assert.Single(updated));
    }

    [Fact]
    public async Task ProcessFinal_TranslationSuccess_StampsTranslationTimestamps()
    {
        var clock = new MutableClock();
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine, utcNow: clock.UtcNow);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessFinal(Final(2, "hello"));
        clock.Now = clock.Now.AddMilliseconds(500);
        engine.CompleteLatest("kumusta", "tl");
        await service.FlushAsync();

        var line = Assert.Single(service.State.History);
        Assert.Equal("kumusta", line.TranslatedText);
        Assert.NotNull(line.TranslationStartedAtUtc);
        Assert.NotNull(line.TranslationCompletedAtUtc);
        Assert.Equal(clock.Now.AddMilliseconds(-500), line.TranslationStartedAtUtc);
        Assert.Equal(clock.Now, line.TranslationCompletedAtUtc);
        Assert.Equal(TimeSpan.FromMilliseconds(500), line.TranslationCompletedAtUtc!.Value - line.TranslationStartedAtUtc!.Value);
    }

    [Fact]
    public async Task ProcessPartial_TranslationSuccess_StampsLiveTranslationTimestamps()
    {
        var clock = new MutableClock();
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine, utcNow: clock.UtcNow);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessPartial(Partial(1, "hello"));
        clock.Now = clock.Now.AddMilliseconds(300);
        engine.CompleteLatest("kumusta", "tl");
        await service.FlushAsync();

        var line = service.State.ActiveLine;
        Assert.NotNull(line);
        Assert.Equal("kumusta", line.TranslatedText);
        Assert.NotNull(line.TranslationStartedAtUtc);
        Assert.NotNull(line.TranslationCompletedAtUtc);
        Assert.Equal(clock.Now.AddMilliseconds(-300), line.TranslationStartedAtUtc);
        Assert.Equal(clock.Now, line.TranslationCompletedAtUtc);
    }

    [Fact]
    public async Task ProcessFinal_TranslationFailure_StampsStartButNotCompletion()
    {
        var clock = new MutableClock();
        var engine = StubTranslationEngine.Failure(TranslationErrorKind.EngineUnavailable, "python missing");
        var service = CreateService(engine, utcNow: clock.UtcNow);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessFinal(Final(2, "hello"));
        clock.Now = clock.Now.AddMilliseconds(250);
        await service.FlushAsync();

        var line = Assert.Single(service.State.History);
        Assert.Equal(CaptionTranslationStatus.Failed, line.TranslationStatus);
        Assert.Equal(clock.Now.AddMilliseconds(-250), line.TranslationStartedAtUtc);
        Assert.Null(line.TranslationCompletedAtUtc);
    }

    [Fact]
    public async Task ProcessPartial_StaleResult_ProducesNoTimestampsOrUpdate()
    {
        var clock = new MutableClock();
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine, utcNow: clock.UtcNow);
        var updated = new List<string?>();
        service.CaptionLineUpdated += (_, line) => updated.Add(line.TranslatedText);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessPartial(Partial(1, "hel"));
        service.ProcessPartial(Partial(2, "hello"));

        clock.Now = clock.Now.AddMilliseconds(400);
        engine.Complete(0, "hel!", "tl");
        await WaitForAsync(() => engine.RequestCount == 2);

        // The stale result (the older partial, superseded before its translation completed) is
        // discarded: no update is surfaced and the current active line carries no timestamps yet.
        Assert.Empty(updated);
        var current = service.State.ActiveLine;
        Assert.NotNull(current);
        Assert.Equal("hello", current.Text);
        Assert.Null(current.TranslationStartedAtUtc);
        Assert.Null(current.TranslationCompletedAtUtc);

        engine.CompleteLatest("hello!", "tl");
        await service.FlushAsync();

        Assert.Equal(new string?[] { "hello!" }, updated);
        var final = service.State.ActiveLine;
        Assert.NotNull(final);
        Assert.Equal("hello!", final.TranslatedText);
        Assert.NotNull(final.TranslationStartedAtUtc);
        Assert.NotNull(final.TranslationCompletedAtUtc);
    }

    [Fact]
    public async Task ProcessPartial_TranslationDisabledMidFlight_ProducesNoTimestampsOrUpdate()
    {
        var clock = new MutableClock();
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine, utcNow: clock.UtcNow);
        var updated = new List<string?>();
        service.CaptionLineUpdated += (_, line) => updated.Add(line.TranslatedText);
        service.Start();
        service.SetTranslationEnabled(true);

        service.ProcessPartial(Partial(1, "hello"));
        service.SetTranslationEnabled(false);

        clock.Now = clock.Now.AddMilliseconds(200);
        engine.CompleteLatest("kumusta", "tl");
        await service.FlushAsync();

        Assert.Empty(updated);
        var line = service.State.ActiveLine;
        Assert.NotNull(line);
        Assert.Null(line.TranslatedText);
        Assert.Null(line.TranslationStartedAtUtc);
        Assert.Null(line.TranslationCompletedAtUtc);
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CaptionService(null!));
    }

    [Fact]
    public void Options_NullSourceLanguage_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CaptionServiceOptions(null!));
    }

    [Fact]
    public async Task SetCaptionLineTranslation_False_SuppressesActiveLineTranslation()
    {
        // Gemini owns translation: the caption service's Argos caption-line path is suppressed even
        // though the common translation state is ON — source lines must never be re-translated.
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.SetCaptionLineTranslation(false);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessPartial(Partial(1, "hello"));
        await service.FlushAsync();

        Assert.Empty(engine.Requests);
        CaptionLine line = service.State.ActiveLine!;
        Assert.Equal("hello", line.Text);
        Assert.Equal(CaptionTranslationStatus.NotRequested, line.TranslationStatus);
    }

    [Fact]
    public async Task SetCaptionLineTranslation_False_SuppressesCommittedFinalTranslation()
    {
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.SetCaptionLineTranslation(false);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessFinal(Final(1, "hello"));
        await service.FlushAsync();

        Assert.Empty(engine.Requests);
        CaptionLine line = Assert.Single(service.State.History);
        Assert.Equal("hello", line.Text);
        Assert.Equal(CaptionTranslationStatus.NotRequested, line.TranslationStatus);
    }

    [Fact]
    public async Task SetCaptionLineTranslation_True_RestoresCaptionLinePath()
    {
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.SetCaptionLineTranslation(false);
        service.SetCaptionLineTranslation(true);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessPartial(Partial(1, "hello"));
        await service.FlushAsync();

        CaptionLine line = service.State.ActiveLine!;
        Assert.Equal("hello!", line.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus);
    }

    [Fact]
    public void SetCaptionLineTranslation_False_StillRelaysTranslationOriginLines()
    {
        // The caption-line path is suppressed, but translation-origin lines (from the live Gemini
        // engine) must still flow through when the common translation state is on.
        var service = CreateService();
        service.SetCaptionLineTranslation(false);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessPartialTranslation(new PartialTranslation(
            null, "pupunta na", "en", "tl", DateTime.UtcNow, DateTime.UtcNow, 5));

        CaptionLine active = service.State.ActiveTranslationLine!;
        Assert.Equal("pupunta na", active.Text);
        Assert.Equal(LineOrigin.Translation, active.Origin);
    }

    [Fact]
    public async Task Provider_switch_live_to_caption_line_preserves_target_and_continues_translation()
    {
        // The reported regression: Gemini EN → TL, then switch the provider to Argos WITHOUT touching
        // the language selection. The provider switch must only change the provider — the selected
        // target language (tl) must remain, and new captions must keep being translated into it (via
        // the caption-line path), never fall back to English.
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.SetCaptionLineTranslation(false); // Gemini owns translation
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessFinal(Final(1, "hello"));
        await service.FlushAsync();
        Assert.Empty(engine.Requests); // Gemini session: caption-line path suppressed.

        service.SetCaptionLineTranslation(true); // Argos takes over translation.
        service.SetTranslationEnabled(true, "tl"); // Same target — a no-op on the language axis.
        Assert.Equal("tl", service.State.TargetLanguage);

        service.ProcessFinal(Final(2, "world"));
        await service.FlushAsync();

        (string Text, string? Source, string Target) request = Assert.Single(engine.Requests);
        Assert.Equal("world", request.Text);
        Assert.Equal("tl", request.Target);
        Assert.Equal("tl", service.State.TargetLanguage);
        CaptionLine line = Assert.Single(service.State.History, l => l.Sequence == 2);
        Assert.Equal("world!", line.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus);
    }

    [Fact]
    public async Task Provider_switch_caption_line_to_live_preserves_target()
    {
        // The mirror direction: Argos EN → TL → Gemini must still be EN → TL. The target survives the
        // provider switch, and after Gemini owns translation the caption-line path is suppressed.
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.SetCaptionLineTranslation(true); // Argos owns translation.
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessFinal(Final(1, "hello"));
        await service.FlushAsync();
        (string Text, string? Source, string Target) argosRequest = Assert.Single(engine.Requests);
        Assert.Equal("tl", argosRequest.Target);

        service.SetCaptionLineTranslation(false); // Gemini takes over translation.
        service.SetTranslationEnabled(true, "tl"); // Same target — no-op on the language axis.
        Assert.Equal("tl", service.State.TargetLanguage);

        service.ProcessFinal(Final(2, "world"));
        await service.FlushAsync();

        Assert.Single(engine.Requests); // No new caption-line request after the switch.
        Assert.Equal("tl", service.State.TargetLanguage);
        CaptionLine line = Assert.Single(service.State.History, l => l.Sequence == 2);
        Assert.Equal(CaptionTranslationStatus.NotRequested, line.TranslationStatus);
    }

    [Fact]
    public void ClearCaptionContent_ClearsContent_KeepsTranslationConfiguration()
    {
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.SetCaptionLineTranslation(false);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessPartial(Partial(1, "hello"));
        Assert.NotNull(service.State.ActiveLine);

        service.ProcessPartialTranslation(new PartialTranslation(
            null, "pupunta na", "en", "tl", DateTime.UtcNow, DateTime.UtcNow, 5));
        Assert.NotNull(service.State.ActiveTranslationLine);

        service.ProcessFinal(Final(2, "world"));
        Assert.NotEmpty(service.State.History);

        service.ClearCaptionContent();

        Assert.Empty(service.State.History);
        Assert.Null(service.State.ActiveLine);
        Assert.Null(service.State.ActiveTranslationLine);
        Assert.True(service.State.TranslationEnabled);
        Assert.Equal("tl", service.State.TargetLanguage);
        Assert.True(service.IsRunning);

        service.ProcessPartial(Partial(3, "next"));
        Assert.Equal("next", service.State.ActiveLine?.Text);
    }

    [Fact]
    public void SetLiveTranslationSession_ReflectsInSnapshot()
    {
        var service = CreateService();
        service.Start();

        Assert.False(service.GetSnapshot().IsLiveTranslationSession);

        service.SetLiveTranslationSession(true);
        Assert.True(service.GetSnapshot().IsLiveTranslationSession);

        service.SetLiveTranslationSession(false);
        Assert.False(service.GetSnapshot().IsLiveTranslationSession);
    }

    [Fact]
    public void SetTranslationEnabled_False_DropsTranslationOriginContent()
    {
        // Toggling translation off must stop the translation lineage immediately: a stale live-engine
        // event racing the toggle is ignored, and a previously-set active translation line is cleared.
        var service = CreateService();
        service.SetCaptionLineTranslation(false);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessPartialTranslation(new PartialTranslation(
            null, "pupunta na", "en", "tl", DateTime.UtcNow, DateTime.UtcNow, 5));
        Assert.NotNull(service.State.ActiveTranslationLine);

        service.SetTranslationEnabled(false);

        Assert.False(service.State.TranslationEnabled);
        Assert.Null(service.State.ActiveTranslationLine);

        service.ProcessPartialTranslation(new PartialTranslation(
            null, "hindi dapat", "en", "tl", DateTime.UtcNow, DateTime.UtcNow, 6));
        service.ProcessFinalTranslation(new FinalTranslation(
            null, "hindi dapat", "en", "tl", DateTime.UtcNow, DateTime.UtcNow, 6, DateTime.UtcNow));

        Assert.Null(service.State.ActiveTranslationLine);
        Assert.Empty(service.State.History);
    }

    [Fact]
    public void ProcessPartialTranslation_WithTranslationOff_IsIgnored()
    {
        // Translation was never enabled: translation-origin lines are not accepted at all.
        var service = CreateService();
        service.SetCaptionLineTranslation(false);
        service.Start();

        service.ProcessPartialTranslation(new PartialTranslation(
            null, "pupunta na", "en", "tl", DateTime.UtcNow, DateTime.UtcNow, 5));

        Assert.Null(service.State.ActiveTranslationLine);
    }

    [Fact]
    public void SetTranslationEnabled_False_RemovesTranslationOriginHistory_KeepsSourceHistory()
    {
        // v0.5.37 fix for the "mixed English + Tagalog after toggling Translate OFF" symptom.
        // The committed history must contain ONLY LineOrigin.SourceStt entries after the toggle;
        // every LineOrigin.Translation entry (Tagalog, Japanese, etc.) is scrubbed regardless of
        // target language. Existing English/source history is preserved.
        var service = CreateService();
        service.SetCaptionLineTranslation(false);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        // Seed a mixed history: English source partials (will commit when finals arrive) interleaved
        // with translation-origin finals from the live engine. The caption-line path is suppressed,
        // so the source commits are pure English and the translation commits are pure target text.
        service.ProcessPartial(Partial(1, "english one"));
        service.ProcessFinal(Final(1, "english one"));
        service.ProcessFinalTranslation(new FinalTranslation(
            null, "tagalog one", "en", "tl",
            DateTime.UtcNow, DateTime.UtcNow, 2, committedAtUtc: DateTime.UtcNow));
        service.ProcessFinal(Final(3, "english two"));
        service.ProcessFinalTranslation(new FinalTranslation(
            null, "tagalog two", "en", "tl",
            DateTime.UtcNow, DateTime.UtcNow, 4, committedAtUtc: DateTime.UtcNow));

        Assert.Equal(4, service.State.History.Count);
        Assert.Equal(2, service.State.History.Count(c => c.Origin == LineOrigin.Translation));
        Assert.Equal(2, service.State.History.Count(c => c.Origin == LineOrigin.SourceStt));

        service.SetTranslationEnabled(false);

        // After toggle: every translation-origin entry is removed; every source entry survives.
        var history = service.State.History;
        Assert.Equal(2, history.Count);
        Assert.All(history, line => Assert.Equal(LineOrigin.SourceStt, line.Origin));
        Assert.Equal(new[] { "english one", "english two" }, history.Select(line => line.Text));
        Assert.Null(service.State.ActiveTranslationLine);
    }

    [Fact]
    public void SetTranslationEnabled_False_AfterNewCaptions_OnlyNewSourceIsShown_NoMixed()
    {
        // After the toggle, a new Whisper partial/final arrives. The overlay must receive ONLY that
        // English line — no stale Tagalog lines from the previous session should reappear in history.
        var service = CreateService();
        service.SetCaptionLineTranslation(false);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessFinal(Final(1, "english one"));
        service.ProcessFinalTranslation(new FinalTranslation(
            null, "tagalog one", "en", "tl",
            DateTime.UtcNow, DateTime.UtcNow, 2, committedAtUtc: DateTime.UtcNow));

        service.SetTranslationEnabled(false);

        // New Whisper final after the toggle.
        service.ProcessFinal(Final(3, "english two"));

        var history = service.State.History;
        Assert.Equal(2, history.Count);
        Assert.All(history, line => Assert.Equal(LineOrigin.SourceStt, line.Origin));
        Assert.DoesNotContain(history, line => line.Text == "tagalog one");
        Assert.Contains(history, line => line.Text == "english two");
    }

    [Fact]
    public void ClearTranslationHistory_WhenNotRunning_IsNoop()
    {
        // Defensive no-op when the service isn't started: clears must not run before a session exists.
        var service = CreateService();
        // Intentionally do NOT call service.Start().

        var stateChanges = 0;
        service.StateChanged += (_, _) => stateChanges++;

        service.ClearTranslationHistory();

        Assert.Equal(0, stateChanges);
        Assert.Empty(service.State.History);
    }

    [Fact]
    public void ClearTranslationHistory_WhenNothingToClear_DoesNotRaiseStateChanged()
    {
        // The overlay would re-render for no reason if StateChanged fired on a no-op clear. The
        // service returns the cleared-count from CaptionState and gates the event on it being > 0.
        var service = CreateService();
        service.Start();

        var stateChanges = 0;
        service.StateChanged += (_, _) => stateChanges++;

        service.ClearTranslationHistory();

        Assert.Equal(0, stateChanges);
    }

    [Fact]
    public void ClearTranslationHistory_PreservesSourceEntries_AndRaisesStateChanged()
    {
        // Direct exercise of the new API: scrubbing translation history must not touch source entries
        // and must raise exactly one StateChanged when it actually clears something.
        var service = CreateService();
        service.SetCaptionLineTranslation(false);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessFinal(Final(1, "english"));
        service.ProcessFinalTranslation(new FinalTranslation(
            null, "tagalog", "en", "tl",
            DateTime.UtcNow, DateTime.UtcNow, 2, committedAtUtc: DateTime.UtcNow));

        var stateChanges = 0;
        service.StateChanged += (_, _) => stateChanges++;

        service.ClearTranslationHistory();

        Assert.Equal(1, stateChanges);
        var history = service.State.History;
        Assert.Single(history);
        Assert.Equal("english", history[0].Text);
        Assert.Equal(LineOrigin.SourceStt, history[0].Origin);
    }

    [Fact]
    public void SetTranslationEnabled_ChangingTargetLanguageWhileOn_ClearsPreviousTargetHistory()
    {
        // v0.5.37 extension: switching target language (tl → ja) mid-session scrubs the previous
        // target's history so the new session starts clean. SourceStt history is preserved as the
        // shared ground truth across both targets.
        var service = CreateService();
        service.SetCaptionLineTranslation(false);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        // Seed mixed history: two source finals + two Tagalog finals.
        service.ProcessFinal(Final(1, "english one"));
        service.ProcessFinalTranslation(new FinalTranslation(
            null, "tagalog one", "en", "tl",
            DateTime.UtcNow, DateTime.UtcNow, 2, committedAtUtc: DateTime.UtcNow));
        service.ProcessFinal(Final(3, "english two"));
        service.ProcessFinalTranslation(new FinalTranslation(
            null, "tagalog two", "en", "tl",
            DateTime.UtcNow, DateTime.UtcNow, 4, committedAtUtc: DateTime.UtcNow));

        // Switch target language: tl → ja. The previous target's history must be scrubbed.
        service.SetTranslationEnabled(true, "ja");

        var history = service.State.History;
        Assert.Equal(2, history.Count);
        Assert.All(history, line => Assert.Equal(LineOrigin.SourceStt, line.Origin));
        Assert.Equal(new[] { "english one", "english two" }, history.Select(line => line.Text));
        Assert.True(service.State.TranslationEnabled);
        Assert.Equal("ja", service.State.TargetLanguage);
    }

    [Fact]
    public void SetTranslationEnabled_SettingSameTargetWhileOn_DoesNotClearHistory()
    {
        // Setting the same target language again must NOT scrub history. Only an actual change
        // triggers the clear — otherwise every settings-UI re-save would wipe the user's session.
        var service = CreateService();
        service.SetCaptionLineTranslation(false);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessFinal(Final(1, "english one"));
        service.ProcessFinalTranslation(new FinalTranslation(
            null, "tagalog one", "en", "tl",
            DateTime.UtcNow, DateTime.UtcNow, 2, committedAtUtc: DateTime.UtcNow));

        // Same target again — this must be a no-op for history.
        service.SetTranslationEnabled(true, "tl");

        var history = service.State.History;
        Assert.Equal(2, history.Count);
        Assert.Contains(history, line => line.Text == "tagalog one" && line.Origin == LineOrigin.Translation);
        Assert.Contains(history, line => line.Text == "english one" && line.Origin == LineOrigin.SourceStt);
    }

    [Fact]
    public void SetTranslationEnabled_ChangingTargetAfterOff_DoesNotRunClearAgain()
    {
        // Edge: OFF already cleared translation history. A subsequent ON with a different target
        // must NOT error or re-clear (no-op — history is already source-only). Verifies the OFF
        // path and the language-change path are independent.
        var service = CreateService();
        service.SetCaptionLineTranslation(false);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessFinal(Final(1, "english one"));
        service.ProcessFinalTranslation(new FinalTranslation(
            null, "tagalog one", "en", "tl",
            DateTime.UtcNow, DateTime.UtcNow, 2, committedAtUtc: DateTime.UtcNow));

        service.SetTranslationEnabled(false);
        Assert.Single(service.State.History);
        Assert.Equal("english one", service.State.History[0].Text);

        // Different target — but translation is OFF, so the language-change branch must NOT fire
        // (it requires TranslationEnabled == true to detect a change). This is just an ON toggle.
        service.SetTranslationEnabled(true, "ja");
        Assert.Single(service.State.History);
        Assert.Equal("english one", service.State.History[0].Text);
        Assert.Equal("ja", service.State.TargetLanguage);
    }

    [Fact]
    public async Task SetTranslationEnabled_ChangingTargetWhileOn_ResetsArgosTranslatedSourceLines()
    {
        // THE provider-behaviour parity fix: with the caption-line path (Argos), the translation is
        // attached to the SOURCE line (SourceStt + TranslatedText), so a runtime target-language
        // change must scrub that translated text too — not just the Translation-origin lines a live
        // engine (Gemini) makes. The English ground truth of the old lines SURVIVES (stripped back
        // to source) — the checklist's "history reset + English source remains".
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessFinal(Final(1, "english one"));
        await service.FlushAsync();

        CaptionLine line = Assert.Single(service.State.History);
        Assert.Equal(LineOrigin.SourceStt, line.Origin);
        Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus);
        Assert.NotNull(line.TranslatedText);

        service.SetTranslationEnabled(true, "ja");

        CaptionLine after = Assert.Single(service.State.History);
        Assert.Equal(LineOrigin.SourceStt, after.Origin);
        Assert.Equal("english one", after.Text);
        Assert.Equal(CaptionTranslationStatus.NotRequested, after.TranslationStatus);
        Assert.Null(after.TranslatedText);
        Assert.Equal("ja", service.State.TargetLanguage);
    }

    [Fact]
    public async Task SetTranslationEnabled_False_StripsArgosTranslatedSourceLines()
    {
        // THE reported "Argos output mixes Japanese with English" bug: toggling translation OFF must
        // also strip the translated text off Argos-translated SourceStt lines (not only drop
        // Translation-origin lines), so the old target's Japanese can never mix into the new
        // English-only source stream — yet the English ground truth remains.
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessFinal(Final(1, "english one"));
        service.ProcessFinal(Final(3, "english two"));
        await service.FlushAsync();

        Assert.All(service.State.History, line => Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus));
        Assert.All(service.State.History, line => Assert.NotNull(line.TranslatedText));

        service.SetTranslationEnabled(false);

        Assert.False(service.State.TranslationEnabled);
        Assert.Equal(2, service.State.History.Count);
        Assert.All(service.State.History, line =>
        {
            Assert.Equal(LineOrigin.SourceStt, line.Origin);
            Assert.Equal(CaptionTranslationStatus.NotRequested, line.TranslationStatus);
            Assert.Null(line.TranslatedText);
            Assert.Null(line.TranslationErrorMessage);
        });
        Assert.Equal(new[] { "english one", "english two" }, service.State.History.Select(line => line.Text));
    }

    [Fact]
    public async Task CommittedTranslationResult_ForOldTarget_AfterTargetChange_IsDiscarded()
    {
        // An Argos committed-line translation in flight when the target changes must be discarded:
        // applying it would re-mix the old target's output into the new session after the reset.
        // The pending line is reverted to its English source by the reset (double protection with
        // the stale-target guard).
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessFinal(Final(1, "english one"));
        Assert.Equal(1, engine.RequestCount);

        service.SetTranslationEnabled(true, "ja");

        engine.CompleteLatest("luma na", "tl");
        await service.FlushAsync();

        CaptionLine line = Assert.Single(service.State.History);
        Assert.Equal("english one", line.Text);
        Assert.Equal(LineOrigin.SourceStt, line.Origin);
        Assert.Equal(CaptionTranslationStatus.NotRequested, line.TranslationStatus);
        Assert.Null(line.TranslatedText);
        Assert.Equal("ja", service.State.TargetLanguage);
    }

    [Fact]
    public async Task ActiveLineTranslationResult_ForOldTarget_AfterTargetChange_IsDiscarded()
    {
        // Same stale-target guard on the live active-line path: the old target's result is dropped
        // and the in-progress line re-translates to the NEW target instead.
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessPartial(Partial(1, "hello"));
        Assert.Equal(1, engine.RequestCount);

        service.SetTranslationEnabled(true, "ja");

        engine.Complete(0, "kumusta", "tl");
        await WaitForAsync(() => engine.RequestCount >= 2);

        engine.Complete(1, "kumusta po", "ja");
        await service.FlushAsync();

        CaptionLine? line = service.State.ActiveLine;
        Assert.NotNull(line);
        Assert.Equal("kumusta po", line.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus);
    }

    [Fact]
    public void ProcessFinalTranslation_ForOldTarget_IsDropped()
    {
        // Stale-target guard on the live-engine (Gemini) path: a final whose target no longer
        // matches the current session target must not commit (an engine swap races a target change).
        var service = CreateService();
        service.SetCaptionLineTranslation(false);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessFinalTranslation(new FinalTranslation(
            null, "japanese text", "en", "ja",
            DateTime.UtcNow, DateTime.UtcNow, 1, committedAtUtc: DateTime.UtcNow));

        Assert.Empty(service.State.History);
    }

    [Fact]
    public void ProcessPartialTranslation_ForOldTarget_IsDropped()
    {
        var service = CreateService();
        service.SetCaptionLineTranslation(false);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        service.ProcessPartialTranslation(new PartialTranslation(
            null, "japanese text", "en", "ja",
            DateTime.UtcNow, DateTime.UtcNow, 1));

        Assert.Null(service.State.ActiveTranslationLine);
    }

    [Fact]
    public async Task ResetTranslatedContent_RemovesTranslatedHistory_KeepsPureSource_AndClearsActiveLine()
    {
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.SetTranslationEnabled(true, "tl");
        service.Start();

        // Argos-style translated source line.
        service.ProcessFinal(Final(1, "english one"));
        await service.FlushAsync();
        // Gemini-style active translation line.
        service.ProcessPartialTranslation(new PartialTranslation(
            null, "tagalog live", "en", "tl", DateTime.UtcNow, DateTime.UtcNow, 2));

        Assert.Equal(CaptionTranslationStatus.Completed, Assert.Single(service.State.History).TranslationStatus);
        Assert.NotNull(service.State.ActiveTranslationLine);

        service.ResetTranslatedContent();

        CaptionLine source = Assert.Single(service.State.History);
        Assert.Equal(LineOrigin.SourceStt, source.Origin);
        Assert.Equal("english one", source.Text);
        Assert.Equal(CaptionTranslationStatus.NotRequested, source.TranslationStatus);
        Assert.Null(source.TranslatedText);
        Assert.Null(service.State.ActiveTranslationLine);
        Assert.True(service.State.TranslationEnabled);
        Assert.Equal("tl", service.State.TargetLanguage);
    }

    [Fact]
    public async Task Target_change_strips_active_line_translation()
    {
        // THE runtime leak: in the Argos caption-line path the ACTIVE source line carries the
        // in-progress utterance's completed translation. A target change reset only scrubbed the
        // committed history, so the old target's Japanese stayed on screen as the live line. The
        // reset must strip the active line's translation too — the English ground truth survives.
        // A gated engine keeps the post-switch re-translation to the new target in flight, so the
        // assertions prove the old target is gone the instant the reset runs (the runtime window
        // where Argos has not yet delivered the new target's output).
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        service.SetCaptionLineTranslation(true);
        service.SetTranslationEnabled(true, "ja");
        service.Start();

        service.ProcessPartial(Partial(1, "hello"));
        engine.Complete(0, "hello!", "ja");
        await WaitForAsync(() => service.State.ActiveLine?.TranslatedText == "hello!");
        Assert.Equal("ja", service.State.ActiveLine!.TargetLanguage);

        service.SetTranslationEnabled(true, "tl");

        CaptionLine active = service.State.ActiveLine!;
        Assert.Equal("hello", active.Text);
        Assert.Null(active.TranslatedText);
        Assert.Null(active.TargetLanguage);
        Assert.Equal(CaptionTranslationStatus.NotRequested, active.TranslationStatus);
        Assert.Equal("tl", service.State.TargetLanguage);

        // When the in-flight re-translation to the NEW target attaches, it is the new target that
        // lands — the old Japanese is never re-introduced.
        engine.Complete(1, "hello!", "tl");
        await WaitForAsync(() => service.State.ActiveLine?.TargetLanguage == "tl");
        Assert.NotEqual("ja", service.State.ActiveLine!.TargetLanguage);
    }

    [Fact]
    public async Task Toggle_off_strips_active_line_translation()
    {
        // Same leak via the toggle-off path: switching translation OFF while an Argos-translated
        // active line is on screen must not leave the old target's text as the live line.
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.SetCaptionLineTranslation(true);
        service.SetTranslationEnabled(true, "ja");
        service.Start();

        service.ProcessPartial(Partial(1, "hello"));
        await service.FlushAsync();
        Assert.Equal("hello!", service.State.ActiveLine!.TranslatedText);

        service.SetTranslationEnabled(false);

        CaptionLine active = service.State.ActiveLine!;
        Assert.Equal("hello", active.Text);
        Assert.Null(active.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.NotRequested, active.TranslationStatus);
        Assert.False(service.State.TranslationEnabled);
    }

    [Fact]
    public async Task Target_change_sequence_never_leaks_old_language()
    {
        // The user-reported runtime sequence EN → JA → EN → TL: after the final switch to TL, no
        // Japanese may remain anywhere in the state — not in committed history, not on the active
        // line. English source is the ground truth that survives; the translation layer resets and
        // only the NEW target's output may appear on the live line.
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.SetCaptionLineTranslation(true);
        service.SetTranslationEnabled(true, "ja");
        service.Start();

        // EN → JA: a committed final and the in-progress active line are both translated to Japanese.
        service.ProcessFinal(Final(1, "first sentence"));
        await service.FlushAsync();
        Assert.Equal("ja", Assert.Single(service.State.History).TargetLanguage);
        service.ProcessPartial(Partial(2, "second sentence"));
        await service.FlushAsync();
        Assert.Equal("ja", service.State.ActiveLine!.TargetLanguage);

        // JA → EN (target switch): the committed history loses its translation entirely (committed
        // lines are never re-translated) and the active line is stripped then re-translated to the
        // NEW target — Japanese must be gone from everywhere.
        service.SetTranslationEnabled(true, "en");
        Assert.Equal("en", service.State.TargetLanguage);
        Assert.All(service.State.History, line =>
        {
            Assert.Null(line.TranslatedText);
            Assert.Equal(CaptionTranslationStatus.NotRequested, line.TranslationStatus);
        });
        Assert.NotEqual("ja", service.State.ActiveLine!.TargetLanguage);

        // EN → TL: history stays source-only; the active line carries only the new target.
        service.SetTranslationEnabled(true, "tl");
        Assert.Equal("tl", service.State.TargetLanguage);
        Assert.All(service.State.History, line =>
        {
            Assert.Null(line.TranslatedText);
            Assert.NotEqual("ja", line.TargetLanguage);
            Assert.Equal(CaptionTranslationStatus.NotRequested, line.TranslationStatus);
        });
        Assert.NotEqual("ja", service.State.ActiveLine!.TargetLanguage);
        if (service.State.ActiveLine!.TranslatedText is not null)
        {
            Assert.Equal("tl", service.State.ActiveLine!.TargetLanguage);
        }

        // New content in the TL session is translated to Tagalog, never Japanese.
        service.ProcessFinal(Final(3, "third sentence"));
        await service.FlushAsync();
        CaptionLine latest = Assert.Single(service.State.History, l => l.Sequence == 3);
        Assert.Equal("tl", latest.TargetLanguage);
        Assert.NotEqual("ja", latest.TargetLanguage);
    }

    [Fact]
    public async Task Reenable_same_target_discards_inflight_translation_from_before_the_off()
    {
        // THE reported runtime leak (EN → JA → toggle OFF → re-enable JA): an active-line translation
        // still in flight when the toggle-off happened completes AFTER the re-enable. The stale-result
        // guards (TranslationEnabled + target match) are re-armed by the re-enable, and the reset skips
        // a NotRequested in-flight active line (it has no translation to strip) so the same line
        // instance survives — without a session boundary the pre-OFF Japanese lands on the live line.
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        service.SetCaptionLineTranslation(true);
        service.SetTranslationEnabled(true, "ja");
        service.Start();

        // A long utterance's active-line translation is in flight when the toggle-off happens.
        service.ProcessPartial(Partial(1, "hello"));
        Assert.Equal(1, engine.RequestCount);

        service.SetTranslationEnabled(false);
        service.SetTranslationEnabled(true, "ja");

        // Re-enabling with the same target must start a FRESH session: the active line stays source-only.
        CaptionLine active = service.State.ActiveLine!;
        Assert.Equal("hello", active.Text);
        Assert.Equal(CaptionTranslationStatus.NotRequested, active.TranslationStatus);

        // The pre-OFF result arrives after the re-enable — it must be DISCARDED (the fresh session's
        // own request for the same line then starts in its place).
        engine.Complete(0, "hello!", "ja");
        await WaitForAsync(() => engine.RequestCount >= 2);
        active = service.State.ActiveLine!;
        Assert.Equal("hello", active.Text);
        Assert.Null(active.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.NotRequested, active.TranslationStatus);

        // The fresh session's request applies normally — the new session's Japanese, not the old one's.
        engine.Complete(1, "hello!", "ja");
        await WaitForAsync(() => service.State.ActiveLine?.TranslatedText == "hello!");
        Assert.Equal("ja", service.State.ActiveLine!.TargetLanguage);
    }

    [Fact]
    public async Task Target_change_to_source_language_issues_no_translation_request()
    {
        // Spec (target-language change): changing the target to the SOURCE language is
        // passthrough/source-only — no translation request should be issued unnecessarily. The old
        // target's translation is reverted; the active line stays source-only.
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        service.SetCaptionLineTranslation(true);
        service.SetTranslationEnabled(true, "ja");
        service.Start();

        service.ProcessPartial(Partial(1, "hello"));
        Assert.Equal(1, engine.RequestCount);

        service.SetTranslationEnabled(true, "en");

        // The active line stays source-only and NO new request is issued for the passthrough target.
        Assert.Equal("en", service.State.TargetLanguage);
        Assert.Equal(CaptionTranslationStatus.NotRequested, service.State.ActiveLine!.TranslationStatus);
        Assert.Null(service.State.ActiveLine!.TranslatedText);
        Assert.Equal(1, engine.RequestCount);

        // New finals in the passthrough session commit source-only — no request either.
        service.ProcessFinal(Final(2, "second sentence"));
        Assert.Equal(1, engine.RequestCount);
        CaptionLine committed = Assert.Single(service.State.History);
        Assert.Equal(LineOrigin.SourceStt, committed.Origin);
        Assert.Equal(CaptionTranslationStatus.NotRequested, committed.TranslationStatus);
    }

    [Fact]
    public async Task Target_change_without_new_audio_does_not_fabricate_translations()
    {
        // Spec (pause/resume): while no new audio is coming, changing the target still follows session
        // rules — the old target's translations are cleared, but NO new translation is generated merely
        // because the target changed. English source history remains. New source commits then translate
        // to the NEW target.
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        service.SetCaptionLineTranslation(true);
        service.SetTranslationEnabled(true, "ja");
        service.Start();

        service.ProcessFinal(Final(1, "first sentence"));
        Assert.Equal(1, engine.RequestCount);
        engine.Complete(0, "first sentence!", "ja");
        await WaitForAsync(() => service.State.History.Count > 0
            && service.State.History[0].TranslationStatus == CaptionTranslationStatus.Completed);
        Assert.Equal("ja", Assert.Single(service.State.History).TargetLanguage);

        // "Paused": the target changes with no new finals. Old Japanese clears; nothing new appears.
        service.SetTranslationEnabled(true, "tl");
        Assert.Equal("tl", service.State.TargetLanguage);
        Assert.Equal(1, engine.RequestCount);
        CaptionLine committed = Assert.Single(service.State.History);
        Assert.Equal("first sentence", committed.Text);
        Assert.Equal(LineOrigin.SourceStt, committed.Origin);
        Assert.Null(committed.TranslatedText);
        Assert.Equal(CaptionTranslationStatus.NotRequested, committed.TranslationStatus);

        // Resume: new source content commits and translates to the NEW target.
        service.ProcessFinal(Final(2, "second sentence"));
        Assert.Equal(2, engine.RequestCount);
        engine.Complete(1, "pangalawang pangungusap!", "tl");
        await WaitForAsync(() => service.State.History.Count >= 2
            && service.State.History[1].TranslationStatus == CaptionTranslationStatus.Completed);
        CaptionLine resumed = Assert.Single(service.State.History, l => l.Sequence == 2);
        Assert.Equal("tl", resumed.TargetLanguage);
        Assert.Equal("pangalawang pangungusap!", resumed.TranslatedText);
    }

    [Fact]
    public async Task Provider_change_reset_keeps_target_and_english_source_history()
    {
        // Spec (provider switch): the reset clears/reverts ONLY the translated content while keeping
        // TranslationEnabled + TargetLanguage AND the English source history — source is persistent
        // ground truth, translation state is disposable and session-scoped. The provider switch then
        // starts a fresh session under the new provider with the same target.
        var engine = StubTranslationEngine.Success();
        var service = CreateService(engine);
        service.SetCaptionLineTranslation(true);
        service.SetTranslationEnabled(true, "ja");
        service.Start();

        service.ProcessFinal(Final(1, "first sentence"));
        service.ProcessFinal(Final(2, "second sentence"));
        await service.FlushAsync();
        Assert.All(service.State.History, line => Assert.Equal("ja", line.TargetLanguage));

        // The provider switch (e.g. Argos → Gemini) resets translated content.
        service.ResetTranslatedContent();

        Assert.Equal("ja", service.State.TargetLanguage);
        Assert.True(service.State.TranslationEnabled);
        Assert.Equal(new[] { "first sentence", "second sentence" }, service.State.History.Select(line => line.Text));
        Assert.All(service.State.History, line =>
        {
            Assert.Equal(LineOrigin.SourceStt, line.Origin);
            Assert.Null(line.TranslatedText);
            Assert.Equal(CaptionTranslationStatus.NotRequested, line.TranslationStatus);
        });

        // The fresh session under the new provider translates new content to the SAME target.
        service.ProcessFinal(Final(3, "third sentence"));
        await service.FlushAsync();
        CaptionLine latest = Assert.Single(service.State.History, l => l.Sequence == 3);
        Assert.Equal("ja", latest.TargetLanguage);
    }

    [Fact]
    public void Live_session_boundary_drops_old_engines_same_target_translation_input()
    {
        // Spec (Gemini OFF → ON with the same target): translation-origin content produced BEFORE the
        // new live session began must be dropped, even when it carries the same target language — the
        // stale-target guard alone cannot tell a pre-OFF message from a post-ON one. This is the
        // Gemini-side counterpart of the Argos epoch guard (toggle OFF → ON is a fresh session).
        var clock = new MutableClock();
        var service = CreateService(engine: null, utcNow: clock.UtcNow);
        service.SetTranslationEnabled(true, "ja");
        service.Start();

        // The first live session begins; its content is accepted.
        service.SetLiveTranslationSession(true);
        clock.Now = new DateTime(2026, 8, 1, 0, 0, 30, DateTimeKind.Utc);
        service.ProcessFinalTranslation(new FinalTranslation(
            null, "luma", "en", "ja", clock.Now, clock.Now, 1, clock.Now));
        Assert.Single(service.State.History);

        // Toggle OFF then ON with the same target: a NEW live session begins at a later boundary.
        service.SetTranslationEnabled(false);
        service.SetLiveTranslationSession(false);
        Assert.Empty(service.State.History); // translation-origin content was reverted
        clock.Now = new DateTime(2026, 8, 1, 0, 1, 0, DateTimeKind.Utc);
        service.SetTranslationEnabled(true, "ja");
        service.SetLiveTranslationSession(true); // boundary recorded at 00:01:00

        // An OLD-session final (emitted BEFORE the boundary, same target) must be dropped.
        service.ProcessFinalTranslation(new FinalTranslation(
            null, "lumang ja", "en", "ja",
            new DateTime(2026, 8, 1, 0, 0, 55, DateTimeKind.Utc),
            new DateTime(2026, 8, 1, 0, 0, 56, DateTimeKind.Utc),
            2, new DateTime(2026, 8, 1, 0, 0, 56, DateTimeKind.Utc)));
        Assert.Empty(service.State.History);

        // A NEW-session final (emitted AFTER the boundary) is accepted — even when its CAPTURE time
        // is before the boundary, mirroring the Gemini live engine which stamps every transcript with
        // a fixed session-start base capture time set before the service's boundary is recorded. The
        // guard must separate sessions by emit time, not capture time.
        service.ProcessFinalTranslation(new FinalTranslation(
            null, "bagong ja", "en", "ja",
            new DateTime(2026, 8, 1, 0, 0, 58, DateTimeKind.Utc),
            new DateTime(2026, 8, 1, 0, 1, 6, DateTimeKind.Utc),
            3, new DateTime(2026, 8, 1, 0, 1, 6, DateTimeKind.Utc)));
        CaptionLine line = Assert.Single(service.State.History);
        Assert.Equal("bagong ja", line.Text);
        Assert.Equal(LineOrigin.Translation, line.Origin);
    }

    /// <summary>
    /// Awaits a condition that flips asynchronously (a gated engine's self-replenished follow-up
    /// request). Bounded so a regression fails instead of hanging the suite.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Condition was not reached within the timeout.");
            }

            await Task.Delay(5);
        }
    }
}
