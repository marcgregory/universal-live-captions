using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Processing;

namespace UniversalCaptions.Audio.Processing;

/// <summary>
/// Options for the energy-based voice activity detector.
/// </summary>
/// <param name="RmsThreshold">Root-mean-square energy above which a chunk counts as active. Range (0, 1].</param>
/// <param name="MinActiveChunks">Consecutive active chunks required before speech is declared.</param>
/// <param name="SilenceHangoverChunks">Consecutive inactive chunks tolerated before speech ends.</param>
public sealed record VadOptions(double RmsThreshold = 0.01, int MinActiveChunks = 2, int SilenceHangoverChunks = 6);

/// <summary>
/// A simple energy-based voice activity detector with attack/release hysteresis.
/// </summary>
public sealed class EnergyVad : IVoiceActivityDetector
{
    private readonly VadOptions _options;
    private int _activeRun;
    private int _inactiveRun;
    private bool _isSpeech;

    /// <summary>
    /// Creates an energy VAD.
    /// </summary>
    /// <param name="options">Detection options.</param>
    public EnergyVad(VadOptions? options = null)
    {
        _options = options ?? new VadOptions();
    }

    /// <inheritdoc />
    public bool IsSpeech(AudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Samples.Length == 0)
        {
            return _isSpeech;
        }

        double rms = ComputeRms(chunk.Samples);

        if (rms >= _options.RmsThreshold)
        {
            _inactiveRun = 0;
            _activeRun++;
            if (_activeRun >= _options.MinActiveChunks)
            {
                _isSpeech = true;
            }
        }
        else
        {
            _activeRun = 0;
            if (_isSpeech)
            {
                _inactiveRun++;
                if (_inactiveRun >= _options.SilenceHangoverChunks)
                {
                    _isSpeech = false;
                    _inactiveRun = 0;
                }
            }
        }

        return _isSpeech;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _activeRun = 0;
        _inactiveRun = 0;
        _isSpeech = false;
    }

    private static double ComputeRms(float[] samples)
    {
        double sum = 0;
        foreach (float sample in samples)
        {
            sum += (double)sample * sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }
}
