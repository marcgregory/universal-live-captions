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
