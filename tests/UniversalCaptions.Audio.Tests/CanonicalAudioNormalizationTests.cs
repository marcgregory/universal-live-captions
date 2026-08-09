using System.Text;
using NAudio.Wave;
using UniversalCaptions.Audio.Processing;

namespace UniversalCaptions.Audio.Tests;

/// <summary>
/// ADR-0010 canonical boundary tests. All twelve input combinations
/// (6 sample rates × {mono, stereo}) produce canonical mono float32/16 kHz audio
/// through the production <see cref="SampleRateConverter"/>, with deterministic PCM16-LE
/// projection and deterministic EOS padding.
/// </summary>
public sealed class CanonicalAudioNormalizationTests
{
    private static readonly int[] AllRates = [8000, 11025, 16000, 22050, 44100, 48000];

    [Theory]
    [InlineData(8000, 1)]
    [InlineData(8000, 2)]
    [InlineData(11025, 1)]
    [InlineData(11025, 2)]
    [InlineData(16000, 1)]
    [InlineData(16000, 2)]
    [InlineData(22050, 1)]
    [InlineData(22050, 2)]
    [InlineData(44100, 1)]
    [InlineData(44100, 2)]
    [InlineData(48000, 1)]
    [InlineData(48000, 2)]
    public void EveryRateAndChannelCombination_ProducesCanonicalFormat(int rate, int channels)
    {
        byte[] wav = BuildPcm16Wav(rate, channels, seconds: 0.35);

        var canonical = CanonicalAudioBoundary.FromWavBytes(wav);

        // Source metadata is preserved; the canonical representation is always mono.
        Assert.Equal(channels, canonical.SourceChannels);
        Assert.Equal(rate, canonical.SourceSampleRate);
        Assert.Equal(16000, CanonicalAudioBoundary.CanonicalSampleRate);
        Assert.All(canonical.MonoSamples, s =>
        {
            Assert.False(float.IsNaN(s));
            Assert.InRange(s, -1.0000001f, 1.0000001f);
        });

        // Expected duration ≈ input seconds at 16 kHz (plus the tiny deterministic EOS tail window).
        int expectedFrames = (int)Math.Round(0.35 * CanonicalAudioBoundary.CanonicalSampleRate);
        Assert.InRange(canonical.MonoSamples.Length, expectedFrames - 200, expectedFrames + CanonicalAudioBoundary.EosTailOutputSamples + 200);

        Assert.NotNull(canonical.Pcm16Le);
        Assert.Equal(canonical.MonoSamples.Length * 2, canonical.Pcm16Le.Length);
    }

    [Theory]
    [InlineData(16000)]
    [InlineData(48000)]
    public void Deterministic_SameBytesIn_SameCanonicalOut(int rate)
    {
        byte[] wav = BuildPcm16Wav(rate, 2, seconds: 0.3);

        var first = CanonicalAudioBoundary.FromWavBytes(wav);
        var second = CanonicalAudioBoundary.FromWavBytes(wav);

        Assert.Equal(first.MonoSamples, second.MonoSamples);
        Assert.Equal(first.Pcm16Le, second.Pcm16Le);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(44100)]
    public void EosTail_IsDeterministicAndBounded(int rate)
    {
        byte[] wav = BuildPcm16Wav(rate, 1, seconds: 0.3);

        var a = CanonicalAudioBoundary.FromWavBytes(wav);
        var b = CanonicalAudioBoundary.FromWavBytes(wav);

        // Identical trailing region across identical input (no wall-clock/time dependence).
        int tailStart = a.MonoSamples.Length - CanonicalAudioBoundary.EosTailOutputSamples;
        Assert.Equal(
            a.MonoSamples.AsSpan(tailStart).ToArray(),
            b.MonoSamples.AsSpan(tailStart).ToArray());

        // The tail is near-zero (EOS pad) — the filter settles, never amplifies.
        var tail = a.MonoSamples.AsSpan(tailStart);
        Assert.All(tail.ToArray(), s => Assert.InRange(Math.Abs(s), 0f, 0.02f));
    }

    [Fact]
    public void Downmix_StereoToMono_AveragesChannels()
    {
        var stereo = new float[] { 0.5f, 0.5f, 0.2f, 0.6f, -0.3f, -0.3f };

        var canonical = CanonicalAudioBoundary.Normalize(stereo, 8000, 2);

        // Stereo (L/R) → mono (L+R)/2 for the first down-mixed frame, with resample to 16 kHz after.
        // Check the pre-resample mono value under the hood via a single rate that matches rate.
        var atRate = CanonicalAudioBoundary.Normalize(stereo, 16000, 2);
        Assert.Equal(new[] { 0.5f, 0.4f, -0.3f }, atRate.MonoSamples);
    }

    [Fact]
    public void DownMix_ClampsOverflowToUnity()
    {
        // Both channels beyond unity — must clamp, never exceed 1.0.
        var hot = new float[] { 1.2f, 1.2f };

        var canonical = CanonicalAudioBoundary.Normalize(hot, 16000, 2);

        Assert.Equal(1f, canonical.MonoSamples[0], 5);
    }

