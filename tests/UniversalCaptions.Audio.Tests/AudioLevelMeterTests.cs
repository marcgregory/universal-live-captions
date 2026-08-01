using UniversalCaptions.Audio.Metering;
using UniversalCaptions.Core.Audio;

namespace UniversalCaptions.Audio.Tests;

public sealed class AudioLevelMeterTests
{
    private static readonly AudioFormat Format = new(48000, 1, 32);

    [Fact]
    public void Process_ComputesRmsAndPeak()
    {
        var meter = new AudioLevelMeter();
        var chunk = Chunk([0.5f, -0.25f, 0.5f], 4);

        LevelReading reading = default;
        meter.LevelUpdated += (_, r) => reading = r;
        meter.Process(chunk);

        double expectedRms = Math.Sqrt((0.25 + 0.0625 + 0.25) / 3);
        Assert.Equal(expectedRms, reading.Rms, 5);
        Assert.Equal(0.5, reading.Peak, 5);
    }

    [Fact]
    public void Process_CarriesSequenceAndDuration()
    {
        var meter = new AudioLevelMeter();
        var chunk = Chunk(new float[480], 12);

        LevelReading reading = default;
        meter.LevelUpdated += (_, r) => reading = r;
        meter.Process(chunk);

        Assert.Equal(12, reading.Sequence);
        Assert.Equal(TimeSpan.FromMilliseconds(10), reading.WindowDuration);
    }

    [Fact]
    public void Process_EmptySamples_ReportsZero()
    {
        var meter = new AudioLevelMeter();
        var chunk = Chunk([], 1);

        LevelReading reading = default;
        meter.LevelUpdated += (_, r) => reading = r;
        meter.Process(chunk);

        Assert.Equal(0, reading.Rms);
        Assert.Equal(0, reading.Peak);
    }

    [Fact]
    public void Process_RaisesEventPerChunk()
    {
        var meter = new AudioLevelMeter();
        int raised = 0;
        meter.LevelUpdated += (_, _) => raised++;

        meter.Process(Chunk([0.1f], 1));
        meter.Process(Chunk([0.2f], 2));

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Process_NullChunk_Throws()
    {
        var meter = new AudioLevelMeter();
        Assert.Throws<ArgumentNullException>(() => meter.Process(null!));
    }

    private static AudioChunk Chunk(float[] samples, long sequence)
        => new(samples, Format, DateTime.UtcNow, sequence);
}
