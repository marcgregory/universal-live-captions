using UniversalCaptions.Core.Translation;
using UniversalCaptions.Translation.Tests.Support;

namespace UniversalCaptions.Translation.Tests;

/// <summary>
/// Verifies the translation contract through the deterministic <see cref="FakeTranslationEngine"/>:
/// success, empty input, cancellation, failure, pair validation, and ordering.
/// </summary>
public sealed class FakeTranslationEngineTests
{
    [Fact]
    public async Task TranslateAsync_ReturnsMappedText_WithRequestedLanguages()
    {
        var engine = new FakeTranslationEngine();
        engine.Register("en", "tl", "Hello world", "Kumusta mundo");

        var result = await engine.TranslateAsync("Hello world", "en", "tl");

        Assert.Equal("Kumusta mundo", result.Text);
        Assert.Equal("en", result.SourceLanguage);
        Assert.Equal("tl", result.TargetLanguage);
        Assert.False(result.UsedPivot);
        Assert.Null(result.PivotLanguage);
        Assert.Null(result.DetectedSourceLanguage);
        Assert.True(result.Latency >= TimeSpan.Zero);
    }

    [Fact]
    public async Task TranslateAsync_AutoDetect_UsesDetectedSource()
    {
        var engine = new FakeTranslationEngine { DetectedSourceLanguage = "ja" };
        engine.Register("ja", "en", "こんにちは", "Hello");

        var result = await engine.TranslateAsync("こんにちは", null, "en");

        Assert.Equal("ja", result.SourceLanguage);
        Assert.Equal("ja", result.DetectedSourceLanguage);
        Assert.Equal("Hello", result.Text);
    }

    [Fact]
    public async Task TranslateAsync_ReportsPivotMetadata_WhenConfigured()
    {
        var engine = new FakeTranslationEngine { UsedPivot = true, PivotLanguage = "en" };
        engine.Register("tl", "ja", "Magandang araw", "良い一日");

        var result = await engine.TranslateAsync("Magandang araw", "tl", "ja");

        Assert.True(result.UsedPivot);
        Assert.Equal("en", result.PivotLanguage);
    }

    [Fact]
    public async Task TranslateAsync_SequencesCalls_IncreasingOrder()
    {
        var engine = new FakeTranslationEngine();
        engine.Register("en", "tl", "one", "isa");
        engine.Register("en", "tl", "two", "dalawa");

        var first = await engine.TranslateAsync("one", "en", "tl");
        var second = await engine.TranslateAsync("two", "en", "tl");

        Assert.True(second.Sequence > first.Sequence);
        Assert.Equal(2, engine.CallCount);
        Assert.Equal(new[] { "one", "two" }, engine.Calls.Select(c => c.Text));
    }

    [Fact]
    public async Task TranslateAsync_EmptyInput_ThrowsEmptyInput()
    {
        var engine = new FakeTranslationEngine();
        var exc = await Assert.ThrowsAsync<TranslationException>(() =>
            engine.TranslateAsync("", "en", "tl"));
        Assert.Equal(TranslationErrorKind.EmptyInput, exc.Kind);
    }

    [Fact]
    public async Task TranslateAsync_Cancellation_SurfacesOperationCanceled()
    {
        var engine = new FakeTranslationEngine { Latency = TimeSpan.FromSeconds(30) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.TranslateAsync("hello", "en", "tl", cts.Token));
    }

    [Fact]
    public async Task TranslateAsync_ConfiguredFailure_ThrowsTranslationException()
    {
        var engine = new FakeTranslationEngine();
        engine.FailNext(TranslationErrorKind.LanguagePairNotSupported, "no model");

        var exc = await Assert.ThrowsAsync<TranslationException>(() =>
            engine.TranslateAsync("hello", "en", "tl"));

        Assert.Equal(TranslationErrorKind.LanguagePairNotSupported, exc.Kind);
        Assert.Contains("no model", exc.Message);
    }

    [Fact]
    public async Task TranslateAsync_SourceEqualsTarget_Throws()
    {
        var engine = new FakeTranslationEngine();
        engine.Register("en", "en", "hello", "hello");

        var exc = await Assert.ThrowsAsync<TranslationException>(() =>
            engine.TranslateAsync("hello", "en", "en"));

        Assert.Equal(TranslationErrorKind.SourceEqualsTarget, exc.Kind);
        Assert.False(engine.WasCalled);
    }
}
