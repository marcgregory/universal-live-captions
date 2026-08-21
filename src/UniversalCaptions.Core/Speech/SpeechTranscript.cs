namespace UniversalCaptions.Core.Speech;

/// <summary>
/// A recognition result produced by the speech pipeline (a live transcription of the source audio).
/// </summary>
public abstract class SpeechTranscript
{
    /// <summary>
    /// Creates a new transcript.
    /// </summary>
    /// <param name="text">The recognized text.</param>
    /// <param name="capturedAtUtc">The time the source audio was captured (UTC).</param>
    /// <param name="emittedAtUtc">The time this transcript was produced (UTC).</param>
    /// <param name="sequence">A monotonically increasing number used to order transcripts.</param>
    /// <param name="confidence">Optional engine-reported confidence in [0, 1].</param>
    protected SpeechTranscript(string text, DateTime capturedAtUtc, DateTime emittedAtUtc, long sequence, float? confidence = null)
    {
        Text = text;
        CapturedAtUtc = capturedAtUtc;
        EmittedAtUtc = emittedAtUtc;
        Sequence = sequence;
        Confidence = confidence;
    }

    /// <summary>The recognized text.</summary>
    public string Text { get; }

    /// <summary>Time the source audio was captured (UTC). Used for latency measurement.</summary>
    public DateTime CapturedAtUtc { get; }

    /// <summary>Time the transcript was produced (UTC). Used for latency measurement.</summary>
    public DateTime EmittedAtUtc { get; }

    /// <summary>Monotonically increasing sequence number shared with the source audio stream.</summary>
    public long Sequence { get; }

    /// <summary>Engine-reported confidence in [0, 1], when available.</summary>
    public float? Confidence { get; }

    /// <summary>Pipeline latency between audio capture and transcript production.</summary>
    public TimeSpan Latency => EmittedAtUtc - CapturedAtUtc;

    /// <inheritdoc />
    public override string ToString() => $"{GetType().Name}[{Sequence}] {Text}";
}
