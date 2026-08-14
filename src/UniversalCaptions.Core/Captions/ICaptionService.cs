using UniversalCaptions.Core.Speech;
using UniversalCaptions.Core.Translation;

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

    /// <summary>
    /// Stops the session and discards the active line. New transcripts are no longer accepted, but
    /// committed finals already being translated are drained asynchronously and applied (bounded) so
    /// captions recognized just before the stop are not dropped. Returns immediately; the caller must
    /// not cancel any translation token afterwards. Idempotent.
    /// </summary>
    void Stop();

    /// <summary>Clears the session, history, and translation configuration, and cancels in-flight translations. Idempotent.</summary>
    void Reset();

    /// <summary>
    /// Enables or disables translation for newly committed lines. When enabled, committed lines are
    /// translated to <paramref name="targetLanguage"/> (or the configured default when null) as long
    /// as a translation engine is available.
    /// </summary>
    /// <remarks>
    /// Two history-scrubbing transitions are tied to this method:
    /// <list type="bullet">
    ///   <item>Disabling translation: clears every <see cref="LineOrigin.Translation"/> entry from
    ///   the committed history so the overlay returns to a pure source display. The active
    ///   translation line is also dropped.</item>
    ///   <item>Switching target language while translation stays on (e.g. <c>tl</c> → <c>ja</c>):
    ///   clears the previous target's history so the new session starts clean. SourceStt history
    ///   is preserved. Setting the same target language again is a no-op.</item>
    /// </list>
    /// </remarks>
    /// <param name="enabled">Whether translation is enabled.</param>
    /// <param name="targetLanguage">The ISO 639-1 target language, when overriding the configured default.</param>
    void SetTranslationEnabled(bool enabled, string? targetLanguage = null);

    /// <summary>
    /// Enables or disables this service's own caption-line translation path — the local
    /// <see cref="ITranslationEngine"/> applied to source lines. Set to false when a live audio
    /// translation engine owns translation (for example a cloud provider): the service then only
    /// relays translation-origin lines and never starts its own translations, so the two paths can
    /// never both fill the overlay. Independent of the common
    /// <see cref="CaptionState.TranslationEnabled"/>/<see cref="CaptionState.TargetLanguage"/> state,
    /// which reflects the user's translation toggle for every provider.
    /// </summary>
    /// <param name="enabled">True when this service should translate source lines itself.</param>
    void SetCaptionLineTranslation(bool enabled);

    /// <summary>
    /// Sets whether a live audio translation engine (a cloud provider such as Gemini) currently owns
    /// translation for the session. True when the live engine is the display (the overlay is
    /// target-language-only and source STT finals are hidden), false when the caption-line
    /// (local Argos) path owns translation or when translation is off. Drives the overlay's
    /// live-translation display mode explicitly — the mode must reflect the actual provider, never be
    /// inferred from stale history content (a provider change could otherwise flash the previous
    /// provider's untranslated English source).
    /// </summary>
    /// <param name="active">True while a live audio translation engine is the translation mechanism.</param>
    void SetLiveTranslationSession(bool active);

    /// <summary>
    /// Clears the committed history and both active lines while KEEPING the translation configuration
    /// (<see cref="CaptionState.TranslationEnabled"/> and <see cref="CaptionState.TargetLanguage"/>)
    /// and the running session. Used on a runtime provider change so the overlay starts clean under
    /// the new provider in the SAME target language — the selected target is never reset by switching
    /// the translation provider. Raises <see cref="StateChanged"/>.
    /// </summary>
    void ClearCaptionContent();

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
    /// Replaces the active translation line with a caption built from a partial translation. The
    /// translation lineage is independent from the STT lineage: a Whisper partial and a translation
    /// partial arriving at the same moment do not overwrite one another. Ignored while not running.
    /// </summary>
    /// <param name="translation">The partial translation. Must not be null.</param>
    void ProcessPartialTranslation(PartialTranslation translation);

    /// <summary>
    /// Commits a caption built from a final translation into the unified history. The translation
    /// active line is cleared. Ignored while not running.
    /// </summary>
    /// <param name="translation">The final translation. Must not be null.</param>
    void ProcessFinalTranslation(FinalTranslation translation);

    /// <summary>
    /// Removes every <see cref="LineOrigin.Translation"/> entry from the committed history, leaving
    /// <see cref="LineOrigin.SourceStt"/> entries (and any other origins) untouched. This is the
    /// "Translate OFF should not leave translated text mixed into English source captions" hook:
    /// the live translation session accumulates target-language history while it is on, and toggling
    /// translation off must scrub those entries so the overlay returns to source-only display.
    /// Language-agnostic: it does not filter by target language (Tagalog, Japanese, French, etc.),
    /// only by <see cref="LineOrigin"/>. No-op while not running or when there is nothing to clear
    /// (does not raise <see cref="StateChanged"/> in that case). The active translation line is NOT
    /// touched — that is <see cref="ProcessFinalTranslation"/>'s job and the live-failure path's.
    /// </summary>
    void ClearTranslationHistory();

    /// <summary>
    /// Resets every <em>displayed</em> translation from the committed history and the active
    /// translation line, so a runtime reconfiguration (target-language or provider change) starts
    /// clean: both the translation-origin lines a live engine (Gemini) produces AND the completed
    /// translations Argos attaches to source lines are dropped — the overlay must never mix one
    /// target's or provider's output into the next. Source STT history that carries no translation
    /// is untouched (unlike <see cref="ClearTranslationHistory"/>, which only handles the live-engine
    /// path). The active translation line is cleared so a stopped provider's in-progress line cannot
    /// linger as the display. Raises <see cref="StateChanged"/> when something was cleared.
    /// </summary>
    void ResetTranslatedContent();

    /// <summary>
    /// Discards the active translation line and raises <see cref="StateChanged"/>. Used by the
    /// pipeline when the live audio translation engine raises
    /// <see cref="Translation.ILiveAudioTranslationEngine.TranslationFailed"/> so the overlay stops
    /// painting a stale in-progress translation. Committed translated history stays visible while
    /// translation is ON (live-translation display policy). No-op when translation is disabled, no
    /// translation active line is set, or the service is not running.
    /// </summary>
    void ClearLiveTranslationActiveLine();

    /// <summary>
    /// Waits until all in-flight translations have settled (completed, failed, or cancelled).
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
