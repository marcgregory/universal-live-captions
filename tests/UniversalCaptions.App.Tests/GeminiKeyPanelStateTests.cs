using UniversalCaptions.App.Controls;
using UniversalCaptions.App.Settings;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// The Gemini key panel must ALWAYS stay reachable when the stored key needs attention (missing,
/// malformed, or rejected). Otherwise a broken key permanently locks the user out of fixing it:
/// captions cannot start without a usable key, so Add/Update/Remove must never be disabled.
/// The <c>isGemini</c> parameter is retained by <see cref="ControlWindow.ComputeGeminiKeyPanelState"/>
/// for compatibility; the App now always passes true, and these tests pin both branches of the
/// pure function.
/// </summary>
public sealed class GeminiKeyPanelStateTests
{
    [Fact]
    public void Non_gemini_branch_without_key_problem_panel_is_not_applicable()
    {
        var state = ControlWindow.ComputeGeminiKeyPanelState(
            isGemini: false, hasKey: false, availability: GeminiAvailability.Unknown);

        Assert.False(state.IsEnabled);
        Assert.Equal("Not applicable", state.StatusText);
        Assert.False(state.ShowAdd);
        Assert.False(state.ShowUpdate);
        Assert.False(state.ShowRemove);
    }

    [Fact]
    public void Stored_malformed_key_panel_stays_reachable_to_update()
    {
        // THE dead-end regression: the stored key is malformed, but the user must still be able to
        // UPDATE the key.
        var state = ControlWindow.ComputeGeminiKeyPanelState(
            isGemini: false, hasKey: true, availability: GeminiAvailability.MalformedKey);

        Assert.True(state.IsEnabled, "A malformed key must never disable the key panel.");
        Assert.Equal("Key looks invalid", state.StatusText);
        Assert.False(state.ShowAdd);
        Assert.True(state.ShowUpdate, "The Update button must be reachable while the key is malformed.");
        Assert.True(state.ShowRemove);
        Assert.True(state.RemoveEnabled);
    }

    [Fact]
    public void Missing_key_panel_stays_reachable_to_add()
    {
        var state = ControlWindow.ComputeGeminiKeyPanelState(
            isGemini: false, hasKey: false, availability: GeminiAvailability.MissingKey);

        Assert.True(state.IsEnabled, "A missing key must never disable the key panel.");
        Assert.Equal("No key stored", state.StatusText);
        Assert.True(state.ShowAdd, "The Add button must be reachable so a key can be added.");
        Assert.False(state.ShowUpdate);
        Assert.True(state.ShowRemove);
        Assert.False(state.RemoveEnabled);
    }

    [Fact]
    public void Rejected_key_panel_stays_reachable_to_update()
    {
        var state = ControlWindow.ComputeGeminiKeyPanelState(
            isGemini: false, hasKey: true, availability: GeminiAvailability.InvalidKey);

        Assert.True(state.IsEnabled, "A rejected key must never disable the key panel.");
        Assert.Equal("Key rejected — update", state.StatusText);
        Assert.True(state.ShowUpdate);
    }

    [Fact]
    public void Gemini_selected_with_valid_key_panel_shows_verified()
    {
        var state = ControlWindow.ComputeGeminiKeyPanelState(
            isGemini: true, hasKey: true, availability: GeminiAvailability.Available);

        Assert.True(state.IsEnabled);
        Assert.Equal("Configured", state.StatusText);
        Assert.True(state.ShowUpdate);
        Assert.True(state.RemoveEnabled);
        Assert.Equal("✓ verified", state.LastUpdatedText);
    }

    [Fact]
    public void Gemini_selected_with_missing_key_panel_shows_add()
    {
        var state = ControlWindow.ComputeGeminiKeyPanelState(
            isGemini: true, hasKey: false, availability: GeminiAvailability.MissingKey);

        Assert.True(state.IsEnabled);
        Assert.Equal("No key stored", state.StatusText);
        Assert.True(state.ShowAdd);
        Assert.False(state.ShowUpdate);
    }

    [Fact]
    public void Gemini_selected_with_network_check_pending_panel_keeps_selection_usable()
    {
        // Transient states must not lock the user out either — the live check may be pending.
        var state = ControlWindow.ComputeGeminiKeyPanelState(
            isGemini: true, hasKey: true, availability: GeminiAvailability.Unknown);

        Assert.True(state.IsEnabled);
        Assert.Equal("Checking…", state.StatusText);
        Assert.True(state.ShowUpdate);
    }
}
