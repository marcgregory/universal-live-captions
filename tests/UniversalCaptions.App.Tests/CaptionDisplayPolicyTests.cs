using UniversalCaptions.App.Overlay;
using UniversalCaptions.Core.Captions;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Verifies the overlay display policy resolved in change-impact Q1: the active line renders the
/// verbatim latest partial, committed finals render as history (newest first), and a completed
/// translation replaces the source text while an off/pending/failed translation keeps it.
/// </summary>
public class CaptionDisplayPolicyTests
{
    private static CaptionLine Active(string text, long sequence = 1) =>
        new(text, "en", sequence, DateTime.UtcNow, CaptionLineState.Active);

    private static CaptionLine Final(
        string text,
        long sequence,
        string? translated = null,
        CaptionTranslationStatus status = CaptionTranslationStatus.NotRequested) =>
        new(text, "en", sequence, DateTime.UtcNow, CaptionLineState.Final, DateTime.UtcNow,
            targetLanguage: translated is null ? null : "tl",
            translatedText: translated,
            translationStatus: status);

    [Fact]
    public void Missing_line_maps_to_null()
    {
        Assert.Null(CaptionDisplayPolicy.ToDisplayLine(null));
    }

    [Fact]
    public void Active_line_renders_verbatim_partial_text()
    {
        CaptionDisplayLine? display = CaptionDisplayPolicy.ToDisplayLine(Active("the quick brown"));

        Assert.NotNull(display);
        Assert.Equal("the quick brown", display!.Text);
        Assert.False(display.IsTranslated);
    }

    [Fact]
    public void Completed_translation_replaces_source_on_final_line()
    {
        CaptionDisplayLine? display = CaptionDisplayPolicy.ToDisplayLine(
            Final("hello", 1, "kamusta", CaptionTranslationStatus.Completed));

        Assert.Equal("kamusta", display!.Text);
        Assert.True(display.IsTranslated);
    }

    [Theory]
    [InlineData(CaptionTranslationStatus.NotRequested)]
    [InlineData(CaptionTranslationStatus.Pending)]
    [InlineData(CaptionTranslationStatus.Failed)]
    public void Translation_off_pending_or_failed_keeps_source_text(CaptionTranslationStatus status)
    {
        CaptionDisplayLine? display = CaptionDisplayPolicy.ToDisplayLine(Final("hello", 1, "kamusta", status));

        Assert.Equal("hello", display!.Text);
        Assert.False(display.IsTranslated);
    }

    [Fact]
    public void Model_combines_active_line_and_newest_first_history()
    {
        var snapshot = new CaptionSnapshot(
            Active("going now"),
            [Final("first", 1), Final("second", 2, "pangalawa", CaptionTranslationStatus.Completed)],
            IsSessionActive: true,
            TranslationEnabled: true,
            TargetLanguage: "tl");

        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(snapshot);

        Assert.Equal("going now", model.ActiveLine!.Text);
        Assert.Equal(2, model.History.Count);
        Assert.Equal("pangalawa", model.History[0].Text);
        Assert.True(model.History[0].IsTranslated);
        Assert.Equal("first", model.History[1].Text);
        Assert.False(model.IsEmpty);
    }

    [Fact]
    public void Empty_state_is_empty()
    {
        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(
            new CaptionSnapshot(null, [], IsSessionActive: false, TranslationEnabled: false, TargetLanguage: null));

        Assert.True(model.IsEmpty);
        Assert.Null(model.ActiveLine);
        Assert.Empty(model.History);
    }
}
