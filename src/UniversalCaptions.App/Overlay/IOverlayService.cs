namespace UniversalCaptions.App.Overlay;

/// <summary>
/// Owns the overlay window's appearance and placement (ADR-0004): visibility, position, opacity,
/// font size, and opt-in click-through. It never mutates caption state — it only renders it.
/// </summary>
public interface IOverlayService
{
    /// <summary>True when the overlay window is visible.</summary>
    bool IsVisible { get; }

    /// <summary>The overlay opacity in [0.2, 1.0].</summary>
    double Opacity { get; set; }

    /// <summary>The overlay caption font size in [10, 96].</summary>
    double FontSize { get; set; }

    /// <summary>True when the overlay passes mouse input through to windows beneath it.</summary>
    bool ClickThrough { get; set; }

    /// <summary>Shows the overlay, positioning it at its configured (or default) location.</summary>
    /// <summary>Updates the source-language badge for the current caption session.</summary>
    void SetSourceLanguage(string? sourceLanguage);

    void Show();

    /// <summary>Hides the overlay.</summary>
    void Hide();

    /// <summary>Shows the overlay at the given screen coordinates.</summary>
    /// <param name="left">The overlay's left edge in screen coordinates.</param>
    /// <param name="top">The overlay's top edge in screen coordinates.</param>
    void ShowAt(double left, double top);

    /// <summary>
    /// Forces the overlay to re-read the current caption snapshot and reconcile its visual blocks.
    /// Idempotent and side-effect-free: if the snapshot is empty, stale text is cleared; if a new
    /// partial is in flight, it repaints normally. Used by the control window after an
    /// auto-reconnect to guarantee the dispatcher picks up the cleared caption state immediately,
    /// rather than waiting for the next event from the new session (540k bug).
    /// </summary>
    void Refresh();
}
