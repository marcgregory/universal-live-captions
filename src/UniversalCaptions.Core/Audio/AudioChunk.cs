namespace UniversalCaptions.Core.Audio;

/// <summary>
/// A unit of captured audio flowing through the pipeline.
/// Samples are normalized floating point PCM in the range [-1, 1], interleaved by channel.
/// </summary>
public sealed class AudioChunk
{
    /// <summary>
    /// Creates a new audio chunk.
    /// </summary>
    /// <param name="samples">Interleaved float PCM samples in the range [-1, 1]. The caller must not mutate the array after passing it.</param>
    /// <param name="format">The format the samples are encoded in.</param>
    /// <param name="capturedAtUtc">The time the underlying audio was captured.</param>
    /// <param name="sequence">A monotonically increasing sequence number for ordering diagnostics.</param>
    public AudioChunk(float[] samples, AudioFormat format, DateTime capturedAtUtc, long sequence)
    {
        Samples = samples;
        Format = format;
        CapturedAtUtc = capturedAtUtc;
        Sequence = sequence;
    }

    /// <summary>Interleaved float PCM samples in the range [-1, 1].</summary>
    public float[] Samples { get; }

    /// <summary>Format of the captured audio.</summary>
    public AudioFormat Format { get; }

    /// <summary>Time the audio was captured (UTC).</summary>
    public DateTime CapturedAtUtc { get; }

    /// <summary>Monotonically increasing sequence number.</summary>
    public long Sequence { get; }

    /// <summary>Number of multi-channel frames in this chunk.</summary>
    public int FrameCount => Format.Channels == 0 ? 0 : Samples.Length / Format.Channels;

    /// <summary>Duration of this chunk.</summary>
    public TimeSpan Duration => Format.SampleRate == 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds((double)FrameCount / Format.SampleRate);
}
