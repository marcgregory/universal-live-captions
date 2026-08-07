using UniversalCaptions.Core.Translation;
using UniversalCaptions.Speech.Gemini;

namespace UniversalCaptions.App;

/// <summary>
/// Builds the optional <see cref="ILiveAudioTranslationEngine"/> from environment knobs. The
/// companion of <see cref="SpeechEngineFactory"/>: it lives in <c>UniversalCaptions.App</c> (the DI
/// composition root) rather than <c>UniversalCaptions.Core.Translation</c> so the choice of provider
/// — none, Gemini Live Translate, future cloud providers — stays a deployment decision rather than a
/// contract that the Core layer would force on every consumer (App-side factory placement, A4).
/// </summary>
/// <remarks>
/// <para>
/// The selection table mirrors the speech factory's surface: the <c>UC_LIVE_TRANSLATION_PROVIDER</c>
/// environment variable picks the implementation. <c>unset</c> / <c>none</c> / empty → no live
/// translation engine is created (the default; the App composes an offline-only pipeline). <c>gemini</c>
/// → wires a <see cref="GeminiLiveTranslateEngine"/> with the API key from <c>UC_GEMINI_API_KEY</c>.
/// </para>
/// <para>
/// Returning <c>null</c> is the intended default — the caption pipeline accepts a null
/// <see cref="ILiveAudioTranslationEngine"/> and silently skips PCM fan-out. The factory itself
/// never throws on an unset/unknown provider; a real provider that requires configuration must do its
/// own validation and surface failures through <see cref="ILiveAudioTranslationEngine.TranslationFailed"/>
/// (not via factory-time exceptions), so the pipeline stays resilient to a misconfigured live
/// translation path.
/// </para>
/// <para>
/// API key handling: the engine's constructor requires the key directly. The factory reads it from
/// <c>UC_GEMINI_API_KEY</c> as a transient mechanism. The App's intended future path is to load the
/// key from the Windows Credential Manager; the env-var surface is the A6 wire-up and is documented
/// in the landing page as a setup step. The factory never logs the key, never includes it in
/// exceptions, and never passes it through diagnostics.
/// </para>
/// </remarks>
public static class LiveTranslationEngineFactory
{
    /// <summary>
    /// Selects and constructs the live-audio translation engine from <c>UC_LIVE_TRANSLATION_PROVIDER</c>.
    /// Returns <c>null</c> when the provider is unset, empty, set to <c>none</c>, set to an unknown
    /// value, or when the chosen provider is missing its required configuration (for example, the
    /// Gemini engine without <c>UC_GEMINI_API_KEY</c>). The pipeline treats a null return as
    /// "no live translation engine", which silently degrades to the offline-only pipeline.
    /// </summary>
    /// <param name="sourceLanguage">The ISO 639-1 source language, when known.</param>
    /// <param name="targetLanguage">The ISO 639-1 target language, when known.</param>
    /// <returns>The constructed engine, or <c>null</c> when no live translation is configured.</returns>
    public static ILiveAudioTranslationEngine? Create(string? sourceLanguage, string? targetLanguage)
    {
        string provider = Environment.GetEnvironmentVariable("UC_LIVE_TRANSLATION_PROVIDER")?.Trim().ToLowerInvariant() ?? string.Empty;

        if (string.IsNullOrEmpty(provider) || provider == "none" || provider == "off")
        {
            return null;
        }

        if (provider == "gemini")
        {
            string? apiKey = Environment.GetEnvironmentVariable("UC_GEMINI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                // Missing required configuration: return null and let the offline pipeline run. The
                // factory never throws on a misconfigured provider — the pipeline stays resilient.
                return null;
            }

            var options = new GeminiLiveTranslateEngineOptions
            {
                ApiKey = apiKey,
                Model = Environment.GetEnvironmentVariable("UC_GEMINI_MODEL") ?? GeminiLiveTranslateEngineOptions.DefaultModel,
                TargetLanguage = string.IsNullOrWhiteSpace(targetLanguage) ? "tl" : targetLanguage,
                SourceLanguage = sourceLanguage,
                SystemInstruction = Environment.GetEnvironmentVariable("UC_GEMINI_SYSTEM_INSTRUCTION"),
                Endpoint = Environment.GetEnvironmentVariable("UC_GEMINI_ENDPOINT") ?? GeminiLiveTranslateEngineOptions.DefaultEndpoint,
            };

            return new GeminiLiveTranslateEngine(options, new ClientWebSocketGeminiChannel());
        }

        // Unknown provider: degrade gracefully to the offline-only pipeline. Logging here would be
        // premature — the App's own startup diagnostics surface the active provider to the user.
        return null;
    }
}
