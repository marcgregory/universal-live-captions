using UniversalCaptions.Core.Audio;

namespace UniversalCaptions.Core.Speech;

/// <summary>
/// Recognizes a continuous stream of audio and raises partial and final transcripts.
/// Implementations are engine-neutral: <see cref="AudioChunk"/> in, transcripts out.
/// </summary>
public interface ISpeechToTextEngine : IDisposable
{
    /// <summary>
    /// Raised as recognition progresses. The text of a partial is provisional and may be revised
    /// by later partials or a <see cref="FinalTranscriptAvailable"/> for the same utterance.
    /// </summary>
    event EventHandler<PartialTranscript>? PartialTranscriptAvailable;

    /// <summary>
    /// Raised when a stable result for a completed utterance is available.
    /// </summary>
    event EventHandler<FinalTranscript>? FinalTranscriptAvailable;

    /// <summary>Raised when recognition fails (for example, the model cannot be used).</summary>
    event EventHandler<SpeechRecognitionError>? RecognitionFailed;

    /// <summary>True while recognition is active.</summary>
    bool IsRecognizing { get; }

    /// <summary>
    /// Starts recognition. Synchronous initialization failures are raised via <see cref="RecognitionFailed"/>.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops recognition and discards in-progress state. Idempotent. A final transcript is not
    /// guaranteed for audio fed but not yet finalized.
    /// </summary>
    void Stop();

    /// <summary>
    /// Feeds a chunk of captured audio into the engine. Chunks fed before <see cref="Start"/> or
    /// after <see cref="Stop"/> are ignored. The chunk must not be mutated after the call returns.
    /// </summary>
    /// <param name="chunk">The captured audio to recognize.</param>
    void Process(AudioChunk chunk);
}
