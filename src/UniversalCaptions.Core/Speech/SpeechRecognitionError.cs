namespace UniversalCaptions.Core.Speech;

/// <summary>
/// Categorizes speech recognition failures so they can be surfaced to the user.
/// </summary>
public enum SpeechRecognitionErrorKind
{
    /// <summary>The configured model file could not be found.</summary>
    ModelNotFound,

    /// <summary>The model file exists but could not be loaded.</summary>
    ModelLoadFailed,

    /// <summary>The engine received audio in a format it cannot process.</summary>
    InvalidAudioFormat,

    /// <summary>The recognition engine failed at runtime.</summary>
    EngineFailed,

    /// <summary>An unexpected failure occurred.</summary>
    Unknown,
}

/// <summary>
/// A user-readable recognition failure. Raised via <see cref="ISpeechToTextEngine.RecognitionFailed"/>.
/// </summary>
public sealed class SpeechRecognitionError
{
    /// <summary>
    /// Creates a new recognition error.
    /// </summary>
    /// <param name="kind">The failure category.</param>
    /// <param name="message">A message suitable for display to the user.</param>
    /// <param name="exception">The underlying exception, when available.</param>
    public SpeechRecognitionError(SpeechRecognitionErrorKind kind, string message, Exception? exception = null)
    {
        Kind = kind;
        Message = message;
        Exception = exception;
    }

    /// <summary>The failure category.</summary>
    public SpeechRecognitionErrorKind Kind { get; }

    /// <summary>A message suitable for display to the user.</summary>
    public string Message { get; }

    /// <summary>The underlying exception, when available.</summary>
    public Exception? Exception { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind}: {Message}";
}

/// <summary>
/// Thrown when a speech-to-text engine cannot be created or started (for example, the model is missing).
/// </summary>
public sealed class SpeechRecognitionException : Exception
{
    /// <summary>
    /// Creates a new speech recognition exception.
    /// </summary>
    /// <param name="kind">The failure category.</param>
    /// <param name="message">A message suitable for display to the user.</param>
    /// <param name="innerException">The underlying exception, when available.</param>
    public SpeechRecognitionException(SpeechRecognitionErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>The failure category.</summary>
    public SpeechRecognitionErrorKind Kind { get; }
}
