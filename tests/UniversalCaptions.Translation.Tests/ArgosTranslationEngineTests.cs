using UniversalCaptions.Core.Translation;
using UniversalCaptions.Translation.Argos;
using UniversalCaptions.Translation.Tests.Support;

namespace UniversalCaptions.Translation.Tests;

/// <summary>
/// Verifies <see cref="ArgosTranslationEngine"/> validation, request mapping, error mapping, and
/// process lifecycle through a deterministic fake process (no Python runtime required).
/// </summary>
public sealed class ArgosTranslationEngineTests
{
    private sealed record Fixture(FakeArgosProcess Process, ArgosTranslationEngine Engine)
        : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }

    private static Fixture CreateFixture(FakeArgosProcess? process = null)
    {
        var fake = process ?? new FakeArgosProcess();
        return new Fixture(fake, new ArgosTranslationEngine(fake));
    }

    [Fact]
    public async Task TranslateAsync_SendsRequest_AndReturnsTranslatedText()
    {
        var process = new FakeArgosProcess();
        process.SetHandler(req => new ArgosResponse(true, "Kumusta mundo", "en", false, null, null, null, null));
        using var fixture = CreateFixture(process);

        var result = await fixture.Engine.TranslateAsync("Hello world", "en", "tl");

        Assert.Equal("Kumusta mundo", result.Text);
        Assert.Equal("en", result.SourceLanguage);
        Assert.Equal("tl", result.TargetLanguage);
        Assert.False(result.UsedPivot);
        Assert.Null(result.PivotLanguage);

        var request = Assert.Single(process.Requests);
        Assert.Equal("Hello world", request.Text);
        Assert.Equal("en", request.Source);
        Assert.Equal("tl", request.Target);
        Assert.True(process.Started);
    }

    [Fact]
    public async Task TranslateAsync_ReportsDetectedSource_AndPivotMetadata()
    {
        var process = new FakeArgosProcess();
        process.SetHandler(req => new ArgosResponse(true, "良い一日", "ja", true, "en", null, null, null));
        using var fixture = CreateFixture(process);

        var result = await fixture.Engine.TranslateAsync("Magandang araw", null, "ja");

        Assert.Equal("ja", result.DetectedSourceLanguage);
        Assert.Equal("ja", result.SourceLanguage);
        Assert.True(result.UsedPivot);
        Assert.Equal("en", result.PivotLanguage);
    }

    [Fact]
    public async Task TranslateAsync_ProcessFailure_MapsToTranslationException()
    {
        var process = new FakeArgosProcess();
        process.FailOnTranslate(TranslationErrorKind.LanguagePairNotSupported, "no model for tl->ja");
        using var fixture = CreateFixture(process);

        var exc = await Assert.ThrowsAsync<TranslationException>(() =>
            fixture.Engine.TranslateAsync("hello", "tl", "ja"));

        Assert.Equal(TranslationErrorKind.LanguagePairNotSupported, exc.Kind);
        Assert.Contains("no model", exc.Message);
        Assert.IsType<TranslationProcessException>(exc.InnerException);
    }

    [Fact]
    public async Task TranslateAsync_NullText_ThrowsArgumentNullException()
    {
        using var fixture = CreateFixture();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            fixture.Engine.TranslateAsync(null!, "en", "tl"));
    }

    [Fact]
    public async Task TranslateAsync_EmptyText_ThrowsEmptyInput()
    {
        using var fixture = CreateFixture();
        var exc = await Assert.ThrowsAsync<TranslationException>(() =>
            fixture.Engine.TranslateAsync("   ", "en", "tl"));
        Assert.Equal(TranslationErrorKind.EmptyInput, exc.Kind);
    }

    [Fact]
    public async Task TranslateAsync_MissingTarget_ThrowsUnsupportedLanguage()
    {
        using var fixture = CreateFixture();
        var exc = await Assert.ThrowsAsync<TranslationException>(() =>
            fixture.Engine.TranslateAsync("hello", "en", " "));
        Assert.Equal(TranslationErrorKind.UnsupportedLanguage, exc.Kind);
    }

    [Fact]
    public async Task TranslateAsync_SourceEqualsTarget_Throws()
    {
        using var fixture = CreateFixture();
        var exc = await Assert.ThrowsAsync<TranslationException>(() =>
            fixture.Engine.TranslateAsync("hello", "en", "EN"));
        Assert.Equal(TranslationErrorKind.SourceEqualsTarget, exc.Kind);
        Assert.Equal(0, fixture.Process.StartCount);
    }

    [Fact]
    public async Task TranslateAsync_StartFailure_SurfacesAsTranslationException()
    {
        var process = new FakeArgosProcess();
        process.ThrowOnStart(new TranslationProcessException(TranslationErrorKind.EngineUnavailable, "python missing"));
        using var fixture = CreateFixture(process);

        var exc = await Assert.ThrowsAsync<TranslationException>(() =>
            fixture.Engine.TranslateAsync("hello", "en", "tl"));

        Assert.Equal(TranslationErrorKind.EngineUnavailable, exc.Kind);
    }

    [Fact]
    public async Task TranslateAsync_Cancellation_Propagates()
    {
        var process = new FakeArgosProcess();
        process.AddTranslateDelay(TimeSpan.FromSeconds(30));
        using var fixture = CreateFixture(process);
        using var cts = new CancellationTokenSource();

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Engine.TranslateAsync("hello", "en", "tl", cts.Token));
    }

    [Fact]
    public async Task TranslateAsync_SequencesRequests_Increasing()
    {
        var process = new FakeArgosProcess();
        using var fixture = CreateFixture(process);

        var first = await fixture.Engine.TranslateAsync("one", "en", "tl");
        var second = await fixture.Engine.TranslateAsync("two", "en", "tl");

        Assert.True(second.Sequence > first.Sequence);
        Assert.Equal(2, process.Requests.Count);
        Assert.Equal(new[] { "one", "two" }, process.Requests.Select(r => r.Text));
    }

    [Fact]
    public async Task TranslateAsync_ConcurrentCalls_AreSerialized()
    {
        var process = new FakeArgosProcess();
        process.SetHandler(req => new ArgosResponse(true, req.Text, null, false, null, null, null, null));
        using var fixture = CreateFixture(process);

        var results = await Task.WhenAll(
            fixture.Engine.TranslateAsync("a", "en", "tl"),
            fixture.Engine.TranslateAsync("b", "en", "tl"),
            fixture.Engine.TranslateAsync("c", "en", "tl"));

        Assert.Equal(3, results.Length);
        Assert.Equal(3, process.Requests.Count);
        Assert.Equal(new[] { "a", "b", "c" }, process.Requests.Select(r => r.Text));
    }

    [Fact]
    public async Task TranslateAsync_AfterFatalProcessError_RestartsProcess()
    {
        var process = new FakeArgosProcess();
        process.SetHandler(req => new ArgosResponse(true, "ok", null, false, null, null, null, null));
        using var fixture = CreateFixture(process);

        var first = await fixture.Engine.TranslateAsync("hello", "en", "tl");
        Assert.NotNull(first);
        Assert.Equal(1, process.StartCount);

        process.FailOnTranslate(TranslationErrorKind.EngineUnavailable, "process died");
        var exc = await Assert.ThrowsAsync<TranslationException>(() =>
            fixture.Engine.TranslateAsync("hello", "en", "tl"));
        Assert.Equal(TranslationErrorKind.EngineUnavailable, exc.Kind);

        process.ClearFailure();
        var recovered = await fixture.Engine.TranslateAsync("hello", "en", "tl");
        Assert.NotNull(recovered);
        Assert.Equal(2, process.StartCount);
    }

    [Fact]
    public async Task Dispose_DisposesProcess()
    {
        var process = new FakeArgosProcess();
        var engine = new ArgosTranslationEngine(process);

        engine.Dispose();
        engine.Dispose();

        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task TriggerPreWarm_StartsProcess_AndSendsOneWarmupRequest()
    {
        var process = new FakeArgosProcess();
        process.SetHandler(req => new ArgosResponse(true, "warm", null, false, null, null, null, null));
        using var fixture = CreateFixture(process);

        await fixture.Engine.TriggerPreWarmAsync("en", "tl");

        Assert.True(process.Started);
        Assert.Equal(1, process.StartCount);
        Assert.Single(process.Requests);
        Assert.Equal("The quick brown fox jumps over the lazy dog.", process.Requests[0].Text);
        Assert.Equal("tl", process.Requests[0].Target);
    }

    [Fact]
    public async Task TriggerPreWarm_IsIdempotent_SharedTask_DoesNotStartTwice()
    {
        var process = new FakeArgosProcess();
        process.SetHandler(req => new ArgosResponse(true, "ok", null, false, null, null, null, null));
        using var fixture = CreateFixture(process);

        var first = fixture.Engine.TriggerPreWarmAsync("en", "tl");
        var second = fixture.Engine.TriggerPreWarmAsync("en", "tl");

        await Task.WhenAll(first, second);

        Assert.Equal(1, process.StartCount);
        Assert.Single(process.Requests);
    }

    [Fact]
    public async Task RealTranslation_DuringWarmup_ReusesSharedStart_NoDuplicateInit()
    {
        var process = new FakeArgosProcess();
        process.AddTranslateDelay(TimeSpan.FromMilliseconds(150));
        process.SetHandler(req => new ArgosResponse(true, $"[{req.Target}] {req.Text}", null, false, null, null, null, null));
        using var fixture = CreateFixture(process);

        var warmTask = fixture.Engine.TriggerPreWarmAsync("en", "tl");
        // Real translation arrives while warm-up/init is still in flight: must await the same
        // start task (StartCount == 1), not spawn a second initialization.
        var real = await fixture.Engine.TranslateAsync("Hello", "en", "tl");
        await warmTask;

        Assert.Equal(1, process.StartCount);
        Assert.Single(process.Requests.Where(r => r.Text == "Hello"));
        Assert.Equal("Hello", real.Text[real.Text.IndexOf(']')..].TrimStart(']', ' ').Trim());
    }

    [Fact]
    public async Task RealTranslation_DuringWarmup_AwaitsWarmUp_DoesNotRaceIt()
    {
        var process = new FakeArgosProcess();
        process.AddTranslateDelay(TimeSpan.FromMilliseconds(250));
        process.SetHandler(req => new ArgosResponse(true, $"[{req.Target}] {req.Text}", null, false, null, null, null, null));
        using var fixture = CreateFixture(process);

        // A slow warm-up is in flight; the real caption arrives right after. The engine must await
        // the in-flight warm-up (its throwaway translate pays the cold model-load) so the real
        // request reuses the warmed process instead of racing the warm-up through the gate. While
        // the warm-up is still running, only its single request may be in flight; the real request
        // may only be issued after the warm-up completes.
        var warmTask = fixture.Engine.TriggerPreWarmAsync("en", "tl");
        Assert.Single(process.Requests); // warm-up request issued immediately

        var realTask = fixture.Engine.TranslateAsync("Real caption", "en", "tl");
        Assert.Single(process.Requests); // real request must NOT race the warm-up

        await realTask;
        Assert.Equal("The quick brown fox jumps over the lazy dog.", process.Requests[0].Text);
        Assert.Equal("Real caption", process.Requests[1].Text);
        await warmTask;

        Assert.Equal(2, process.Requests.Count);
        Assert.Equal(1, process.StartCount);
    }

    [Fact]
    public async Task RealTranslation_DuringWarmup_ForDifferentTarget_DoesNotAwait()
    {
        var process = new FakeArgosProcess();
        process.AddTranslateDelay(TimeSpan.FromMilliseconds(250));
        process.SetHandler(req => new ArgosResponse(true, $"[{req.Target}] {req.Text}", null, false, null, null, null, null));
        using var fixture = CreateFixture(process);

        // Warm-up targets 'tl'; a real translation for a *different* target must not be blocked by
        // that warm-up (it gets its own lazy process start/request). The real request is issued
        // while the slow warm-up is still in flight, so it must not have awaited it.
        var warmTask = fixture.Engine.TriggerPreWarmAsync("en", "tl");
        var real = await fixture.Engine.TranslateAsync("Hallo", "en", "de");
        await warmTask;

        Assert.Single(process.Requests.Where(r => r.Target == "de"));
        Assert.Single(process.Requests.Where(r => r.Target == "tl"));
        Assert.Contains(process.Requests, r => r.Target == "tl" && r.Text == "The quick brown fox jumps over the lazy dog.");
    }

    [Fact]
    public async Task TriggerPreWarm_StartFailure_IsSwallowed_RealTranslationStillFallsBackToLazyStart()
    {
        var process = new FakeArgosProcess();
        process.ThrowOnStart(new TranslationProcessException(TranslationErrorKind.EngineUnavailable, "python missing"));
        using var fixture = CreateFixture(process);

        // Pre-warm swallows the failure; the shared start task is cleared so the real translation
        // retries start (fallback) rather than failing forever.
        await fixture.Engine.TriggerPreWarmAsync("en", "tl");

        // Pre-warm swallows the failure; the shared start task is cleared so the real translation
        // retries start (fallback) rather than failing forever.
        var exc = await Assert.ThrowsAsync<TranslationException>(() =>
            fixture.Engine.TranslateAsync("hello", "en", "tl"));
        Assert.Equal(TranslationErrorKind.EngineUnavailable, exc.Kind);
        Assert.Equal(2, fixture.Process.StartCount);
    }

    [Fact]
    public async Task RealTranslation_AfterFatalWarmProcessError_RestartsProcess()
    {
        var process = new FakeArgosProcess();
        // Warm-up translate fails with a fatal kind that kills the underlying process.
        process.SetHandler(req => throw new TranslationProcessException(TranslationErrorKind.Timeout, "warm timed out"));
        using var fixture = CreateFixture(process);

        await fixture.Engine.TriggerPreWarmAsync("en", "tl");
        Assert.Equal(1, process.StartCount);

        // The fatal warm error must reset the shared start task so a real translation re-creates
        // the process instead of being handed a dead "completed" start and losing the first caption.
        var exc = await Assert.ThrowsAsync<TranslationException>(() =>
            fixture.Engine.TranslateAsync("hello", "en", "tl"));
        Assert.Equal(TranslationErrorKind.Timeout, exc.Kind);
        // The real translation re-started the process (StartCount incremented), proving the shared
        // start task was reset rather than reused-as-completed.
        Assert.Equal(2, fixture.Process.StartCount);
    }

    [Fact]
    public async Task TriggerPreWarm_TargetChange_TriggersFreshWarm()
    {
        var process = new FakeArgosProcess();
        process.SetHandler(req => new ArgosResponse(true, req.Text, null, false, null, null, null, null));
        using var fixture = CreateFixture(process);

        await fixture.Engine.TriggerPreWarmAsync("en", "tl");
        Assert.Single(fixture.Process.Requests);

        // A different target must not reuse the completed warm-up; it needs its own.
        await fixture.Engine.TriggerPreWarmAsync("en", "de");
        Assert.Equal(2, fixture.Process.Requests.Count);
        Assert.Equal("de", fixture.Process.Requests[1].Target);

        // Re-requesting the original target re-warms it again (it is no longer the candidate).
        await fixture.Engine.TriggerPreWarmAsync("en", "tl");
        Assert.Equal(3, fixture.Process.Requests.Count);
    }
}
