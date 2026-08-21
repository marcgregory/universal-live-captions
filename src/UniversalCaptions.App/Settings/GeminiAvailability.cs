namespace UniversalCaptions.App.Settings;

/// <summary>
/// The runtime availability of the Gemini engine. The control window reflects this in the API-key
/// panel: captions can only start when a usable key is stored, and a definitive
/// key problem (missing / malformed / server-rejected) keeps the start blocked until the key is fixed.
/// Transient states (<see cref="NetworkError"/>, <see cref="QuotaExceeded"/>, <see cref="Unknown"/>)
/// keep Gemini selectable so a temporary outage does not lock the user out of their configured key.
/// </summary>
public enum GeminiAvailability
{
    /// <summary>Not yet evaluated (initial state before the control window runs its check).</summary>
    Unknown,

    /// <summary>A Gemini API key is present and (at least syntactically) usable.</summary>
    Available,

    /// <summary>No Gemini API key is stored in the credential store.</summary>
    MissingKey,

    /// <summary>A credential is stored but it does not look like a Gemini API key.</summary>
    MalformedKey,

    /// <summary>The server rejected the stored key (HTTP 400/401/403, e.g. API_KEY_INVALID).</summary>
    InvalidKey,

    /// <summary>The server throttled the key (HTTP 429 / RESOURCE_EXHAUSTED).</summary>
    QuotaExceeded,

    /// <summary>The key could not be validated because the endpoint was unreachable.</summary>
    NetworkError,
}
