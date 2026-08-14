namespace UniversalCaptions.Core.Captions;

/// <summary>
/// The lifecycle state of a <see cref="CaptionLine"/>.
/// </summary>
public enum CaptionLineState
{
    /// <summary>The line reflects an in-progress utterance and may be replaced by later partials.</summary>
    Active,

    /// <summary>The line is a committed, stable caption that will not be rewritten.</summary>
    Final,
}

/// <summary>
/// Identifies which pipeline produced a <see cref="CaptionLine"/>. Lines from different origins
/// coexist in the same caption state without overwriting one another; active-line identity is
/// <see cref="Origin"/> + instance identity.
/// </summary>
public enum LineOrigin
{
    /// <summary>The line was produced by an <c>ISpeechToTextEngine</c> recognising the source audio.</summary>
    SourceStt,

    /// <summary>The line was produced by an <c>ILiveAudioTranslationEngine</c> emitting target-language text.</summary>
    Translation,
}

/// <summary>
/// The translation state of a <see cref="CaptionLine"/>.
/// </summary>
public enum CaptionTranslationStatus
{
    /// <summary>Translation is disabled or was not requested for this line.</summary>
    NotRequested,

    /// <summary>A translation is in flight for this line.</summary>
    Pending,

    /// <summary>The line has a translated text in <see cref="CaptionLine.TranslatedText"/>.</summary>
    Completed,

    /// <summary>Translation failed; the source text remains available in <see cref="CaptionLine.Text"/>.</summary>
    Failed,
}

/// <summary>
/// A single caption line: the source-language text, the translated text when translation is
/// enabled, the languages involved, timestamps, and ordering/state. Immutable; the caption
/// service produces new instances as state changes.
/// </summary>
/// <remarks>
/// A line is either <see cref="CaptionLineState.Active"/> (in-progress, fed by partials) or
/// <see cref="CaptionLineState.Final"/> (committed, fed by a Whisper final and retained in
/// <see cref="CaptionState.History"/>). Translation never replaces the source text: a failure is
/// represented by <see cref="CaptionTranslationStatus.Failed"/> with the original text intact.
/// </remarks>
public sealed class CaptionLine
{
    /// <summary>
    /// Creates a new caption line.
    /// </summary>
    /// <param name="text">The source-language caption text.</param>
    /// <param name="sourceLanguage">The ISO 639-1 code of the source-language text.</param>
    /// <param name="sequence">A monotonically increasing number used to order lines.</param>
    /// <param name="capturedAtUtc">The time the source audio was captured (UTC).</param>
    /// <param name="state">Whether the line is active or committed.</param>
    /// <param name="committedAtUtc">The time the line was committed (UTC), when final.</param>
    /// <param name="targetLanguage">The ISO 639-1 code translation targets, when enabled.</param>
    /// <param name="translatedText">The translated text, when available.</param>
    /// <param name="translationStatus">The translation state of the line.</param>
    /// <param name="translationErrorMessage">A message describing a translation failure, when failed.</param>
    /// <param name="translationStartedAtUtc">The time the translation request started (UTC), when translation was attempted.</param>
    /// <param name="translationCompletedAtUtc">The time the translated line was applied/published (UTC), when translation completed.</param>
    /// <param name="origin">Which pipeline produced this line. Defaults to <see cref="LineOrigin.SourceStt"/>.</param>
    public CaptionLine(
        string text,
        string sourceLanguage,
        long sequence,
        DateTime capturedAtUtc,
        CaptionLineState state,
        DateTime? committedAtUtc = null,
        string? targetLanguage = null,
        string? translatedText = null,
        CaptionTranslationStatus translationStatus = CaptionTranslationStatus.NotRequested,
        string? translationErrorMessage = null,
        DateTime? translationStartedAtUtc = null,
        DateTime? translationCompletedAtUtc = null,
        LineOrigin origin = LineOrigin.SourceStt)
    {
        Text = text;
        SourceLanguage = sourceLanguage;
        Sequence = sequence;
        CapturedAtUtc = capturedAtUtc;
        State = state;
        CommittedAtUtc = committedAtUtc;
        TargetLanguage = targetLanguage;
        TranslatedText = translatedText;
        TranslationStatus = translationStatus;
        TranslationErrorMessage = translationErrorMessage;
        TranslationStartedAtUtc = translationStartedAtUtc;
        TranslationCompletedAtUtc = translationCompletedAtUtc;
        Origin = origin;
    }

    /// <summary>The source-language caption text.</summary>
    public string Text { get; }

