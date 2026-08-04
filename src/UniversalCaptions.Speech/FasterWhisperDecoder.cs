namespace UniversalCaptions.Speech;

/// <summary>
/// <see cref="ISTTDecoder"/> implementation that runs a persistent faster-whisper Python worker.
/// Converts a window of mono 16 kHz float samples to int16 PCM and transcribes via the worker;
/// the streaming engine owns all windowing, trimming, and commit orchestration.
/// </summary>
internal sealed class FasterWhisperDecoder : ISTTDecoder
{
    private readonly FasterWhisperEngineOptions _options;
    private readonly IFasterWhisperProcess _process;
    private readonly bool _ownedProcess;

    public FasterWhisperDecoder(FasterWhisperEngineOptions options)
        : this(options, new LineProtocolFasterWhisperProcess(options), ownedProcess: true)
    {
    }

    internal FasterWhisperDecoder(FasterWhisperEngineOptions options, IFasterWhisperProcess process)
        : this(options, process, ownedProcess: false)
    {
    }

    private FasterWhisperDecoder(FasterWhisperEngineOptions options, IFasterWhisperProcess process, bool ownedProcess)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _ownedProcess = ownedProcess;
    }

    /// <inheritdoc />
    public void EnsureReady()
    {
        try
        {
            _process.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (FasterWhisperProcessException ex)
        {
            throw new FileNotFoundException(
                $"Faster-whisper model '{_options.Model}' could not be loaded: {ex.Message}",
                ex);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TranscriptSegment> Decode(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
    {
        var pcm = FloatToInt16(samples);
        try
        {
            return _process.TranscribeAsync(pcm, _options.Language, cancellationToken).GetAwaiter().GetResult();
        }
        catch (FasterWhisperProcessException ex)
        {
            throw new InvalidOperationException(
                $"Faster-whisper decoding failed: {ex.Message}",
                ex);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_ownedProcess)
        {
            return _process.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Converts float samples in [-1, 1] to int16 PCM, matching the WAV int16 convention.</summary>
    private static short[] FloatToInt16(ReadOnlyMemory<float> samples)
    {
        var result = new short[samples.Length];
        var span = samples.Span;
        for (int i = 0; i < span.Length; i++)
        {
            float v = span[i];
            if (v > 1.0f)
            {
                v = 1.0f;
            }
            else if (v < -1.0f)
            {
                v = -1.0f;
            }

            // -1.0 maps to short.MinValue (-32768) and +1.0 to short.MaxValue (32767),
            // matching the standard WAV int16 convention (whisper.cpp's wav conversion).
            result[i] = v < 0.0f
                ? (short)(v * -short.MinValue)
                : (short)(v * short.MaxValue);
        }

        return result;
    }
}
