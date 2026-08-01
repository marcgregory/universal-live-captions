using UniversalCaptions.Audio.Processing;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Processing;

namespace UniversalCaptions.Audio.Tests;

public sealed class AudioProcessorTests
{
    private static readonly AudioFormat Target = new(16000, 1, 32);

    [Fact]
    public void TryProcess_MatchingFormat_PassesThroughUnchanged()
    {
        var processor = new AudioProcessor(Target);
        var input = Chunk([0.1f, -0.2f, 0.3f], Target, 7);

        bool ok = processor.TryProcess(input, out AudioChunk? output);

        Assert.True(ok);
        Assert.NotNull(output);
        Assert.Equal(Target, output!.Format);
        Assert.Equal([0.1f, -0.2f, 0.3f], output.Samples);
        Assert.Equal(7, output.Sequence);
        Assert.Equal(input.CapturedAtUtc, output.CapturedAtUtc);
    }

    [Fact]
    public void TryProcess_StereoToMono_AveragesChannels()
    {
        var processor = new AudioProcessor(Target);
        var stereoFormat = new AudioFormat(16000, 2, 32);
        var input = Chunk([0.5f, 0.5f, 0.2f, 0.6f], stereoFormat, 1);

        processor.TryProcess(input, out AudioChunk? output);

        Assert.NotNull(output);
        Assert.Equal(1, output!.Format.Channels);
        Assert.Equal(2, output.Samples.Length);
        Assert.Equal(0.5f, output.Samples[0], 5);
        Assert.Equal(0.4f, output.Samples[1], 5);
    }

    [Fact]
    public void TryProcess_MonoToStereo_DuplicatesChannel()
    {
        var processor = new AudioProcessor(new AudioFormat(16000, 2, 32));
        var input = Chunk([0.5f, -0.25f], Target, 1);

        processor.TryProcess(input, out AudioChunk? output);

        Assert.NotNull(output);
        Assert.Equal(2, output!.Format.Channels);
        Assert.Equal([0.5f, 0.5f, -0.25f, -0.25f], output.Samples);
    }

    [Fact]
    public void TryProcess_ResamplesToOutputRate()
    {
        var processor = new AudioProcessor(Target);
        var inputFormat = new AudioFormat(48000, 1, 32);
        float[] sine = new float[4800];
        for (int i = 0; i < sine.Length; i++)
        {
            sine[i] = (float)Math.Sin(2 * Math.PI * 1000 * i / 48000) * 0.5f;
        }

        var input = Chunk(sine, inputFormat, 2);
        processor.TryProcess(input, out AudioChunk? output);

        Assert.NotNull(output);
        Assert.Equal(16000, output!.Format.SampleRate);
        Assert.InRange(output.Samples.Length, 800, 2400);
    }

    [Fact]
    public void TryProcess_ResamplesAndDownmixes()
    {
        var processor = new AudioProcessor(Target);
        var inputFormat = new AudioFormat(48000, 2, 32);
        var input = Chunk(new float[4800 * 2], inputFormat, 3);

        processor.TryProcess(input, out AudioChunk? output);

        Assert.NotNull(output);
        Assert.Equal(Target, output!.Format);
        Assert.InRange(output.Samples.Length, 800, 2400);
    }

    [Fact]
    public void TryProcess_NullInput_Throws()
    {
        var processor = new AudioProcessor(Target);
        Assert.Throws<ArgumentNullException>(() => processor.TryProcess(null!, out _));
    }

    [Fact]
    public void OutputFormat_ReturnsTarget()
    {
        var processor = new AudioProcessor(Target);
        Assert.Equal(Target, processor.OutputFormat);
    }

    private static AudioChunk Chunk(float[] samples, AudioFormat format, long sequence)
        => new(samples, format, DateTime.UtcNow, sequence);
}
