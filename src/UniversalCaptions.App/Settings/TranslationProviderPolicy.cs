using UniversalCaptions.Core.Captions;

namespace UniversalCaptions.App.Settings;

/// <summary>
/// Decides which component owns caption translation for the selected <see cref="TranslationProvider"/>.
/// The App must never run two translation paths over the same captions: when Gemini is selected the
/// live Gemini engine owns translation, so Argos caption-line translation is suppressed; when Argos is
/// selected the local caption-line engine owns it (and no live audio engine is involved).
/// </summary>
/// <remarks>
/// <para>
/// This class is the single testable seam for the provider → translation-path mapping. It answers only
/// <em>how</em> translation is performed for the selected provider — never how the UI behaves: the
/// common translation state (<see cref="CaptionState.TranslationEnabled"/>/<see cref="CaptionState.TargetLanguage"/>)
/// and the checkbox/dropdown/badge all reflect the user's toggle identically for every provider. The
/// control window uses the policy in exactly two places: deciding whether the caption service should
/// translate source lines itself (Argos, via <c>SetCaptionLineTranslation</c>) and whether a live audio
/// translation engine should be constructed for the session (Gemini). Keeping the decision here instead
/// of inline in WPF handlers pins it with unit tests so the two-translation-path bug from v0.5.31 — the
/// UI promised Gemini but the runtime silently ran the offline faster-whisper + Argos path — cannot
/// regress.
/// </para>
/// </remarks>
public static class TranslationProviderPolicy
{
    /// <summary>
    /// True when the selected provider owns translation through a live audio engine. Only Gemini
    /// does today (the App's factory constructs a Gemini live-translation engine for it); Argos never
    /// uses a live audio engine.
    /// </summary>
    public static bool UsesLiveAudioEngine(TranslationProvider? provider) => provider == TranslationProvider.Gemini;

    /// <summary>
    /// True when the caption service should translate source lines with the local caption-line engine
    /// (Argos). Gemini suppresses the caption-line path because Gemini owns translation; every other
    /// selection (including null = default Argos behavior) uses it. Provider-dependent only — this is
    /// a mechanism decision, not a UI-toggle decision: the common TranslationEnabled state reflects the
    /// checkbox for every provider, and this method simply decides which path performs the translation.
    /// </summary>
    public static bool UsesCaptionLineTranslation(TranslationProvider? provider) =>
        provider != TranslationProvider.Gemini;
}
