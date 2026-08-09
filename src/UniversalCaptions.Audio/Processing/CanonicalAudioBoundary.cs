using NAudio.Wave;

namespace UniversalCaptions.Audio.Processing;

/// <summary>
/// Canonical audio ingestion boundary (ADR-0010). Turns any supported WAV into the single
/// canonical representation that every STT/Gemini consumer must use:
/// mono, float32, 16 kHz sample rate, samples bounded to [-1, 1], no NaN.
/// Consumers MUST NOT perform their own resampling or down-mixing.
/// This is a pure, deterministic function of its input: identical bytes in produce identical
/// canonical bytes out. The resetting is done with the production windowed-sinc/Blackman
/// <see cref="SampleRateConverter"/>, never via a benchmark- or spike-local resampler.
/// </summary>
public sealed record CanonicalAudio(
    float[] MonoSamples,
    byte[] Pcm16Le,
    int SourceSampleRate,
    int SourceChannels)
{
    /// <summary>Duration of the canonical float stream in seconds (16 kHz frame rate).</summary>
    public double Seconds => MonoSamples.Length / (double)CanonicalAudioBoundary.CanonicalSampleRate;
}

/// <summary>
/// Static facade over the canonical audio boundary. All conversion is deterministic;
/// the same input always produces the same output.
/// </summary>
public static class CanonicalAudioBoundary
{
    /// <summary>The canonical output sample rate every consumer receives.</summary>
    public const int CanonicalSampleRate = 16_000;

    /// <summary>The canonical output channel count (mono).</summary>
    public const int CanonicalChannels = 1;

    /// <summary>Inclusive lower bound of supported input sample rates (Hz).</summary>
    public const int MinInputSampleRate = 8_000;

    /// <summary>Inclusive upper bound of supported input sample rates (Hz).</summary>
    public const int MaxInputSampleRate = 48_000;

    /// <summary>
    /// Deterministic number of zeros fed to the converter past the last source sample so the
    /// filter's trailing taps drain to steady state (the flush). Frames past the real tail are
    /// near-zero by construction, so the final segment keeps full context instead of being
    /// truncated. The tail count must stay constant across runs.
    /// </summary>
    private const int EosFlushInputSamples = 4096;

    /// <summary>How many canonical (16 kHz) output frames the deterministic EOS tail keeps.</summary>
    public const int EosTailOutputSamples = 64;

    /// <summary>
    /// Decodes a WAV file and normalizes it to the canonical representation.
    /// </summary>
    /// <exception cref="InvalidDataException">The file lacks a supported WAV structure/format.</exception>
    public static CanonicalAudio FromWav(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var reader = new WaveFileReader(path);
        return DecodeAndNormalize(reader);
    }

    /// <summary>
    /// Decodes in-memory WAV bytes and normalizes them to the canonical representation.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes lack a supported WAV structure/format.</exception>
    public static CanonicalAudio FromWavBytes(byte[] wavBytes)
    {
        ArgumentNullException.ThrowIfNull(wavBytes);
        using var inputStream = new MemoryStream(wavBytes);
        using var reader = new WaveFileReader(inputStream);
        return DecodeAndNormalize(reader);
    }

    /// <summary>
    /// Normalizes already-decoded interleaved float samples to the canonical representation.
    /// Deterministic: identical inputs produce identical outputs.
    /// </summary>
    /// <exception name="ArgumentException">Rate outside [<see cref="MinInputSampleRate"/>, <see cref="MaxInputSampleRate"/>] or channels not 1 or 2.</exception>
    /// <exception name="InvalidDataException">An input sample is NaN.</exception>
    public static CanonicalAudio Normalize(float[] interleaved, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        if (sampleRate is < MinInputSampleRate or > MaxInputSampleRate)
        {
            throw new ArgumentException(
                $"Sample rate {sampleRate} Hz is outside the supported range [{MinInputSampleRate}, {MaxInputSampleRate}].");
        }

        if (channels is < 1 or > 2)
        {
            throw new ArgumentException($"Channel count {channels} is not supported (mono or stereo only).");
        }

        if (interleaved.Length % channels != 0)
        {
            throw new ArgumentException("Interleaved sample count is not a multiple of the channel count.");
        }

        float[] mono = channels == 1 ? (float[])interleaved.Clone() : DownmixToMono(interleaved, channels);

        float[] canonical;
        if (sampleRate == CanonicalSampleRate)
        {
            canonical = (float[])mono.Clone();
        }
        else
        {
            canonical = ResampleWithDeterministicEos(mono, sampleRate);
        }

        for (int i = 0; i < canonical.Length; i++)
        {
            if (float.IsNaN(canonical[i]))
            {
                throw new InvalidDataException("Input audio contains NaN samples; canonical output must not contain NaN.");
            }

            canonical[i] = Math.Clamp(canonical[i], -1f, 1f);
        }

        return new CanonicalAudio(canonical, ToPcm16Le(canonical), sampleRate, channels);
    }

