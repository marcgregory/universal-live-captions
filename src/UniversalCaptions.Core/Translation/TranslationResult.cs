namespace UniversalCaptions.Core.Translation;

/// <summary>
/// The result of translating a single text string.
/// </summary>
public sealed class TranslationResult
{
    /// <summary>
    /// Creates a new translation result.
    /// </summary>
    /// <param name="text">The translated text.</param>
    /// <param name="sourceLanguage">The language the text was translated from.</param>
    /// <param name="targetLanguage">The language the text was translated to.</param>
    /// <param name="sequence">A monotonically increasing number used to order translations.</param>
    /// <param name="startedUtc">The time the translation started (UTC).</param>
    /// <param name="completedUtc">The time the translation completed (UTC).</param>
    /// <param name="detectedSourceLanguage">
    /// The source language detected by the engine when auto-detection was requested, when available.
    /// </param>
    /// <param name="usedPivot">True when translation pivoted through an intermediate language.</param>
    /// <param name="pivotLanguage">The intermediate language used, when a pivot was required.</param>
    public TranslationResult(
        string text,
        string sourceLanguage,
        string targetLanguage,
        long sequence,
        DateTime startedUtc,
        DateTime completedUtc,
        string? detectedSourceLanguage = null,
        bool usedPivot = false,
        string? pivotLanguage = null)
    {
        Text = text;
        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
        Sequence = sequence;
        StartedUtc = startedUtc;
        CompletedUtc = completedUtc;
        DetectedSourceLanguage = detectedSourceLanguage;
        UsedPivot = usedPivot;
        PivotLanguage = pivotLanguage;
    }

    /// <summary>The translated text.</summary>
    public string Text { get; }

    /// <summary>The language the text was translated from.</summary>
    public string SourceLanguage { get; }

    /// <summary>The language the text was translated to.</summary>
    public string TargetLanguage { get; }

    /// <summary>Monotonically increasing sequence number used to order translations.</summary>
    public long Sequence { get; }

    /// <summary>Time the translation started (UTC). Used for latency measurement.</summary>
    public DateTime StartedUtc { get; }

    /// <summary>Time the translation completed (UTC). Used for latency measurement.</summary>
    public DateTime CompletedUtc { get; }

    /// <summary>
    /// The source language detected by the engine when auto-detection was requested, when available.
    /// </summary>
    public string? DetectedSourceLanguage { get; }

    /// <summary>True when translation pivoted through an intermediate language.</summary>
    public bool UsedPivot { get; }

    /// <summary>The intermediate language used, when a pivot was required.</summary>
    public string? PivotLanguage { get; }

    /// <summary>Pipeline latency between translation start and completion.</summary>
    public TimeSpan Latency => CompletedUtc - StartedUtc;

    /// <inheritdoc />
    public override string ToString() => $"{SourceLanguage}->{TargetLanguage}[{Sequence}] {Text}";
}
