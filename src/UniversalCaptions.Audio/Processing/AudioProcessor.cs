using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Processing;

namespace UniversalCaptions.Audio.Processing;

/// <summary>
/// Converts chunks to a target format by down-mixing/up-mixing channels and resampling when needed.
/// When the input already matches the target format the chunk is passed through unchanged.
/// </summary>
public sealed class AudioProcessor : IAudioProcessor
{
    private readonly AudioFormat _outputFormat;
    private SampleRateConverter? _resampler;
    private int _resamplerInputRate;
    private int _resamplerChannels;

    /// <summary>
    /// Creates a processor producing the given output format.
    /// </summary>
    /// <param name="outputFormat">The format every processed chunk should have.</param>
    public AudioProcessor(AudioFormat outputFormat)
    {
        _outputFormat = outputFormat;
    }

    /// <inheritdoc />
    public AudioFormat OutputFormat => _outputFormat;

    /// <inheritdoc />
    public bool TryProcess(AudioChunk input, out AudioChunk? output)
    {
        ArgumentNullException.ThrowIfNull(input);

        float[] samples = input.Samples;
        int channels = input.Format.Channels;
        int sampleRate = input.Format.SampleRate;

        if (channels != _outputFormat.Channels)
        {
            samples = MixChannels(samples, channels, _outputFormat.Channels);
            channels = _outputFormat.Channels;
        }

        if (sampleRate != _outputFormat.SampleRate)
        {
            if (_resampler is null || _resamplerInputRate != sampleRate || _resamplerChannels != channels)
            {
                _resampler = new SampleRateConverter(sampleRate, _outputFormat.SampleRate, channels);
                _resamplerInputRate = sampleRate;
                _resamplerChannels = channels;
            }

            samples = _resampler.Convert(samples);
        }

        output = new AudioChunk(samples, _outputFormat, input.CapturedAtUtc, input.Sequence);
        return true;
    }

    private static float[] MixChannels(float[] source, int sourceChannels, int targetChannels)
    {
        int frames = source.Length / sourceChannels;
        var result = new float[frames * targetChannels];

        if (targetChannels == 1)
        {
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < sourceChannels; c++)
                {
                    sum += source[(f * sourceChannels) + c];
                }

                result[f] = sum / sourceChannels;
            }

            return result;
        }

        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < targetChannels; c++)
            {
                result[(f * targetChannels) + c] = source[(f * sourceChannels) + Math.Min(c, sourceChannels - 1)];
            }
        }

        return result;
    }
}
