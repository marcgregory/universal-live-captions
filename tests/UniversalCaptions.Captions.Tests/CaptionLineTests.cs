using UniversalCaptions.Core.Captions;

namespace UniversalCaptions.Captions.Tests;

/// <summary>
/// Verifies <see cref="CaptionLine"/> translation timestamp propagation through the immutable
/// With-methods, which is what end-to-end latency measurement reads.
/// </summary>
public sealed class CaptionLineTests
{
    private static readonly DateTime CapturedAt = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime StartedAt = new(2026, 8, 1, 0, 0, 5, DateTimeKind.Utc);
    private static readonly DateTime CompletedAt = new(2026, 8, 1, 0, 0, 6, DateTimeKind.Utc);

    private static CaptionLine Active(string text = "hello", long sequence = 1) =>
        new(text, "en", sequence, CapturedAt, CaptionLineState.Active);

    [Fact]
    public void Constructor_Defaults_TranslationTimestampsNull()
    {
        var line = Active();

        Assert.Null(line.TranslationStartedAtUtc);
        Assert.Null(line.TranslationCompletedAtUtc);
    }

    [Fact]
    public void WithPendingTranslation_StampsStartTime()
    {
        var line = Active().WithPendingTranslation("tl", StartedAt);

        Assert.Equal(StartedAt, line.TranslationStartedAtUtc);
        Assert.Null(line.TranslationCompletedAtUtc);
        Assert.Equal(CaptionTranslationStatus.Pending, line.TranslationStatus);
    }

    [Fact]
    public void WithTranslation_StampsStartAndCompletionTimes()
    {
        var line = Active().WithTranslation("kumusta", "tl", StartedAt, CompletedAt);

        Assert.Equal(StartedAt, line.TranslationStartedAtUtc);
        Assert.Equal(CompletedAt, line.TranslationCompletedAtUtc);
        Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus);
    }

    [Fact]
    public void WithTranslation_WithoutTimestamps_LeavesThemNull()
    {
        var line = Active().WithTranslation("kumusta", "tl");

        Assert.Null(line.TranslationStartedAtUtc);
        Assert.Null(line.TranslationCompletedAtUtc);
        Assert.Equal(CaptionTranslationStatus.Completed, line.TranslationStatus);
    }

    [Fact]
    public void WithTranslationFailure_StampsStartTimeOnly()
    {
        var line = Active().WithTranslationFailure("boom", StartedAt);

        Assert.Equal(StartedAt, line.TranslationStartedAtUtc);
        Assert.Null(line.TranslationCompletedAtUtc);
        Assert.Equal(CaptionTranslationStatus.Failed, line.TranslationStatus);
    }

    [Fact]
    public void WithTranslation_PreservesCapturedTime()
    {
        var line = Active().WithTranslation("kumusta", "tl", StartedAt, CompletedAt);

        Assert.Equal(CapturedAt, line.CapturedAtUtc);
    }
}
