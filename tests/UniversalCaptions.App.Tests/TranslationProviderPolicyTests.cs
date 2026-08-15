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

    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData(null)]
    [InlineData("")]
    public void IsSourceLanguageEnabled_Argos_EnglishAndAuto_Enabled(string? sourceCode)
    {
        Assert.True(TranslationProviderPolicy.IsSourceLanguageEnabled(TranslationProvider.Argos, sourceCode));
        Assert.True(TranslationProviderPolicy.IsSourceLanguageEnabled(null, sourceCode));
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("tl")]
    public void IsSourceLanguageEnabled_Argos_OtherSources_Disabled(string sourceCode)
    {
        Assert.False(TranslationProviderPolicy.IsSourceLanguageEnabled(TranslationProvider.Argos, sourceCode));
        Assert.False(TranslationProviderPolicy.IsSourceLanguageEnabled(null, sourceCode));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("tl")]
    [InlineData(null)]
    public void IsSourceLanguageEnabled_Gemini_AllSources_Enabled(string? sourceCode)
    {
        Assert.True(TranslationProviderPolicy.IsSourceLanguageEnabled(TranslationProvider.Gemini, sourceCode));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("tl")]
    public void IsTargetLanguageEnabled_Argos_EnJaTl_Enabled(string targetCode)
    {
        Assert.True(TranslationProviderPolicy.IsTargetLanguageEnabled(TranslationProvider.Argos, targetCode));
        Assert.True(TranslationProviderPolicy.IsTargetLanguageEnabled(null, targetCode));
    }

    [Theory]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    [InlineData("pt-PT")]
    [InlineData("ru")]
    [InlineData("ar")]
    [InlineData("hi")]
    [InlineData("id")]
    [InlineData("ms")]
    [InlineData("th")]
    [InlineData("vi")]
    [InlineData("af")]
    [InlineData("ak")]
    [InlineData("sq")]
    [InlineData("am")]
    [InlineData("hy")]
    [InlineData("az")]
    [InlineData("eu")]
    [InlineData("be")]
    [InlineData("bn")]
    [InlineData("bg")]
    [InlineData("my")]
    [InlineData("ca")]
    [InlineData("ceb")]
    [InlineData("hr")]
    [InlineData("cs")]
    [InlineData("da")]
    [InlineData("nl")]
    [InlineData("et")]
    [InlineData("fi")]
    [InlineData("gl")]
    [InlineData("ka")]
    [InlineData("el")]
    [InlineData("gu")]
    [InlineData("ha")]
    [InlineData("he")]
    [InlineData("hu")]
    [InlineData("is")]
    [InlineData("it")]
    [InlineData("jv")]
    [InlineData("kn")]
    [InlineData("kk")]
    [InlineData("km")]
    [InlineData("rw")]
    [InlineData("lo")]
    [InlineData("lv")]
    [InlineData("lt")]
    [InlineData("mk")]
    [InlineData("ml")]
    [InlineData("mr")]
    [InlineData("mn")]
    [InlineData("ne")]
    [InlineData("no")]
    [InlineData("fa")]
    [InlineData("pl")]
    [InlineData("pa")]
    [InlineData("ro")]
    [InlineData("sr")]
    [InlineData("sd")]
    [InlineData("si")]
    [InlineData("sk")]
    [InlineData("sl")]
    [InlineData("su")]
    [InlineData("sw")]
    [InlineData("sv")]
    [InlineData("ta")]
    [InlineData("te")]
    [InlineData("tr")]
    [InlineData("uk")]
    [InlineData("ur")]
    [InlineData("uz")]
    [InlineData("zu")]
    public void IsTargetLanguageEnabled_Argos_GeminiOnlyTargets_Disabled(string targetCode)
    {
        Assert.False(TranslationProviderPolicy.IsTargetLanguageEnabled(TranslationProvider.Argos, targetCode));
        Assert.False(TranslationProviderPolicy.IsTargetLanguageEnabled(null, targetCode));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("tl")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    [InlineData("pt-PT")]
    [InlineData("ru")]
    [InlineData("ar")]
    [InlineData("hi")]
    [InlineData("id")]
    [InlineData("ms")]
    [InlineData("th")]
    [InlineData("vi")]
    [InlineData("af")]
    [InlineData("ak")]
    [InlineData("sq")]
    [InlineData("am")]
    [InlineData("hy")]
    [InlineData("az")]
    [InlineData("eu")]
    [InlineData("be")]
    [InlineData("bn")]
    [InlineData("bg")]
    [InlineData("my")]
    [InlineData("ca")]
    [InlineData("ceb")]
    [InlineData("hr")]
    [InlineData("cs")]
    [InlineData("da")]
    [InlineData("nl")]
    [InlineData("et")]
    [InlineData("fi")]
    [InlineData("gl")]
    [InlineData("ka")]
    [InlineData("el")]
    [InlineData("gu")]
    [InlineData("ha")]
    [InlineData("he")]
    [InlineData("hu")]
    [InlineData("is")]
    [InlineData("it")]
    [InlineData("jv")]
    [InlineData("kn")]
    [InlineData("kk")]
    [InlineData("km")]
    [InlineData("rw")]
    [InlineData("lo")]
    [InlineData("lv")]
    [InlineData("lt")]
    [InlineData("mk")]
    [InlineData("ml")]
    [InlineData("mr")]
    [InlineData("mn")]
    [InlineData("ne")]
    [InlineData("no")]
    [InlineData("fa")]
    [InlineData("pl")]
    [InlineData("pa")]
    [InlineData("ro")]
    [InlineData("sr")]
    [InlineData("sd")]
    [InlineData("si")]
    [InlineData("sk")]
    [InlineData("sl")]
    [InlineData("su")]
    [InlineData("sw")]
    [InlineData("sv")]
    [InlineData("ta")]
    [InlineData("te")]
    [InlineData("tr")]
    [InlineData("uk")]
    [InlineData("ur")]
    [InlineData("uz")]
    [InlineData("zu")]
    public void IsTargetLanguageEnabled_Gemini_AllTargets_Enabled(string targetCode)
    {
        Assert.True(TranslationProviderPolicy.IsTargetLanguageEnabled(TranslationProvider.Gemini, targetCode));
    }
}
