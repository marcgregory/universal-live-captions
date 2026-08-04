using UniversalCaptions.Core.Speech;

namespace UniversalCaptions.Speech;

/// <summary>
/// Test seam and engine boundary: decodes one audio window into segments. The streaming engine
/// (windowing, trimming, commit orchestration) lives in the <see cref="ISpeechToTextEngine"/>
/// implementation; a decoder is responsible only for the model-specific work of turning a window
/// of mono 16 kHz samples into <see cref="TranscriptSegment"/>s.
/// </summary>
internal interface ISTTDecoder : IAsyncDisposable
{
    /// <summary>
    /// Loads the model if it has not been loaded yet. Throws <see cref="FileNotFoundException"/> when
    /// the model file is missing and any other exception when the model cannot be loaded; the engine
    /// maps these to <see cref="SpeechRecognitionErrorKind.ModelNotFound"/> and
    /// <see cref="SpeechRecognitionErrorKind.ModelLoadFailed"/>.
    /// </summary>
    void EnsureReady();

    /// <summary>
    /// Decodes a window of mono 16 kHz samples into segments with timestamps relative to the window
    /// start (matching the whisper.cpp processor contract the committer expects).
    /// </summary>
    IReadOnlyList<TranscriptSegment> Decode(ReadOnlyMemory<float> samples, CancellationToken cancellationToken);
}
