namespace UniversalCaptions.Core.Translation;

/// <summary>
/// Categorizes live-translation failures so they can be surfaced to the user. Distinct from
/// <see cref="TranslationErrorKind"/> (the text-translation path used by <see cref="ITranslationEngine"/>);
/// live audio translation has its own failure modes that do not apply to batch translation.
/// </summary>
public enum LiveTranslationErrorKind
{
    /// <summary>The live session could not be opened (network, authentication, or endpoint rejection).</summary>
    ConnectionFailed,

    /// <summary>The session was rejected by the server (invalid API key, unsupported model, or quota).</summary>
    SessionRejected,

    /// <summary>The server throttled the session (HTTP 429 / RESOURCE_EXHAUSTED / quota or rate limit).</summary>
    QuotaExceeded,

    /// <summary>The engine emitted a server-side error frame that ended the session.</summary>
    ServerError,

    /// <summary>
    /// The server ended the session gracefully (a <c>goAway</c> frame), typically because the
    /// session ran past the provider's wall-clock limit (for example Gemini's audio-only session
    /// cap) rather than because of a request failure. Expected and recoverable — the user restarts
    /// the session to resume.
    /// </summary>
    SessionEnded,

    /// <summary>The engine timed out waiting for new output from the server.</summary>
    Timeout,

    /// <summary>An unexpected failure occurred inside the engine.</summary>
    Unknown,
}

/// <summary>
/// A user-readable live-translation failure. Raised via <see cref="ILiveAudioTranslationEngine.TranslationFailed"/>.
/// </summary>
public sealed class LiveTranslationError
{
    /// <summary>
    /// Creates a new live-translation error.
    /// </summary>
    /// <param name="kind">The failure category.</param>
    /// <param name="message">A message suitable for display to the user.</param>
    /// <param name="exception">The underlying exception, when available.</param>
    public LiveTranslationError(LiveTranslationErrorKind kind, string message, Exception? exception = null)
    {
        Kind = kind;
        Message = message;
        Exception = exception;
    }

    /// <summary>The failure category.</summary>
    public LiveTranslationErrorKind Kind { get; }

    /// <summary>A message suitable for display to the user.</summary>
    public string Message { get; }

    /// <summary>The underlying exception, when available.</summary>
    public Exception? Exception { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind}: {Message}";
}
