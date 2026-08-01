namespace UniversalCaptions.Core.Translation;

/// <summary>
/// Categorizes translation failures so they can be surfaced to the user.
/// </summary>
public enum TranslationErrorKind
{
    /// <summary>The requested source or target language code is not supported.</summary>
    UnsupportedLanguage,

    /// <summary>The same language was requested as both source and target.</summary>
    SourceEqualsTarget,

    /// <summary>No model is available for the requested language pair, with or without pivoting.</summary>
    LanguagePairNotSupported,

    /// <summary>The language pair is supported but its model is not installed locally.</summary>
    ModelNotInstalled,

    /// <summary>The translation process could not be started or connected to.</summary>
    EngineUnavailable,

    /// <summary>The translation engine failed at runtime.</summary>
    EngineFailed,

    /// <summary>The engine did not respond before its timeout elapsed.</summary>
    Timeout,

    /// <summary>The input text is empty.</summary>
    EmptyInput,

    /// <summary>An unexpected failure occurred.</summary>
    Unknown,
}

/// <summary>
/// A user-readable translation failure. Thrown by <see cref="ITranslationEngine"/> implementations.
/// </summary>
public sealed class TranslationException : Exception
{
    /// <summary>
    /// Creates a new translation exception.
    /// </summary>
    /// <param name="kind">The failure category.</param>
    /// <param name="message">A message suitable for display to the user.</param>
    /// <param name="innerException">The underlying exception, when available.</param>
    public TranslationException(TranslationErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>The failure category.</summary>
    public TranslationErrorKind Kind { get; }
}
