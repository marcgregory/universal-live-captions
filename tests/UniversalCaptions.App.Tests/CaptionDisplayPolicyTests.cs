using UniversalCaptions.App.Overlay;
using UniversalCaptions.Core.Captions;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Verifies the overlay display policy resolved in change-impact Q1: the active line renders the
/// verbatim latest partial, committed finals render as history (oldest first, chronological), and a
/// completed translation replaces the source text while an off/pending/failed translation keeps it.
/// When translation is enabled, an in-progress active line that has not been translated yet is not
/// rendered at all, so the overlay never flashes the source language between live translations.
/// </summary>
public class CaptionDisplayPolicyTests
{
    private static CaptionLine Active(
        string text,
        long sequence = 1,
        string? translated = null,
        CaptionTranslationStatus status = CaptionTranslationStatus.NotRequested) =>
        new(text, "en", sequence, DateTime.UtcNow, CaptionLineState.Active,
            targetLanguage: translated is null ? null : "tl",
            translatedText: translated,
            translationStatus: status);

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
    public void Model_combines_active_line_and_chronological_history()
    {
        var snapshot = new CaptionSnapshot(
            Active("going now", translated: "pupunta na", status: CaptionTranslationStatus.Completed),
            [Final("first", 1), Final("second", 2, "pangalawa", CaptionTranslationStatus.Completed)],
            IsSessionActive: true,
            TranslationEnabled: true,
            TargetLanguage: "tl");

        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(snapshot);

        Assert.Equal("pupunta na", model.ActiveLine!.Text);
        Assert.True(model.ActiveLine.IsTranslated);
        Assert.Equal(2, model.History.Count);
        Assert.Equal("first", model.History[0].Text);
        Assert.False(model.History[0].IsTranslated);
        Assert.Equal("pangalawa", model.History[1].Text);
        Assert.True(model.History[1].IsTranslated);
        Assert.False(model.IsEmpty);
    }

    [Fact]
    public void First_caption_appears_alone_in_history()
    {
        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(
            new CaptionSnapshot(null, [Final("A", 1)], IsSessionActive: true, TranslationEnabled: false, TargetLanguage: null));

        CaptionDisplayLine first = Assert.Single(model.History);
        Assert.Equal("A", first.Text);
        Assert.Null(model.ActiveLine);
    }

    [Fact]
    public void Newest_caption_is_at_the_bottom()
    {
        // 4 short history lines whose combined length is well under MaxVisibleCharacters (200):
        // all four are kept and the newest (D) appears last (bottom of overlay).
        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(
            new CaptionSnapshot(
                null,
                [Final("A", 1), Final("B", 2), Final("C", 3), Final("D", 4)],
                IsSessionActive: true,
                TranslationEnabled: false,
                TargetLanguage: null));

        Assert.Equal(new[] { "A", "B", "C", "D" }, model.History.Select(line => line.Text));
        Assert.Equal("A", model.History[0].Text);
        Assert.Equal("D", model.History[^1].Text);
    }

    [Fact]
    public void Overlay_passes_all_history_through_without_trimming()
    {
        // The display model no longer trims history by a character budget.
        // The fixed-height ScrollViewer in the overlay clips content visually —
        // all history entries are passed to the overlay and the ScrollViewer auto-scrolls
        // to the bottom so the newest text is visible (oldest scrolls off the top).
        string cap80 = new string('x', 80);
        var state = new CaptionState(historyCapacity: 10);
        state.AddFinalLine(Final(cap80 + "A", 1));
        state.AddFinalLine(Final(cap80 + "B", 2));
        state.AddFinalLine(Final(cap80 + "C", 3));

        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(
            new CaptionSnapshot(null, state.History, IsSessionActive: true, TranslationEnabled: false, TargetLanguage: null));

        // All 3 entries pass through — no eviction in the model layer.
        Assert.Equal(3, model.History.Count);
        Assert.EndsWith("A", model.History[0].Text);
        Assert.EndsWith("B", model.History[1].Text);
        Assert.EndsWith("C", model.History[^1].Text);
    }

