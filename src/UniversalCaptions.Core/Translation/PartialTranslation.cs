namespace UniversalCaptions.Core.Translation;

/// <summary>
/// An in-progress translation result. The engine may revise it via later partials or replace it
/// with a <see cref="FinalTranslation"/>.
/// </summary>
public sealed class PartialTranslation : TranslationTranscript
{
    /// <summary>
    /// Creates a new partial translation transcript.
    /// </summary>
    /// <param name="sourceText">The original source-language text, when available; otherwise null.</param>
    /// <param name="translatedText">The translated text in the target language.</param>
    /// <param name="sourceLanguage">The ISO 639-1 code of the source language.</param>
    /// <param name="targetLanguage">The ISO 639-1 code of the target language.</param>
    /// <param name="capturedAtUtc">The time the source audio was captured (UTC).</param>
    /// <param name="emittedAtUtc">The time this transcript was produced (UTC).</param>
    /// <param name="sequence">A monotonically increasing number used to order transcripts.</param>
    public PartialTranslation(
        string? sourceText,
        string translatedText,
        string sourceLanguage,
        string targetLanguage,
        DateTime capturedAtUtc,
        DateTime emittedAtUtc,
        long sequence)
        : base(sourceText, translatedText, sourceLanguage, targetLanguage, capturedAtUtc, emittedAtUtc, sequence)
    {
    }
}
