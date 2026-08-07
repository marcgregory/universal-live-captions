using UniversalCaptions.Core.Audio;

namespace UniversalCaptions.Core.Translation;

/// <summary>
/// Translates a continuous stream of captured audio into target-language text events. Designed for
/// engines that ingest raw PCM directly (for example, Gemini Live Translate's audio-only input) and
/// surface their output via server-side transcription. Parallel in shape to
/// <see cref="UniversalCaptions.Core.Speech.ISpeechToTextEngine"/>; engines that produce text from
/// text (Argos, Gemini text API, etc.) are not <see cref="ILiveAudioTranslationEngine"/> implementations.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PushAudio"/> is the hot path: it runs on the audio capture callback thread and MUST
/// return without performing network I/O and MUST NOT throw. The engine owns its buffering and
/// WebSocket lifecycle. A bounded internal queue protects the pipeline from network backpressure
/// at the cost of dropping audio when the network cannot keep up; the drop policy is an engine
/// implementation detail.
/// </para>
/// <para>
/// Lifecycle is asynchronous because the underlying transport is network I/O:
/// <see cref="StartAsync"/> opens the session and <see cref="StopAsync"/> closes it. Startup and
/// connection failures are reported through <see cref="TranslationFailed"/>.
/// </para>
/// </remarks>
public interface ILiveAudioTranslationEngine : IDisposable
{
    /// <summary>
    /// Raised as translation progresses. The text of a partial is provisional and may be revised by
    /// later partials or a <see cref="FinalTranslation"/> for the same utterance.
    /// </summary>
    event EventHandler<PartialTranslation>? PartialTranslationAvailable;

    /// <summary>Raised when a stable translation is available for a completed utterance.</summary>
    event EventHandler<FinalTranslation>? FinalTranslationAvailable;

    /// <summary>Raised when the engine fails (for example, the API key is rejected, the WebSocket dies, or the model is unavailable).</summary>
    event EventHandler<LiveTranslationError>? TranslationFailed;

    /// <summary>
    /// Starts the engine. Opens the live session and begins draining the audio buffer to the server.
    /// </summary>
    /// <param name="cancellationToken">Cancels the startup handshake.</param>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the engine and closes the live session.
    /// </summary>
    /// <param name="cancellationToken">Cancels the shutdown handshake.</param>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously hands a chunk of captured audio to the engine. MUST return without performing
    /// network I/O. MUST NOT throw. Chunks fed before <see cref="StartAsync"/> or after
    /// <see cref="StopAsync"/> are ignored. The chunk must not be mutated after the call returns.
    /// </summary>
    /// <param name="chunk">The captured audio to translate. Expected format matches the rest of the pipeline (16 kHz, 1 channel, 32-bit float).</param>
    void PushAudio(AudioChunk chunk);
}
