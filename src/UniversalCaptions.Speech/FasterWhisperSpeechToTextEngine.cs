using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Speech;

namespace UniversalCaptions.Speech;

/// <summary>
/// An <see cref="ISpeechToTextEngine"/> that reuses the shared streaming orchestration (windowing,
/// trimming, commit) of <see cref="WhisperSpeechToTextEngine"/> while decoding with a persistent
/// faster-whisper Python worker instead of whisper.cpp. It is the faster-whisper path for the
/// selectable <c>UC_STT_ENGINE=fasterwhisper</c> app option; the whisper.cpp/default path is
/// unchanged.
/// </summary>
public sealed class FasterWhisperSpeechToTextEngine : ISpeechToTextEngine, IAsyncDisposable
{
    private readonly WhisperSpeechToTextEngine _inner;

    /// <summary>
    /// Creates a faster-whisper engine backed by a persistent Python worker.
    /// </summary>
    public FasterWhisperSpeechToTextEngine(FasterWhisperEngineOptions options)
        : this(options, new FasterWhisperDecoder(options))
    {
    }

    internal FasterWhisperSpeechToTextEngine(FasterWhisperEngineOptions options, ISTTDecoder decoder)
    {
        var innerOptions = new WhisperEngineOptions
        {
            // The whisper.cpp model file is not used by the faster-whisper path, but the shared
            // engine requires a non-empty value so it can be constructed.
            ModelPath = "faster-whisper",
            Language = options.Language,
            SampleRate = options.SampleRate,
            WindowDuration = options.WindowDuration,
            DecodeInterval = options.DecodeInterval,
            CommitOverlap = options.CommitOverlap,
            MinimumAudioBeforeFirstDecode = options.MinimumAudioBeforeFirstDecode,
            StabilityWindow = options.StabilityWindow,
            BoundaryWaitBudget = options.BoundaryWaitBudget,
        };
        _inner = new WhisperSpeechToTextEngine(innerOptions, decoder);
    }

    /// <inheritdoc />
    public event EventHandler<PartialTranscript>? PartialTranscriptAvailable
    {
        add => _inner.PartialTranscriptAvailable += value;
        remove => _inner.PartialTranscriptAvailable -= value;
    }

    /// <inheritdoc />
    public event EventHandler<FinalTranscript>? FinalTranscriptAvailable
    {
        add => _inner.FinalTranscriptAvailable += value;
        remove => _inner.FinalTranscriptAvailable -= value;
    }

    /// <inheritdoc />
    public event EventHandler<SpeechRecognitionError>? RecognitionFailed
    {
        add => _inner.RecognitionFailed += value;
        remove => _inner.RecognitionFailed -= value;
    }

    /// <inheritdoc />
    public bool IsRecognizing => _inner.IsRecognizing;

    /// <inheritdoc />
    public void Start() => _inner.Start();

    /// <inheritdoc />
    public void Stop() => _inner.Stop();

    /// <inheritdoc />
    public void Process(AudioChunk chunk) => _inner.Process(chunk);

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();

    /// <summary>Stops recognition and releases the worker process.</summary>
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
