namespace UniversalCaptions.Core.Translation;

/// <summary>
/// A stable, committed translation result for a completed utterance.
/// </summary>
public sealed class FinalTranslation : TranslationTranscript
{
    /// <summary>
    /// Creates a new final translation transcript.
    /// </summary>
    /// <param name="sourceText">The original source-language text, when available; otherwise null.</param>
    /// <param name="translatedText">The translated text in the target language.</param>
    /// <param name="sourceLanguage">The ISO 639-1 code of the source language.</param>
    /// <param name="targetLanguage">The ISO 639-1 code of the target language.</param>
    /// <param name="capturedAtUtc">The time the source audio was captured (UTC).</param>
    /// <param name="emittedAtUtc">The time this transcript was produced (UTC).</param>
    /// <param name="sequence">A monotonically increasing number used to order transcripts.</param>
    /// <param name="committedAtUtc">The time the translation was finalised (UTC).</param>
    public FinalTranslation(
        string? sourceText,
        string translatedText,
        string sourceLanguage,
        string targetLanguage,
        DateTime capturedAtUtc,
        DateTime emittedAtUtc,
        long sequence,
        DateTime committedAtUtc)
        : base(sourceText, translatedText, sourceLanguage, targetLanguage, capturedAtUtc, emittedAtUtc, sequence)
    {
        CommittedAtUtc = committedAtUtc;
    }

    /// <summary>Time the translation was finalised (UTC).</summary>
    public DateTime CommittedAtUtc { get; }
}
