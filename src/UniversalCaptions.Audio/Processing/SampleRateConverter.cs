using UniversalCaptions.Core.Audio;

namespace UniversalCaptions.Audio.Processing;

/// <summary>
/// Streaming sample-rate converter using a windowed-sinc (Blackman) interpolation kernel.
/// Processes interleaved multi-channel audio and preserves phase continuity across calls.
/// </summary>
public sealed class SampleRateConverter
{
    private readonly double _step;          // input frames per output frame
    private readonly double _cutoff;        // normalized cutoff frequency (cycles per input sample)
    private readonly int _channels;
    private readonly int _taps;
    private readonly int _halfTaps;
    private readonly float[] _history;      // sliding window ring of input frames, interleaved
    private readonly int _ringCapacity;     // in frames
    private long _nextInputFrame;           // absolute index of the next frame to be appended
    private long _historyStartFrame;        // absolute index of the oldest frame still in the ring
    private int _historyCount;              // frames currently in the ring
    private double _outputCursor;           // absolute input position (frames) of the next output frame

    /// <summary>
    /// Creates a streaming resampler.
    /// </summary>
    /// <param name="inputRate">Input sample rate (per channel).</param>
    /// <param name="outputRate">Output sample rate (per channel).</param>
    /// <param name="channels">Number of interleaved channels.</param>
    /// <exception cref="ArgumentException">The rates are not positive or are equal.</exception>
    public SampleRateConverter(int inputRate, int outputRate, int channels)
    {
        if (inputRate <= 0)
        {
            throw new ArgumentException("Input rate must be positive.", nameof(inputRate));
        }

        if (outputRate <= 0)
        {
            throw new ArgumentException("Output rate must be positive.", nameof(outputRate));
        }

        if (inputRate == outputRate)
        {
            throw new ArgumentException("Input and output rates are equal; no conversion is required.", nameof(outputRate));
        }

        if (channels <= 0)
        {
            throw new ArgumentException("Channel count must be positive.", nameof(channels));
        }

        InputRate = inputRate;
        OutputRate = outputRate;
        _channels = channels;
        _step = (double)inputRate / outputRate;

        int oversample = Math.Max(1, (int)Math.Ceiling((double)inputRate / outputRate));
        _taps = Math.Max(16, 16 * oversample);
        _taps += _taps % 2; // ensure even
        _halfTaps = _taps / 2;

        _cutoff = 0.5 * Math.Min(1.0, (double)outputRate / inputRate) * 0.92;

        _ringCapacity = _taps + 2;
        _history = new float[_ringCapacity * channels];
    }

    /// <summary>Input sample rate (per channel).</summary>
    public int InputRate { get; }

    /// <summary>Output sample rate (per channel).</summary>
    public int OutputRate { get; }

    /// <summary>Number of interleaved channels.</summary>
    public int Channels => _channels;

    /// <summary>
    /// Appends interleaved input samples and returns as many output samples as can be produced.
    /// Output may be empty for a leading short input while the internal filter fills.
    /// </summary>
    /// <param name="inputInterleaved">Interleaved input samples.</param>
    /// <returns>Interleaved output samples.</returns>
    public float[] Convert(ReadOnlySpan<float> inputInterleaved)
    {
        if (inputInterleaved.Length == 0)
        {
            return [];
        }

        int inputFrames = inputInterleaved.Length / _channels;
        var output = new List<float>(Math.Max(0, (int)(inputFrames / _step) + 1));
        var frame = new float[_channels];
        int inputOffset = 0;

        while (true)
        {
            long cursorFloor = (long)Math.Floor(_outputCursor);
            double phase = _outputCursor - cursorFloor;
            long firstNeeded = cursorFloor - _halfTaps + 1;
            long lastNeeded = cursorFloor + _halfTaps;

            // Append just enough input so the kernel window is fully available.
            while (lastNeeded >= _nextInputFrame && inputOffset < inputFrames)
            {
                AppendFrame(inputInterleaved, inputOffset);
                inputOffset++;
            }

            // Evict frames the current cursor position no longer needs.
            long keepFrom = Math.Max(0, cursorFloor - _halfTaps - 1);
            DropFramesBefore(keepFrom);

            if (lastNeeded >= _nextInputFrame)
            {
                break; // not enough future input yet
            }

            if (firstNeeded >= 0 && firstNeeded < _historyStartFrame)
            {
                break; // required history has been evicted; wait for more input in a later call
            }

            for (int channel = 0; channel < _channels; channel++)
            {
                double acc = 0;
                for (int i = 0; i < _taps; i++)
                {
                    long frameIndex = firstNeeded + i;
                    float sample;
                    if (frameIndex < 0)
                    {
                        // Frames before the start of the stream are implicitly zero.
                        sample = 0f;
                    }
                    else
                    {
                        int ringIndex = (int)(frameIndex % _ringCapacity);
                        sample = _history[(ringIndex * _channels) + channel];
                    }

                    double n = (i - _halfTaps + 1) - phase;
                    acc += sample * Kernel(n);
                }

                frame[channel] = (float)acc;
            }

            for (int c = 0; c < _channels; c++)
            {
                output.Add(frame[c]);
            }

            _outputCursor += _step;
        }

        return [.. output];
    }

    /// <summary>Computes the windowed-sinc kernel value at offset <paramref name="n"/> (in input samples).</summary>
    private double Kernel(double n)
    {
        double x = 2.0 * _cutoff * n;
        double sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
        double windowX = n / _halfTaps;
        if (Math.Abs(windowX) >= 1.0)
        {
            return 0.0;
        }

        double window = 0.42 + (0.5 * Math.Cos(Math.PI * windowX)) + (0.08 * Math.Cos(2.0 * Math.PI * windowX));
        return 2.0 * _cutoff * sinc * window;
    }

    private void AppendFrame(ReadOnlySpan<float> inputInterleaved, int frameOffset)
    {
        long absoluteFrame = _nextInputFrame;
        int ringIndex = (int)(absoluteFrame % _ringCapacity) * _channels;
        inputInterleaved.Slice(frameOffset * _channels, _channels).CopyTo(_history.AsSpan(ringIndex, _channels));

        _nextInputFrame++;
        _historyCount++;
        if (_historyCount > _ringCapacity)
        {
            // The oldest retained frame has been overwritten by the wrap-around.
            _historyCount = _ringCapacity;
            _historyStartFrame++;
        }
    }

    private void DropFramesBefore(long keepFrom)
    {
        if (_historyStartFrame >= keepFrom)
        {
            return;
        }

        long drop = keepFrom - _historyStartFrame;
        _historyStartFrame = keepFrom;
        _historyCount -= (int)Math.Min(_historyCount, drop);
        if (_historyCount < 0)
        {
            _historyCount = 0;
        }
    }
}
