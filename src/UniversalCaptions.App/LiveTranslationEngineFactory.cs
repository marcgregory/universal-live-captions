using UniversalCaptions.App.Settings;
using UniversalCaptions.Core.Translation;
using UniversalCaptions.Speech.Gemini;

namespace UniversalCaptions.App;

/// <summary>
/// Builds the session's single speech engine — the Gemini Live Translate
/// <see cref="ILiveAudioTranslationEngine"/> — from environment knobs plus the user's stored
/// credential. It lives in <c>UniversalCaptions.App</c> (the DI composition root) rather than Core so
/// the choice of engine stays a deployment decision rather than a contract that the Core layer would
/// force on every consumer (App-side factory placement, A4).
/// </summary>
/// <remarks>
/// <para>
/// ADR-0011: Gemini is the pipeline's ONLY speech engine, so this factory has no provider selection
/// and no offline fallback. Returning <c>null</c> means "no usable Gemini session" (no stored API
/// key) and the pipeline fails the session start with an actionable status.
/// </para>
/// <para>
/// API key handling (ADR-0009): the Gemini key is stored in the Windows Credential Manager under
/// the target name <c>UniversalCaptions:GeminiApiKey</c> via <see cref="ICredentialStore"/>. The
/// factory reads it once at the start of a session and passes it to
/// <see cref="GeminiLiveTranslateEngineOptions.ApiKey"/>; the value is held only by the running
/// engine and is dropped from memory on engine Dispose. The legacy <c>UC_GEMINI_API_KEY</c>
/// environment-variable fallback is not consulted in the App — the developer spike runner
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
    /// Constructs the Gemini Live engine for the (source, target) language pair. Returns <c>null</c>
    /// when no API key is stored (or the credential store fails) — the pipeline treats null as "no
    /// usable speech engine" and fails the start with an actionable message. The factory itself never
    /// throws on a failing <see cref="ICredentialStore"/>.
    /// </summary>
    /// <param name="credentialStore">
    /// The credential store to consult for the Gemini API key.
    /// </param>
    /// <param name="sourceLanguage">The ISO 639-1 source language, when known.</param>
    /// <param name="targetLanguage">The ISO 639-1 target language, when known.</param>
    /// <returns>The constructed engine, or <c>null</c> when no API key is available.</returns>
    public static ILiveAudioTranslationEngine? Create(
        ICredentialStore credentialStore,
        string? sourceLanguage,
        string? targetLanguage)
    {
        if (credentialStore is null)
        {
            // Defensive: a null store is treated the same as an empty one. The factory never throws.
            return null;
        }

        // A failing ICredentialStore must not crash the app: the lookup is wrapped so a broken store
        // degrades to "no key", which the pipeline surfaces as an actionable missing-key error.
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
            // Missing required configuration: return null; the pipeline raises the actionable error.
            return null;
        }

        var options = new GeminiLiveTranslateEngineOptions
        {
            ApiKey = apiKey,
            Model = Environment.GetEnvironmentVariable("UC_GEMINI_MODEL") ?? GeminiLiveTranslateEngineOptions.DefaultModel,
            TargetLanguage = string.IsNullOrWhiteSpace(targetLanguage) ? "tl" : targetLanguage,
            SourceLanguage = sourceLanguage,
            Endpoint = Environment.GetEnvironmentVariable("UC_GEMINI_ENDPOINT") ?? GeminiLiveTranslateEngineOptions.DefaultEndpoint,
        };

        return new GeminiLiveTranslateEngine(options, new ClientWebSocketGeminiChannel());
    }
}
