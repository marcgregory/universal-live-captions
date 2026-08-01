namespace UniversalCaptions.App.Pipeline;

/// <summary>
/// The kind of end-to-end latency sample: whether the translated caption measured is the in-progress
/// active line (partial) or a committed final line.
/// </summary>
public enum EndToEndLatencyKind
{
    /// <summary>Audio capture → translated active (in-progress) line available.</summary>
    Partial,

    /// <summary>Audio capture → translated committed (final) line available.</summary>
    Final,
}

/// <summary>
/// An end-to-end latency measurement for a translated caption, recorded when the translated caption
/// is published to subscribers (the event the overlay renders on). Kept distinct from the pipeline's
/// STT-final <c>LatencyUpdated</c> so a configuration that is slow because of translation is not
/// mistaken for a slow speech engine.
/// </summary>
/// <param name="Kind">Whether the sample is for a partial (active line) or final (committed line) caption.</param>
/// <param name="EndToEndLatency">Originating audio capture time → translated caption published.</param>
/// <param name="TranslationLatency">Translation request start → translated caption published.</param>
public sealed record EndToEndLatencySample(
    EndToEndLatencyKind Kind,
    TimeSpan EndToEndLatency,
    TimeSpan TranslationLatency);
