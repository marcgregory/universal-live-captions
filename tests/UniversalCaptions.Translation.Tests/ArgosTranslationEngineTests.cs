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
}