    /// <summary>
    /// Deterministic clamp → scale → little-endian conversion of mono float32 to PCM16 LE bytes.
    /// The projection used everywhere a PCM16 stream is required (e.g. Gemini wire bytes).
    /// </summary>
    public static byte[] ToPcm16Le(float[] monoSamples)
    {
        ArgumentNullException.ThrowIfNull(monoSamples);
        var bytes = new byte[monoSamples.Length * 2];
        for (int i = 0; i < monoSamples.Length; i++)
        {
            float value = Math.Clamp(monoSamples[i], -1f, 1f);
            short pcm = (short)Math.Clamp((int)(value * short.MaxValue), short.MinValue, short.MaxValue);
            bytes[2 * i] = (byte)(pcm & 0xFF);
            bytes[(2 * i) + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return bytes;
    }

    /// <summary>
    /// Reads the configured PCM (16-bit or 32-bit float) payload from the reader and normalizes it.
    /// </summary>
    private static CanonicalAudio DecodeAndNormalize(WaveFileReader reader)
    {
        WaveFormat format = reader.WaveFormat;
        if (format.SampleRate is < MinInputSampleRate or > MaxInputSampleRate)
        {
            throw new InvalidDataException($"Unsupported sample rate {format.SampleRate} Hz (supported: {MinInputSampleRate}-{MaxInputSampleRate}).");
        }

        if (format.Channels is < 1 or > 2)
        {
            throw new InvalidDataException($"Unsupported channel count {format.Channels} (mono or stereo only).");
        }

        if (format.BitsPerSample != 16 && format.BitsPerSample != 32)
        {
            throw new InvalidDataException($"Unsupported bits per sample {format.BitsPerSample} (16 or 32 only).");
        }

        if (format.Encoding != WaveFormatEncoding.Pcm && format.Encoding != WaveFormatEncoding.IeeeFloat)
        {
            throw new InvalidDataException($"Unsupported WAV encoding {format.Encoding} (PCM or IEEE float only).");
        }

        long byteCount = reader.Length;
        var raw = new byte[byteCount];
        int bytesRead = reader.Read(raw, 0, raw.Length);
        int frameCount = bytesRead / format.BlockAlign;

        var interleaved = new float[frameCount * format.Channels];
        if (format.BitsPerSample == 16)
        {
            for (int i = 0; i < frameCount * format.Channels; i++)
            {
                short s = (short)(raw[2 * i] | (raw[(2 * i) + 1] << 8));
                interleaved[i] = s / 32768f;
            }
        }
        else
        {
            for (int i = 0; i < frameCount * format.Channels; i++)
            {
                interleaved[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(
                    raw.AsSpan(4 * i, 4));
            }
        }

        return Normalize(interleaved, format.SampleRate, format.Channels);
    }

    /// <summary>Stereo → mono down-mix: (L + R) / 2, each frame clamped to [-1, 1] so overflow cannot exceed unity.</summary>
    private static float[] DownmixToMono(float[] interleaved, int channels)
    {
        int frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++)
            {
                sum += interleaved[(f * channels) + c];
            }

            mono[f] = Math.Clamp(sum / channels, -1f, 1f);
        }

        return mono;
    }

    /// <summary>
    /// Resamples mono to 16 kHz using the production <see cref="SampleRateConverter"/> and flaps
    /// the streaming kernel with a fixed count of zeros so the tail is never truncated, then keeps
    /// exactly <see cref="EosTailOutputSamples"/> frames of the resulting near-zero tail so the
    /// final segment carries full context. Deterministic: the pad count and kept tail count are
    /// constants, so identical inputs always produce identical canonical frames.
    /// </summary>
    private static float[] ResampleWithDeterministicEos(float[] mono, int sampleRate)
    {
        var resampler = new SampleRateConverter(sampleRate, CanonicalSampleRate, channels: 1);

        float[] body = resampler.Convert(mono);
        float[] flush = resampler.Convert(new float[EosFlushInputSamples]);

        // Everything between the last real source sample and the kept tail is the ring-out/residual
        // of the sinc filter; we drop the bulk so the canonical duration is faithful to the source,
        // and we only ever report the fixed tail constant.
        int keep = Math.Min(EosTailOutputSamples, flush.Length);
        var result = new float[body.Length + keep];
        Array.Copy(body, 0, result, 0, body.Length);
        Array.Copy(flush, flush.Length - keep, result, body.Length, keep);
        return result;
    }
}
