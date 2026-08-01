using UniversalCaptions.Core.Audio;

namespace UniversalCaptions.Audio.Metering;

/// <summary>
/// A measurement of audio level for one chunk.
/// </summary>
/// <param name="Rms">Root-mean-square level in the range [0, 1].</param>
/// <param name="Peak">Peak absolute sample in the range [0, 1].</param>
/// <param name="TimestampUtc">When the measurement was taken.</param>
/// <param name="WindowDuration">Duration of audio the measurement covers.</param>
/// <param name="Sequence">Sequence of the source chunk.</param>
public readonly record struct LevelReading(double Rms, double Peak, DateTime TimestampUtc, TimeSpan WindowDuration, long Sequence);

/// <summary>
/// Computes RMS and peak levels for audio chunks and raises <see cref="LevelUpdated"/> per chunk.
/// </summary>
public sealed class AudioLevelMeter
{
    /// <summary>Raised for every processed chunk.</summary>
    public event EventHandler<LevelReading>? LevelUpdated;

    /// <summary>
    /// Computes levels for a chunk and raises <see cref="LevelUpdated"/>.
    /// </summary>
    /// <param name="chunk">The chunk to measure.</param>
    public void Process(AudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        double sumSquares = 0;
        double peak = 0;
        foreach (float sample in chunk.Samples)
        {
            double value = sample;
            sumSquares += value * value;
            double absolute = Math.Abs(value);
            if (absolute > peak)
            {
                peak = absolute;
            }
        }

        double rms = chunk.Samples.Length == 0 ? 0 : Math.Sqrt(sumSquares / chunk.Samples.Length);
        LevelUpdated?.Invoke(this, new LevelReading(rms, peak, DateTime.UtcNow, chunk.Duration, chunk.Sequence));
    }
}
