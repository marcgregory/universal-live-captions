namespace UniversalCaptions.App.Settings;

/// <summary>
/// The user-facing preferences persisted between app launches (TD-005). Only the six UI-preference
/// categories identified in discovery are stored: audio source device (1), speech language (2),
/// translation on/off + target (3), overlay appearance — opacity/font size/click-through (4), overlay
/// placement (5), and overlay view state (6). Nullable properties mean "use the built-in default", so
/// a missing, partial, or forward-compatible settings file always degrades gracefully. Engine and
/// environment knobs (<c>UC_STT_*</c>, Argos/Python paths, model selection) are deliberately NOT part
/// of this model — they stay environment-variable driven.
/// </summary>
public sealed record UserSettings
{
    /// <summary>Current settings schema version; future migrations read this before upgrading.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>The audio source render-device id; null = system default render device.</summary>
    public string? DeviceId { get; init; }

    /// <summary>The speech-to-text language hint; null = auto-detect.</summary>
    public string? Language { get; init; }

    /// <summary>True when translation is enabled; null = default (off).</summary>
    public bool? TranslationEnabled { get; init; }

    /// <summary>The ISO 639-1 translation target language; null = default.</summary>
    public string? TargetLanguage { get; init; }

    /// <summary>Overlay opacity in [0.2, 1.0]; null = default (1.0).</summary>
    public double? Opacity { get; init; }

    /// <summary>Overlay caption font size in [10, 96]; null = default (16).</summary>
    public double? FontSize { get; init; }

    /// <summary>True when the overlay passes mouse input through to windows beneath it; null = default (false).</summary>
    public bool? ClickThrough { get; init; }

    /// <summary>Overlay left screen coordinate when the user explicitly positioned it; null = adaptive default placement.</summary>
    public double? OverlayLeft { get; init; }

    /// <summary>Overlay top screen coordinate when the user explicitly positioned it; null = adaptive default placement.</summary>
    public double? OverlayTop { get; init; }

    /// <summary>True when the overlay is expanded; null = default (expanded).</summary>
    public bool? OverlayExpanded { get; init; }

    /// <summary>The current schema version of <see cref="UserSettings"/>.</summary>
    public const int CurrentVersion = 1;
}
