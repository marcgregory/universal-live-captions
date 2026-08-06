namespace UniversalCaptions.Speech;

/// <summary>
/// Configuration for <see cref="SpeechSegmentDetector"/>, the C#-side voice-segment state machine
/// that decides where one decodable speech segment ends and the next begins.
/// </summary>
public sealed class SpeechSegmentDetectorOptions
{
    /// <summary>
    /// Minimum amount of active speech a buffered segment must contain before it is worth decoding.
    /// Shorter bursts (coughs, one-off noise blips) are discarded rather than decoded into noise.
    /// </summary>
    public TimeSpan MinSpeechDuration { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Trailing silence appended to a segment after speech stops, before the segment is closed.
    /// Bridges brief intra-sentence pauses so one coherent FINAL covers a whole sentence.
    /// </summary>
    public TimeSpan SilenceHangover { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// Hard cap on buffered segment length. A segment is closed at this bound even if the speaker is
    /// still talking, bounding how stale captions can become during continuous speech.
    /// </summary>
    public TimeSpan MaxSegmentDuration { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Sample rate the detector assumes for the incoming mono PCM stream.</summary>
    public int SampleRate { get; init; } = 16_000;
}
