namespace UniversalCaptions.Core.Processing;

/// <summary>
/// A bounded FIFO buffer of normalized float PCM samples.
/// On overflow the oldest samples are dropped so the newest data is always retained.
/// </summary>
public interface IAudioBuffer
{
    /// <summary>Total sample capacity of the buffer.</summary>
    int CapacityInSamples { get; }

    /// <summary>Number of samples currently available to read.</summary>
    int ReadableCount { get; }

    /// <summary>
    /// Writes samples into the buffer. On overflow, the oldest samples are discarded.
    /// </summary>
    /// <param name="samples">The interleaved samples to write.</param>
    /// <returns>The number of samples actually written (always equal to the input length).</returns>
    int Write(ReadOnlySpan<float> samples);

    /// <summary>
    /// Reads up to <paramref name="destination"/>.Length samples in FIFO order.
    /// </summary>
    /// <param name="destination">The span to read into.</param>
    /// <returns>The number of samples read.</returns>
    int Read(Span<float> destination);

    /// <summary>Removes all samples from the buffer.</summary>
    void Clear();
}
