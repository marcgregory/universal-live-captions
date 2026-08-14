namespace UniversalCaptions.App.Settings;

/// <summary>
/// Classifies a stored Gemini API key against the live service so the UI can tell the user exactly
/// why Gemini is (or is not) usable. The production implementation
/// (<see cref="GeminiRestApiKeyValidator"/>) hits the public models list endpoint — the same surface
/// the Live Translate WebSocket uses for key validation — and tests inject a fake via
/// <see cref="System.Net.Http.HttpMessageHandler"/>. The key is never logged or persisted.
/// </summary>
public interface IGeminiApiKeyValidator
{
    /// <summary>
    /// Validates an API key against the live Gemini service.
    /// </summary>
    /// <param name="apiKey">The key to validate. Must not be null or whitespace.</param>
    /// <param name="cancellationToken">Cancels the network round-trip.</param>
    /// <returns>
    /// <see cref="GeminiAvailability.Available"/> on success; <see cref="GeminiAvailability.InvalidKey"/>
    /// for HTTP 400/401/403 (bad key / auth / permission);
    /// <see cref="GeminiAvailability.QuotaExceeded"/> for HTTP 429 / resource exhaustion;
    /// <see cref="GeminiAvailability.NetworkError"/> when the endpoint could not be reached or returned
    /// an unclassifiable response.
    /// </returns>
    Task<GeminiAvailability> ValidateAsync(string apiKey, CancellationToken cancellationToken = default);
}
