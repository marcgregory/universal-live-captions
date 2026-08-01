using UniversalCaptions.Audio.Processing;

namespace UniversalCaptions.Audio.Tests;

public sealed class SampleRateConverterTests
{
    [Theory]
    [InlineData(48000, 16000, 1000)]
    [InlineData(44100, 16000, 1000)]
    [InlineData(48000, 24000, 440)]
    public void Downsample_PreservesFrequency(int inputRate, int outputRate, int frequency)
    {
        var converter = new SampleRateConverter(inputRate, outputRate, 1);
        float[] input = GenerateSine(inputRate, frequency, 0.5, 0.5);

        float[] output = converter.Convert(input);

        double measured = EstimateFrequency(output, outputRate);
        Assert.InRange(measured, frequency * 0.97, frequency * 1.03);
        AssertOutputLength(input, output, inputRate, outputRate);
    }

    [Fact]
    public void Upsample_PreservesFrequency()
    {
        var converter = new SampleRateConverter(16000, 48000, 1);
        float[] input = GenerateSine(16000, 1000, 0.5, 0.5);

        float[] output = converter.Convert(input);

        double measured = EstimateFrequency(output, 48000);
        Assert.InRange(measured, 970, 1030);
        AssertOutputLength(input, output, 16000, 48000);
    }

    [Fact]
    public void Downsample_Stereo_PreservesBothChannels()
    {
        var converter = new SampleRateConverter(48000, 16000, 2);
        int frames = 24000;
        float[] input = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            input[(f * 2) + 0] = (float)Math.Sin(2 * Math.PI * 1000 * f / 48000) * 0.4f;
            input[(f * 2) + 1] = (float)Math.Sin(2 * Math.PI * 2000 * f / 48000) * 0.4f;
        }

        float[] output = converter.Convert(input);

        int outFrames = output.Length / 2;
        float[] left = new float[outFrames];
        float[] right = new float[outFrames];
        for (int f = 0; f < outFrames; f++)
        {
            left[f] = output[(f * 2) + 0];
            right[f] = output[(f * 2) + 1];
        }

        Assert.InRange(EstimateFrequency(left, 16000), 970, 1030);
        Assert.InRange(EstimateFrequency(right, 16000), 1940, 2060);
        Assert.InRange(outFrames, (int)(frames / 3.0 * 0.9), (int)(frames / 3.0 * 1.1));
    }

    [Fact]
    public void StreamingChunks_ProduceContinuousOutput()
    {
        var converter = new SampleRateConverter(48000, 16000, 1);
        float[] input = GenerateSine(48000, 1000, 0.5, 0.5);

        var output = new List<float>();
        for (int i = 0; i < input.Length; i += 1200)
        {
            output.AddRange(converter.Convert(input.AsSpan(i, Math.Min(1200, input.Length - i)).ToArray()));
        }

        float[] result = [.. output];
        double measured = EstimateFrequency(result, 16000);
        Assert.InRange(measured, 970, 1030);
    }

    [Theory]
    [InlineData(0, 16000)]
    [InlineData(48000, 0)]
    [InlineData(48000, 48000)]
    [InlineData(-8000, 16000)]
    public void Constructor_RejectsInvalidRates(int inputRate, int outputRate)
    {
        Assert.Throws<ArgumentException>(() => new SampleRateConverter(inputRate, outputRate, 1));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveChannels()
    {
        Assert.Throws<ArgumentException>(() => new SampleRateConverter(48000, 16000, 0));
    }

    [Fact]
    public void Convert_EmptyInput_ReturnsEmpty()
    {
        var converter = new SampleRateConverter(48000, 16000, 1);
        Assert.Empty(converter.Convert([]));
    }

    private static void AssertOutputLength(float[] input, float[] output, int inputRate, int outputRate)
    {
        double ratio = (double)outputRate / inputRate;
        double expected = input.Length * ratio;
        Assert.InRange(output.Length, expected * 0.8, expected * 1.2);
    }

    private static float[] GenerateSine(int sampleRate, double frequency, double seconds, double amplitude)
    {
        int count = (int)(sampleRate * seconds);
        var result = new float[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = (float)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * amplitude);
        }

        return result;
    }

    private static double EstimateFrequency(float[] samples, int sampleRate)
    {
        int start = Math.Min(samples.Length / 10, 1000);
        int end = samples.Length - start;
        if (end <= start + 2)
        {
            return 0;
        }

        int crossings = 0;
        for (int i = start + 1; i < end; i++)
        {
            if ((samples[i - 1] < 0 && samples[i] >= 0) || (samples[i - 1] >= 0 && samples[i] < 0))
            {
                crossings++;
            }
        }

        double duration = (double)(end - start) / sampleRate;
        return crossings / 2.0 / duration;
    }
}
