using UniversalCaptions.App.Settings;
using UniversalCaptions.Core.Translation;
using UniversalCaptions.Speech.Gemini;

namespace UniversalCaptions.App;

/// <summary>
/// Builds the optional <see cref="ILiveAudioTranslationEngine"/> from environment knobs plus the
/// user's stored credential. The companion of <see cref="SpeechEngineFactory"/>: it lives in
/// <c>UniversalCaptions.App</c> (the DI composition root) rather than
/// <c>UniversalCaptions.Core.Translation</c> so the choice of provider — none, Gemini Live
/// Translate, future cloud providers — stays a deployment decision rather than a contract that the
/// Core layer would force on every consumer (App-side factory placement, A4).
/// </summary>
/// <remarks>
/// <para>
/// The selection table mirrors the speech factory's surface: the <c>UC_LIVE_TRANSLATION_PROVIDER</c>
/// environment variable picks the implementation. <c>unset</c> / <c>none</c> / empty → no live
/// translation engine is created (the default; the App composes an offline-only pipeline). <c>gemini</c>
/// → wires a <see cref="GeminiLiveTranslateEngine"/> with the API key read from
/// <see cref="ICredentialStore"/>.
/// </para>
/// <para>
/// Returning <c>null</c> is the intended default — the caption pipeline accepts a null
/// <see cref="ILiveAudioTranslationEngine"/> and silently skips PCM fan-out. The factory itself
/// never throws on an unset/unknown provider; a real provider that requires configuration must do
/// its own validation and surface failures through
/// <see cref="ILiveAudioTranslationEngine.TranslationFailed"/> (not via factory-time exceptions),
/// so the pipeline stays resilient to a misconfigured live translation path.
/// </para>
/// <para>
/// API key handling (ADR-0009): the Gemini key is stored in the Windows Credential Manager under
/// the target name <c>UniversalCaptions:GeminiApiKey</c> via <see cref="ICredentialStore"/>. The
/// factory reads it once at the start of a Gemini session and passes it to
/// <see cref="GeminiLiveTranslateEngineOptions.ApiKey"/>; the value is held only by the running
/// engine and is dropped from memory on engine Dispose. The legacy <c>UC_GEMINI_API_KEY</c>
/// environment-variable fallback is no longer consulted in the App — the developer spike runner
/// (<c>tools/GeminiDirectWireSpike</c>) retains the env-var path for offline wire testing. The
/// factory never logs the key, never includes it in exceptions, and never passes it through
/// diagnostics.
/// </para>
/// </remarks>
public static class LiveTranslationEngineFactory
{
    /// <summary>
    /// Credential target name used by the App for the Gemini API key in Windows Credential Manager.
    /// Exposed <c>internal</c> so tests can probe the same target the factory reads.
    /// </summary>
    internal const string GeminiApiKeyTarget = "UniversalCaptions:GeminiApiKey";

    /// <summary>
    /// Selects and constructs the live-audio translation engine from <c>UC_LIVE_TRANSLATION_PROVIDER</c>
    /// and the user's stored credential. Returns <c>null</c> when the provider is unset, empty, set to
    /// <c>none</c>, set to an unknown value, or when the chosen provider is missing its required
    /// configuration (for example, the Gemini engine without a stored API key). The pipeline treats
    /// a null return as "no live translation engine", which silently degrades to the offline-only
    /// pipeline. The factory itself never throws on a misconfigured or failing
    /// <see cref="ICredentialStore"/> — that would break the offline pipeline.
    /// </summary>
    /// <param name="credentialStore">
    /// The credential store to consult for the Gemini API key. The factory never throws if this
    /// store fails; it returns <c>null</c> instead.
    /// </param>
    /// <param name="sourceLanguage">The ISO 639-1 source language, when known.</param>
    /// <param name="targetLanguage">The ISO 639-1 target language, when known.</param>
    /// <returns>The constructed engine, or <c>null</c> when no live translation is configured.</returns>
    public static ILiveAudioTranslationEngine? Create(ICredentialStore credentialStore, string? sourceLanguage, string? targetLanguage)
    {
        if (credentialStore is null)
        {
            // Defensive: a null store is treated the same as an empty one. The factory never throws.
            return null;
        }

        string provider = Environment.GetEnvironmentVariable("UC_LIVE_TRANSLATION_PROVIDER")?.Trim().ToLowerInvariant() ?? string.Empty;

        if (string.IsNullOrEmpty(provider) || provider == "none" || provider == "off")
        {
            return null;
        }

        if (provider == "gemini")
        {
            // A failing ICredentialStore must not break the offline-only pipeline (ADR-0009
            // invariant: the factory never throws). The lookup is wrapped so a broken store
            // degrades to "no Gemini engine", same as a missing key.
            string? apiKey;
            try
            {
                apiKey = credentialStore.TryGetCredential(GeminiApiKeyTarget);
            }
            catch (Exception)
            {
                // The factory never logs the key, never includes the key in exceptions, and never
                // surfaces store-failure details to the user — that would leak store internals. A
                // null return is the safe, documented degradation path.
                return null;
            }

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
