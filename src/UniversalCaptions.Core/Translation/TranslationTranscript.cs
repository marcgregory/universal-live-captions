namespace UniversalCaptions.Core.Translation;

/// <summary>
/// A translation event produced by a live translation engine (for example, Gemini Live Translate).
/// Carries the source text (when the engine provides it; otherwise null), the translated text, the
/// languages involved, and ordering/capture timestamps. The abstract base deliberately mirrors
/// <see cref="UniversalCaptions.Core.Speech.SpeechTranscript"/> in shape so the caption pipeline can
/// treat STT and live translation as parallel event lineages.
/// </summary>
/// <remarks>
/// Translation and speech recognition are kept as separate lineages. STT produces language detected
/// from audio; live translation produces text that has already been translated server-side and
/// arrives with the target language. The two streams flow into the same <c>CaptionState</c> but
/// stay distinguishable via <c>Origin</c> on the resulting <c>CaptionLine</c>.
/// </remarks>
public abstract class TranslationTranscript
{
    /// <summary>
    /// Creates a new translation transcript.
    /// </summary>
    /// <param name="sourceText">The original source-language text, when the engine provides it; otherwise null.</param>
    /// <param name="translatedText">The translated text in the target language.</param>
    /// <param name="sourceLanguage">The ISO 639-1 code of the source language.</param>
    /// <param name="targetLanguage">The ISO 639-1 code of the target language.</param>
    /// <param name="capturedAtUtc">The time the source audio was captured (UTC).</param>
    /// <param name="emittedAtUtc">The time this transcript was produced (UTC).</param>
    /// <param name="sequence">A monotonically increasing number used to order transcripts.</param>
    protected TranslationTranscript(
        string? sourceText,
        string translatedText,
        string sourceLanguage,
        string targetLanguage,
        DateTime capturedAtUtc,
        DateTime emittedAtUtc,
        long sequence)
    {
        SourceText = sourceText;
        TranslatedText = translatedText;
        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
        CapturedAtUtc = capturedAtUtc;
        EmittedAtUtc = emittedAtUtc;
        Sequence = sequence;
    }

    /// <summary>The original source-language text, when the engine provides it; otherwise null.</summary>
    public string? SourceText { get; }

    /// <summary>The translated text in the target language.</summary>
    public string TranslatedText { get; }

    /// <summary>The ISO 639-1 code of the source language.</summary>
    public string SourceLanguage { get; }

    /// <summary>The ISO 639-1 code of the target language.</summary>
    public string TargetLanguage { get; }

    /// <summary>Time the source audio was captured (UTC). Used for latency measurement.</summary>
    public DateTime CapturedAtUtc { get; }

    /// <summary>Time this transcript was produced (UTC). Used for latency measurement.</summary>
    public DateTime EmittedAtUtc { get; }

    /// <summary>Monotonically increasing sequence number shared with the source audio stream.</summary>
    public long Sequence { get; }

    /// <summary>Pipeline latency between audio capture and transcript production.</summary>
    public TimeSpan Latency => EmittedAtUtc - CapturedAtUtc;

    /// <inheritdoc />
    public override string ToString() => $"{GetType().Name}[{Sequence}] {TranslatedText}";
}