    /// <summary>The ISO 639-1 code of the source-language text.</summary>
    public string SourceLanguage { get; }

    /// <summary>The ISO 639-1 code translation targets, when translation is enabled.</summary>
    public string? TargetLanguage { get; }

    /// <summary>Monotonically increasing sequence number used to order lines.</summary>
    public long Sequence { get; }

    /// <summary>Time the source audio was captured (UTC). Used for latency measurement.</summary>
    public DateTime CapturedAtUtc { get; }

    /// <summary>Time the line was committed (UTC); null while the line is active.</summary>
    public DateTime? CommittedAtUtc { get; }

    /// <summary>
    /// Time the translation request for this line started (UTC); null until a translation is attempted.
    /// Set on both completed and failed lines; used for translation-latency measurement.
    /// </summary>
    public DateTime? TranslationStartedAtUtc { get; }

    /// <summary>
    /// Time the translated line was applied and published to subscribers (UTC); set only when a
    /// translation completed successfully. Together with <see cref="CapturedAtUtc"/> it measures the
    /// end-to-end latency from audio capture to the translated caption being available to the UI.
    /// </summary>
    public DateTime? TranslationCompletedAtUtc { get; }

    /// <summary>Whether the line is active (in-progress) or final (committed).</summary>
    public CaptionLineState State { get; }

    /// <summary>The translated text, when a translation is available.</summary>
    public string? TranslatedText { get; }

    /// <summary>The translation state of the line.</summary>
    public CaptionTranslationStatus TranslationStatus { get; }

    /// <summary>A message describing a translation failure, when translation failed.</summary>
    public string? TranslationErrorMessage { get; }

    /// <summary>Which pipeline produced this line.</summary>
    public LineOrigin Origin { get; }

    /// <summary>
    /// Returns a copy of this line with a completed translation applied. The source text is
    /// preserved. When <paramref name="translationCompletedAtUtc"/> is set, the line is published to
    /// subscribers with it so end-to-end latency (audio → translated caption) can be measured.
    /// </summary>
    public CaptionLine WithTranslation(
        string translatedText,
        string targetLanguage,
        DateTime? translationStartedAtUtc = null,
        DateTime? translationCompletedAtUtc = null) => new(
        Text, SourceLanguage, Sequence, CapturedAtUtc, State, CommittedAtUtc,
        targetLanguage, translatedText, CaptionTranslationStatus.Completed, null,
        translationStartedAtUtc, translationCompletedAtUtc, Origin);

    /// <summary>
    /// Returns a copy of this line marked as pending translation. The source text is preserved.
    /// </summary>
    public CaptionLine WithPendingTranslation(string targetLanguage, DateTime? translationStartedAtUtc = null) => new(
        Text, SourceLanguage, Sequence, CapturedAtUtc, State, CommittedAtUtc,
        targetLanguage, null, CaptionTranslationStatus.Pending, null, translationStartedAtUtc, null, Origin);

    /// <summary>
    /// Returns a copy of this line marked as a translation failure. The source text is preserved.
    /// </summary>
    /// <param name="errorMessage">A message describing why translation failed.</param>
    public CaptionLine WithTranslationFailure(string errorMessage, DateTime? translationStartedAtUtc = null) => new(
        Text, SourceLanguage, Sequence, CapturedAtUtc, State, CommittedAtUtc,
        TargetLanguage, null, CaptionTranslationStatus.Failed, errorMessage, translationStartedAtUtc, null, Origin);

    /// <summary>
    /// Returns a copy of this line with ALL translation state stripped back to the pure source:
    /// <see cref="TranslatedText"/>, <see cref="TranslationStatus"/> (reset to
    /// <see cref="CaptionTranslationStatus.NotRequested"/>), <see cref="TranslationErrorMessage"/> and
    /// both translation timestamps are cleared. Used when a runtime reconfiguration (target change,
    /// translation toggle-off, provider change) ends a translation session and the overlay must return
    /// to pure source captions WITHOUT losing the English ground truth — this is what keeps the
    /// reported "Argos output mixes Japanese with English" impossible: the Japanese text is removed,
    /// the source line survives as English.
    /// </summary>
    public CaptionLine WithoutTranslation() => new(
        Text, SourceLanguage, Sequence, CapturedAtUtc, State, CommittedAtUtc,
        null, null, CaptionTranslationStatus.NotRequested, null, null, null, Origin);

    /// <inheritdoc />
    public override string ToString() => $"{State}[{Sequence}] {Text}";
}
