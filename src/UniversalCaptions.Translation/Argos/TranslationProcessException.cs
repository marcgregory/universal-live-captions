using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.Translation.Argos;

/// <summary>
/// Thrown when a local Argos process cannot be started, cannot be reached, or fails at runtime.
/// </summary>
internal sealed class TranslationProcessException : Exception
{
    public TranslationProcessException(TranslationErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public TranslationErrorKind Kind { get; }
}
