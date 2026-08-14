using UniversalCaptions.App.Settings;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Pins the provider → translation-path policy: exactly one translation engine may own the captions.
/// The policy answers only HOW translation is performed for the selected provider — it never drives the
/// common UI state (the Translate checkbox/target dropdown/badge behave identically for every provider).
/// When Gemini is selected the live Gemini engine owns translation and the Argos caption-line path is
/// suppressed; when Argos (or the offline default) is selected the caption-line engine owns it.
/// Regression guard for the v0.5.31 bug where the UI promised Gemini but the runtime ran the offline
/// faster-whisper + Argos path — the two paths must never both fill the overlay.
/// </summary>
public class TranslationProviderPolicyTests
{
    [Theory]
    [InlineData(TranslationProvider.Argos)]
    [InlineData(null)]
    public void UsesCaptionLineTranslation_NonGemini_True(TranslationProvider? provider)
    {
        // The caption-line mechanism is provider-dependent only — it is independent of the UI toggle,
        // which is owned by the common translation state, not by the provider policy.
        Assert.True(TranslationProviderPolicy.UsesCaptionLineTranslation(provider));
    }

    [Fact]
    public void UsesCaptionLineTranslation_Gemini_Always_False()
    {
        // Gemini owns translation through its live audio engine — the Argos caption-line path must
        // never be enabled, so no Argos translation request/worker is started for caption lines.
        Assert.False(TranslationProviderPolicy.UsesCaptionLineTranslation(TranslationProvider.Gemini));
    }

    [Theory]
    [InlineData(TranslationProvider.Gemini, true)]
    [InlineData(TranslationProvider.Argos, false)]
    [InlineData(null, false)]
    public void UsesLiveAudioEngine_Only_Gemini(TranslationProvider? provider, bool expected)
    {
        Assert.Equal(expected, TranslationProviderPolicy.UsesLiveAudioEngine(provider));
    }

    [Theory]
    [InlineData(GeminiAvailability.Available, true)]
    [InlineData(GeminiAvailability.Unknown, true)]
    [InlineData(GeminiAvailability.NetworkError, true)]
    [InlineData(GeminiAvailability.QuotaExceeded, true)]
    [InlineData(GeminiAvailability.MissingKey, false)]
    [InlineData(GeminiAvailability.MalformedKey, false)]
    [InlineData(GeminiAvailability.InvalidKey, false)]
    public void IsProviderSelectable_Gemini_ReflectsAvailability(GeminiAvailability availability, bool expected)
    {
        Assert.Equal(expected, TranslationProviderPolicy.IsProviderSelectable(TranslationProvider.Gemini, availability));
    }

    [Theory]
    [InlineData(GeminiAvailability.MissingKey)]
    [InlineData(GeminiAvailability.MalformedKey)]
    [InlineData(GeminiAvailability.InvalidKey)]
    [InlineData(GeminiAvailability.QuotaExceeded)]
    [InlineData(GeminiAvailability.NetworkError)]
    [InlineData(GeminiAvailability.Unknown)]
    [InlineData(GeminiAvailability.Available)]
    public void IsProviderSelectable_Argos_Always_True(GeminiAvailability availability)
    {
        Assert.True(TranslationProviderPolicy.IsProviderSelectable(TranslationProvider.Argos, availability));
        Assert.True(TranslationProviderPolicy.IsProviderSelectable(null, availability));
    }

    [Theory]
    [InlineData(GeminiAvailability.MissingKey)]
    [InlineData(GeminiAvailability.MalformedKey)]
    [InlineData(GeminiAvailability.InvalidKey)]
    public void ResolveActiveProvider_GeminiUnavailable_FallsBackToArgos(GeminiAvailability availability)
    {
        Assert.Equal(TranslationProvider.Argos, TranslationProviderPolicy.ResolveActiveProvider(TranslationProvider.Gemini, availability));
    }

    [Theory]
    [InlineData(GeminiAvailability.Available)]
    [InlineData(GeminiAvailability.Unknown)]
    [InlineData(GeminiAvailability.NetworkError)]
    [InlineData(GeminiAvailability.QuotaExceeded)]
    public void ResolveActiveProvider_GeminiSelectable_StaysGemini(GeminiAvailability availability)
    {
        Assert.Equal(TranslationProvider.Gemini, TranslationProviderPolicy.ResolveActiveProvider(TranslationProvider.Gemini, availability));
    }

    [Fact]
    public void ResolveActiveProvider_Null_DefaultsToArgos()
    {
        Assert.Equal(TranslationProvider.Argos, TranslationProviderPolicy.ResolveActiveProvider(null, GeminiAvailability.InvalidKey));
        Assert.Equal(TranslationProvider.Argos, TranslationProviderPolicy.ResolveActiveProvider(null, GeminiAvailability.Available));
    }
}
