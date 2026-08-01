namespace UniversalCaptions.Core.Translation;

/// <summary>
/// Translates text from a source language to a target language.
/// Implementations are engine-neutral: text in, <see cref="TranslationResult"/> out.
/// </summary>
public interface ITranslationEngine
{
    /// <summary>
    /// Translates <paramref name="text"/> from <paramref name="sourceLanguage"/> to
    /// <paramref name="targetLanguage"/>.
    /// </summary>
    /// <param name="text">The text to translate. Must not be null or empty.</param>
    /// <param name="sourceLanguage">
    /// The ISO 639-1 language code of the source text, or null to request auto-detection.
    /// </param>
    /// <param name="targetLanguage">The ISO 639-1 language code to translate into.</param>
    /// <param name="cancellationToken">Cancels the in-flight translation if supported.</param>
    /// <returns>The translation result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="TranslationException">Translation failed (see <see cref="TranslationErrorKind"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<TranslationResult> TranslateAsync(
        string text,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
