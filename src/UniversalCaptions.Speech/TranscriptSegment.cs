namespace UniversalCaptions.Speech;

/// <summary>
/// A decoded speech segment with times relative to the start of the audio window it came from.
/// </summary>
public readonly record struct TranscriptSegment(string Text, TimeSpan Start, TimeSpan End);
