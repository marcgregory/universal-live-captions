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
        int historyCapacity = 50) =>
        new(new CaptionServiceOptions(sourceLanguage, targetLanguage, historyCapacity), engine);

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
    public async Task Stop_CancelsInFlightTranslation_LineStaysPending()
    {
        var engine = new GatedTranslationEngine();
        var service = CreateService(engine);
        service.Start();
        service.SetTranslationEnabled(true);
        service.ProcessFinal(Final(2, "hello"));

        service.Stop();
        await service.FlushAsync();

        Assert.False(service.IsRunning);
        var line = Assert.Single(service.State.History);
        Assert.Equal(CaptionTranslationStatus.Pending, line.TranslationStatus);
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
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CaptionService(null!));
    }

    [Fact]
    public void Options_NullSourceLanguage_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CaptionServiceOptions(null!));
    }
}
