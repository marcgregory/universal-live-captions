namespace UniversalCaptions.Core.Speech;

/// <summary>
/// A final, stable recognition result for a completed utterance.
/// </summary>
public sealed class FinalTranscript : SpeechTranscript
{
    /// <summary>
    /// Creates a new final transcript.
    /// </summary>
    /// <param name="text">The recognized text.</param>
    /// <param name="capturedAtUtc">The time the source audio was captured (UTC).</param>
    /// <param name="emittedAtUtc">The time this transcript was produced (UTC).</param>
    /// <param name="sequence">A monotonically increasing number used to order transcripts.</param>
    /// <param name="confidence">Optional engine-reported confidence in [0, 1].</param>
    public FinalTranscript(string text, DateTime capturedAtUtc, DateTime emittedAtUtc, long sequence, float? confidence = null)
        : base(text, capturedAtUtc, emittedAtUtc, sequence, confidence)
    {
    }
}
