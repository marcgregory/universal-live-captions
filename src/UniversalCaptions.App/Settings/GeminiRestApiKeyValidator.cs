using System.Net.Http;
using System.Text.Json;

namespace UniversalCaptions.App.Settings;

/// <summary>
/// Production <see cref="IGeminiApiKeyValidator"/> backed by the public Gemini REST
/// <c>v1beta/models</c> list endpoint. An invalid key is rejected with HTTP 400
/// <c>API_KEY_INVALID</c> (exactly the response observed for the corrupt credential on 2026-08-14);
/// a valid key returns 200 with the model list. The key travels as the standard <c>?key=</c> query
/// parameter and is never logged, persisted, or echoed back into exceptions.
/// </summary>
public sealed class GeminiRestApiKeyValidator : IGeminiApiKeyValidator, IDisposable
{
    /// <summary>Base endpoint of the Gemini public REST surface (the Live Translate WebSocket host).</summary>
    internal const string DefaultEndpoint = "https://generativelanguage.googleapis.com";

    private static readonly HttpClient SharedClient = CreateHttpClient();

    private readonly HttpClient? _ownedClient;
    private readonly HttpClient _client;

    /// <summary>
    /// Creates a validator using a shared <see cref="HttpClient"/> with a bounded timeout.
    /// </summary>
    public GeminiRestApiKeyValidator()
    {
        _client = SharedClient;
    }

    /// <summary>
    /// Creates a validator over an explicit <see cref="HttpClient"/> — the seam used by tests. The
    /// injected client is disposed by <see cref="Dispose"/>.
    /// </summary>
    public GeminiRestApiKeyValidator(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownedClient = client;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _ownedClient?.Dispose();
    }

    /// <inheritdoc />
    public async Task<GeminiAvailability> ValidateAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return GeminiAvailability.MissingKey;
        }

        try
        {
            using HttpResponseMessage response = await _client
                .GetAsync($"{DefaultEndpoint}/v1beta/models?key={Uri.EscapeDataString(apiKey)}", cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return GeminiAvailability.Available;
            }

            int code = (int)response.StatusCode;
            if (code is 400 or 401 or 403)
            {
                string? body = await TryReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                return IsQuotaResponse(body)
                    ? GeminiAvailability.QuotaExceeded
                    : GeminiAvailability.InvalidKey;
            }

            if (code == 429)
            {
                return GeminiAvailability.QuotaExceeded;
            }

            // 5xx and any other status: the service is reachable but not in a usable state — treat
            // as a transient network/service failure rather than a key problem.
            return GeminiAvailability.NetworkError;
        }
        catch (OperationCanceledException)
        {
            return GeminiAvailability.NetworkError;
        }
        catch (HttpRequestException)
        {
            return GeminiAvailability.NetworkError;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalCaptions/1.0");
        return client;
    }

    /// <summary>
    /// True when the error body indicates resource exhaustion (rate limit / quota) rather than a
    /// bad key. Gemini reports quota as HTTP 429 (RESOURCE_EXHAUSTED) or a 403/400 body carrying
    /// <c>RESOURCE_EXHAUSTED</c> / <c>quota</c> / <c>rate limit</c>.
    /// </summary>
    private static bool IsQuotaResponse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        if (body.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
            || body.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The API_KEY_INVALID reason must not be misread as quota. Be conservative: only parse the
        // JSON reason when it is clearly present and clearly not an API-key rejection.
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out JsonElement error)
                && error.TryGetProperty("details", out JsonElement details)
                && details.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement detail in details.EnumerateArray())
                {
                    if (detail.TryGetProperty("reason", out JsonElement reason))
                    {
                        string? reasonText = reason.GetString();
                        if (string.Equals(reasonText, "API_KEY_INVALID", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        if (string.Equals(reasonText, "RATE_LIMIT_EXCEEDED", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(reasonText, "RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to the generic invalid-key classification.
        }

        return false;
    }

    private static async Task<string?> TryReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
