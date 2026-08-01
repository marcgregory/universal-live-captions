namespace UniversalCaptions.App.Controls;

/// <summary>
/// Validates the control window's translation settings before they are applied. A translation whose
/// target equals the caption source language is rejected up front, because every translation request
/// would fail (the translation backend cannot translate a language into itself).
/// </summary>
public static class TranslationGuard
{
    /// <summary>
    /// Returns a user-readable error message when the target language cannot be used, or null when
    /// the combination is valid.
    /// </summary>
    /// <param name="sourceLanguage">The ISO 639-1 caption source language (the language the captions are in).</param>
    /// <param name="targetLanguage">The ISO 639-1 translation target language.</param>
    /// <returns>An error message when the combination is invalid, otherwise null.</returns>
    public static string? Validate(string? sourceLanguage, string? targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return "Choose a target language before enabling translation.";
        }

        if (string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return $"Translation into {targetLanguage} is not supported because the captions are already in {sourceLanguage}.";
        }

        return null;
    }
}