    [Fact]
    public void PassThrough_16kMono_IsIdentity()
    {
        var input = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };

        var canonical = CanonicalAudioBoundary.Normalize(input, 16000, 1);

        Assert.Equal(input, canonical.MonoSamples);
        // PCM16 projection is the only extra artifact; mono stays same length.
        Assert.Equal(input.Length * 2, canonical.Pcm16Le.Length);
    }

    [Fact]
    public void Pcm16Le_MirrorsFrozenFloatToPcm16Le()
    {
        // Mirrors the frozen engine/wire helper used by the Gemini spike — clamp + scale to
        // short.MaxValue, little-endian bytes — so canonical bytes are byte-identical to what
        // the production wire expects. Note: clamp + (int) truncation means ±1.0 scales to
        // ±short.MaxValue (not ±short.MaxValue+1), and 0.5 truncates rather than rounding.
        var input = new float[] { 0.0f, 1.0f, -1.0f, 0.5f, -0.5f };

        byte[] bytes = CanonicalAudioBoundary.ToPcm16Le(input);

        // 0.0 → 0x0000; 1.0 → 0x7FFF; -1.0 → 0x8001; 0.5 → 0x3FFF; -0.5 → 0xC001 (all LE).
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFF, 0x7F, 0x01, 0x80, 0xFF, 0x3F, 0x01, 0xC0 }, bytes);
    }

    [Fact]
    public void Rejects_UnsupportedSampleRate()
    {
        Assert.Throws<ArgumentException>(() =>
            CanonicalAudioBoundary.Normalize(new float[] { 0f }, 6000, 1));
        Assert.Throws<ArgumentException>(() =>
            CanonicalAudioBoundary.Normalize(new float[] { 0f }, 96000, 1));
    }

    [Fact]
    public void Rejects_UnsupportedChannelCount()
    {
        Assert.Throws<ArgumentException>(() =>
            CanonicalAudioBoundary.Normalize(new float[] { 0f, 0f, 0f }, 16000, 3));
    }

    [Fact]
    public void Rejects_NonMultipleFrameCount()
    {
        Assert.Throws<ArgumentException>(() =>
            CanonicalAudioBoundary.Normalize(new float[] { 0f, 0f, 0f }, 16000, 2));
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(11025)]
    [InlineData(16000)]
    [InlineData(22050)]
    [InlineData(44100)]
    [InlineData(48000)]
    public void QualitySmoke_ToneReconstructsAtEveryRate(int rate)
    {
        int frequency = 1000;
        double seconds = 0.5;
        float[] tone = new float[(int)(rate * seconds)];
        for (int i = 0; i < tone.Length; i++)
        {
            tone[i] = (float)Math.Sin(2 * Math.PI * frequency * i / rate) * 0.5f;
        }

        var canonical = CanonicalAudioBoundary.Normalize(tone, rate, 1);

        // Signal should survive with substantial strength — not torn into noise.
        float peak = canonical.MonoSamples.Max(Math.Abs);
        double rms = Math.Sqrt(canonical.MonoSamples.Average(s => s * (double)s));
        Assert.True(peak > 0.2, $"peak too low at {rate} Hz: {peak}");
        Assert.True(rms > 0.05, $"rms too low at {rate} Hz: {rms}");

        // Dominant frequency should stay near the tone.
        double measured = EstimateFrequency(canonical.MonoSamples, CanonicalAudioBoundary.CanonicalSampleRate);
        Assert.InRange(measured, frequency * 0.9, frequency * 1.1);
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

        return crossings / 2.0 / ((double)(end - start) / sampleRate);
    }

    private static byte[] BuildPcm16Wav(int sampleRate, int channels, double seconds)
    {
        int frameCount = (int)(sampleRate * seconds);
        var sampleData = new float[frameCount * channels];
        var rng = new Random(sampleRate + channels); // deterministic seed, no wall-clock dependency
        for (int f = 0; f < frameCount; f++)
        {
            for (int c = 0; c < channels; c++)
            {
                sampleData[(f * channels) + c] = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.5f;
            }
        }

        return BuildWavBytes(sampleData, sampleRate, channels, bitsPerSample: 16);
    }

    private static byte[] BuildWavBytes(float[] interleaved, int sampleRate, int channels, int bitsPerSample)
    {
        int bytesPerSample = bitsPerSample / 8;
        int frameBytes = channels * bytesPerSample;
        int blockAlign = channels * bytesPerSample;
        int dataBytes = interleaved.Length * bytesPerSample;
        int byteRate = sampleRate * frameBytes;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);

        for (int i = 0; i < interleaved.Length; i++)
        {
            short s = (short)Math.Clamp((int)(Math.Clamp(interleaved[i], -1f, 1f) * short.MaxValue), short.MinValue, short.MaxValue);
            writer.Write(s);
        }

        writer.Flush();
        return ms.ToArray();
    }
}
