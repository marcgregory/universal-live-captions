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
    void Show();

    /// <summary>Hides the overlay.</summary>
    void Hide();

    /// <summary>Shows the overlay at the given screen coordinates.</summary>
    /// <param name="left">The overlay's left edge in screen coordinates.</param>
    /// <param name="top">The overlay's top edge in screen coordinates.</param>
    void ShowAt(double left, double top);
}