    [Fact]
    public void Capacity_eviction_removes_oldest_from_the_top()
    {
        var state = new CaptionState(historyCapacity: 3);
        state.AddFinalLine(Final("A", 1));
        state.AddFinalLine(Final("B", 2));
        state.AddFinalLine(Final("C", 3));
        state.AddFinalLine(Final("D", 4));

        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(
            new CaptionSnapshot(null, state.History, IsSessionActive: true, TranslationEnabled: false, TargetLanguage: null));

        Assert.Equal(new[] { "B", "C", "D" }, model.History.Select(line => line.Text));
        Assert.Equal("B", model.History[0].Text);
        Assert.Equal("D", model.History[^1].Text);
    }

    [Fact]
    public void Partial_to_final_append_preserves_existing_order_and_does_not_duplicate()
    {
        var state = new CaptionState(historyCapacity: 10);
        state.AddFinalLine(Final("A", 1));
        state.AddFinalLine(Final("B", 2));
        state.UpdateActiveLine(Active("C", sequence: 3));

        state.ClearActiveLine();
        state.AddFinalLine(Final("C", 3));
        state.UpdateActiveLine(Active("D", sequence: 4));

        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(
            new CaptionSnapshot(state.ActiveLine, state.History, IsSessionActive: true, TranslationEnabled: false, TargetLanguage: null));

        Assert.Equal(new[] { "A", "B", "C" }, model.History.Select(line => line.Text));
        Assert.Equal("D", model.ActiveLine!.Text);
        Assert.Equal(3, model.History.Count);
        Assert.Single(model.History, line => line.Sequence == 3);
    }

    [Fact]
    public void Current_caption_occupies_its_own_slot_separate_from_history()
    {
        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(
            new CaptionSnapshot(
                Active("going now", sequence: 3),
                [Final("A", 1), Final("B", 2)],
                IsSessionActive: true,
                TranslationEnabled: false,
                TargetLanguage: null));

        Assert.Equal("going now", model.ActiveLine!.Text);
        Assert.Equal(new[] { "A", "B" }, model.History.Select(line => line.Text));
        Assert.DoesNotContain(model.History, line => line.Sequence == 3);
        Assert.DoesNotContain(model.History, line => line.Text == "going now");
    }

    [Fact]
    public void Translation_enabled_hides_untranslated_active_line()
    {
        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(
            new CaptionSnapshot(
                Active("the quick brown"),
                [],
                IsSessionActive: true,
                TranslationEnabled: true,
                TargetLanguage: "tl"));

        Assert.Null(model.ActiveLine);
    }

    [Fact]
    public void Translation_enabled_surfaces_completed_active_line()
    {
        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(
            new CaptionSnapshot(
                Active("going now", translated: "pupunta na", status: CaptionTranslationStatus.Completed),
                [],
                IsSessionActive: true,
                TranslationEnabled: true,
                TargetLanguage: "tl"));

        Assert.Equal("pupunta na", model.ActiveLine!.Text);
        Assert.True(model.ActiveLine.IsTranslated);
    }

    [Fact]
    public void Translation_failure_keeps_active_line_source()
    {
        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(
            new CaptionSnapshot(
                Active("hello", status: CaptionTranslationStatus.Failed),
                [],
                IsSessionActive: true,
                TranslationEnabled: true,
                TargetLanguage: "tl"));

        Assert.Equal("hello", model.ActiveLine!.Text);
        Assert.False(model.ActiveLine.IsTranslated);
    }

    [Fact]
    public void Translation_disabled_shows_active_line_source()
    {
        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(
            new CaptionSnapshot(
                Active("the quick brown"),
                [],
                IsSessionActive: true,
                TranslationEnabled: false,
                TargetLanguage: null));

        Assert.Equal("the quick brown", model.ActiveLine!.Text);
        Assert.False(model.ActiveLine.IsTranslated);
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

    [Fact]
    public void Translation_enabled_exposes_uppercase_language_badge()
    {
        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(
            new CaptionSnapshot(null, [], IsSessionActive: true, TranslationEnabled: true, TargetLanguage: "tl"));

        Assert.Equal("TL", model.LanguageBadge);
    }

    [Fact]
    public void Translation_disabled_has_no_language_badge()
    {
        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(
            new CaptionSnapshot(null, [], IsSessionActive: true, TranslationEnabled: false, TargetLanguage: null));

        Assert.Null(model.LanguageBadge);
    }
}
