using UniversalCaptions.Core.Speech;

namespace UniversalCaptions.Core.Captions;

/// <summary>
/// Turns speech transcripts into <see cref="CaptionState"/>: partials update the active line and
/// finals commit lines to history. When translation is enabled and an engine is available, committed
/// lines are translated in the background; a translation failure never destroys the source caption.
/// Implementations are engine-neutral and carry no UI concepts.
/// </summary>
/// <remarks>
/// Transcripts are fed synchronously from speech engine event handlers via <see cref="ProcessPartial"/>
/// and <see cref="ProcessFinal"/>. Translations run as background tasks; <see cref="FlushAsync"/> waits
/// for in-flight translations and is the deterministic hook tests (and callers ending a session) use.
/// </remarks>
public interface ICaptionService : IDisposable
{
    /// <summary>Raised when the active line is replaced by a newer partial.</summary>
    event EventHandler<CaptionLine>? ActiveLineChanged;

    /// <summary>Raised when a final line is committed to history.</summary>
    event EventHandler<CaptionLine>? CaptionLineCommitted;

    /// <summary>Raised when a committed line's translation completes or fails.</summary>
    event EventHandler<CaptionLine>? CaptionLineUpdated;

    /// <summary>Raised after any change to <see cref="State"/>.</summary>
    event EventHandler<CaptionState>? StateChanged;

    /// <summary>The current caption state, updated by this service.</summary>
    CaptionState State { get; }

    /// <summary>
    /// Returns a consistent, immutable snapshot of the current caption state. Unlike reading
    /// <see cref="State"/> directly, this is safe to call from any thread while the service is being
    /// mutated (events are raised outside the service's internal lock, so live reads can race a
    /// history commit or a reset).
    /// </summary>
    /// <returns>An immutable copy of the current caption state.</returns>
    CaptionSnapshot GetSnapshot();

    /// <summary>True while the service accepts transcripts (after <see cref="Start"/>, before <see cref="Stop"/>).</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts the caption session. Transcripts fed before <see cref="Start"/> or after <see cref="Stop"/>
    /// are ignored. Idempotent.
    /// </summary>
    void Start();

    /// <summary>Stops the session, discards the active line, and cancels in-flight translations. Idempotent.</summary>
    void Stop();

    /// <summary>Clears the session, history, and translation configuration, and cancels in-flight translations. Idempotent.</summary>
    void Reset();

    /// <summary>
    /// Enables or disables translation for newly committed lines. When enabled, committed lines are
    /// translated to <paramref name="targetLanguage"/> (or the configured default when null) as long
    /// as a translation engine is available.
    /// </summary>
    /// <param name="enabled">Whether translation is enabled.</param>
    /// <param name="targetLanguage">The ISO 639-1 target language, when overriding the configured default.</param>
    void SetTranslationEnabled(bool enabled, string? targetLanguage = null);

    /// <summary>
    /// Replaces the active line with a caption built from a partial transcript. Ignored while not running.
    /// </summary>
    /// <param name="transcript">The partial transcript. Must not be null.</param>
    void ProcessPartial(PartialTranscript transcript);

    /// <summary>
    /// Commits a caption built from a final transcript. When translation is enabled, the committed line
    /// is marked pending and translated in the background. Ignored while not running.
    /// </summary>
    /// <param name="transcript">The final transcript. Must not be null.</param>
    void ProcessFinal(FinalTranscript transcript);

    /// <summary>
    /// Waits until all in-flight translations have settled (completed, failed, or cancelled).
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
