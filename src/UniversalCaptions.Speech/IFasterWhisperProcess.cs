namespace UniversalCaptions.Speech;

/// <summary>
/// A persistent faster-whisper worker process. The engine owns windowing/commit orchestration; a
/// process only turns one audio window into <see cref="TranscriptSegment"/>s. Implementations must
/// load the model exactly once and keep the process alive across decodes.
/// </summary>
internal interface IFasterWhisperProcess : IAsyncDisposable
{
    /// <summary>
    /// Spawns the worker and waits for it to load its model (ping round-trip). Throws
    /// <see cref="FasterWhisperProcessException"/> when the process cannot start or become ready.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Transcribes a window of mono 16 kHz int16 samples. Throws
    /// <see cref="FasterWhisperProcessException"/> on protocol/engine failure.
    /// </summary>
    Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        ReadOnlyMemory<short> pcmSamples,
        string? language,
        CancellationToken cancellationToken);
}
