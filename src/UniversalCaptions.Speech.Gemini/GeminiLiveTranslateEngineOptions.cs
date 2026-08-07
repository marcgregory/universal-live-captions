namespace UniversalCaptions.Speech.Gemini;

/// <summary>
/// Configuration for <see cref="GeminiLiveTranslateEngine"/>. The API key is required: the
/// App loads it from the Windows Credential Manager and passes it in via the constructor. The
/// engine never logs, persists, or includes the key in any exception message or diagnostic
/// surface.
/// </summary>
public sealed class GeminiLiveTranslateEngineOptions
{
    /// <summary>
    /// Default Gemini model used by the Live Translate API. VERIFIED 2026-08-08 against Google's
    /// current docs at https://ai.google.dev/gemini-api/docs/live-api/live-translate — the model
    /// id is <c>gemini-3.5-live-translate-preview</c> and the setup-frame <c>model</c> field
    /// carries the <c>models/</c> prefix.
    /// </summary>
    public const string DefaultModel = "models/gemini-3.5-live-translate-preview";

    /// <summary>
    /// Default WebSocket endpoint for the Gemini Live Translate service. VERIFIED 2026-08-08 via
    /// the real-wire spike — the WebSocket handshake completes against this URL. The server then
    /// validates the model identifier + setup frame against the Live Translate contract.
    /// </summary>
    public const string DefaultEndpoint = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    /// <summary>
    /// Default BCP-47 target language code passed in <c>translationConfig.targetLanguageCode</c>.
    /// The Gemini Live Translate API requires BCP-47 (for example <c>fil</c> for Filipino /
    /// Tagalog), NOT ISO 639-1 (which would be <c>tl</c>). The engine maps the App's
    /// <see cref="TargetLanguage"/> ISO 639-1 code to a BCP-47 equivalent via
    /// <see cref="ResolveTargetLanguageCode"/>; callers can override by setting
    /// <see cref="TargetLanguageCode"/> directly.
    /// </summary>
    public const string DefaultTargetLanguageCode = "fil";

    /// <summary>User-supplied Gemini API key. Required; never logged or surfaced in errors.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gemini model identifier, sent as the <c>model</c> field in the setup frame (with the
    /// <c>models/</c> prefix). Defaults to <see cref="DefaultModel"/>.
    /// </summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>
    /// ISO 639-1 target language the App wants translation into. The engine maps this to a
    /// BCP-47 code via <see cref="ResolveTargetLanguageCode"/> for the Gemini setup frame; the
    /// App-side display + filter logic still uses this ISO 639-1 form.
    /// </summary>
    public string TargetLanguage { get; set; } = "tl";

    /// <summary>
    /// Explicit BCP-47 override for <c>translationConfig.targetLanguageCode</c>. When non-null
    /// and non-whitespace, this value wins over the auto-resolved BCP-47 mapping. Set this if
    /// you need a locale the App-side ISO 639-1 vocabulary doesn't cover (for example
    /// <c>zh-Hant</c> instead of <c>zh</c>).
    /// </summary>
    public string? TargetLanguageCode { get; set; }

    /// <summary>
    /// Optional source language hint. The server auto-detects when null; supply a value to
    /// constrain detection.
    /// </summary>
    public string? SourceLanguage { get; set; }

    /// <summary>
    /// Optional system-instruction nudge. STATUS (2026-08-08, Google's Live Translate docs):
    /// REJECTED by the server — "Pure low-latency translation; no support for tools or
    /// instructions." The property is retained on the options object so existing call-sites
    /// compile, but <see cref="GeminiLiveTranslateProtocol.BuildSetupFrame"/> does NOT include
    /// it in the setup frame. A future product change can re-introduce it if/when Google
    /// accepts system instructions on this surface.
    /// </summary>
    public string? SystemInstruction { get; set; }

    /// <summary>WebSocket endpoint URI. Defaults to the documented Gemini Live Translate URL.</summary>
    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>
    /// Builds the WebSocket URI with the API key as a query parameter.
    /// </summary>
    public Uri BuildEndpoint()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException("API key is required.");
        }

        string endpoint = string.IsNullOrWhiteSpace(Endpoint) ? DefaultEndpoint : Endpoint;
        if (endpoint.Contains("?"))
        {
            return new Uri($"{endpoint}&key={Uri.EscapeDataString(ApiKey)}");
        }

        return new Uri($"{endpoint}?key={Uri.EscapeDataString(ApiKey)}");
    }

    /// <summary>
    /// Resolves the BCP-47 <c>targetLanguageCode</c> for the setup frame. Explicit
    /// <see cref="TargetLanguageCode"/> override wins; otherwise the App-side
    /// <see cref="TargetLanguage"/> ISO 639-1 code is mapped via the small built-in table.
    /// </summary>
    public string ResolveTargetLanguageCode()
    {
        if (!string.IsNullOrWhiteSpace(TargetLanguageCode))
        {
            return TargetLanguageCode.Trim();
        }

        string iso = TargetLanguage?.Trim().ToLowerInvariant() ?? string.Empty;
        return iso switch
        {
            "tl" => "fil",
            "en" => "en",
            "ja" => "ja",
            "ko" => "ko",
            "zh" => "zh",
            "es" => "es",
            "fr" => "fr",
            "de" => "de",
            "pt" => "pt",
            "ru" => "ru",
            "ar" => "ar",
            "hi" => "hi",
            "id" => "id",
            "ms" => "ms",
            "th" => "th",
            "vi" => "vi",
            _ => DefaultTargetLanguageCode,
        };
    }
}
