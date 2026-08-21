using UniversalCaptions.Core.Speech;
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.Core.Captions;

/// <summary>
/// Turns speech transcripts into <see cref="CaptionState"/>: partials update the active line and
/// finals commit lines to history. The service is a relay, not a translator: source captions arrive
/// from the speech pipeline and translated captions are relayed from the live audio translation
/// engine. Implementations are engine-neutral and carry no UI concepts.
/// </summary>
/// <remarks>
/// Transcripts are fed synchronously from engine event handlers via <see cref="ProcessPartial"/>
/// and <see cref="ProcessFinal"/>.
/// </remarks>
public interface ICaptionService : IDisposable
{
    /// <summary>Raised when the active line is replaced by a newer partial.</summary>
    event EventHandler<CaptionLine>? ActiveLineChanged;

    /// <summary>Raised when a final line is committed to history.</summary>
    event EventHandler<CaptionLine>? CaptionLineCommitted;

    /// <summary>Raised whenever a caption line is published or updated (active-line updates and commits).</summary>
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
    /// Stops the session and discards the active line. New transcripts are no longer accepted.
    /// Returns immediately. Idempotent.
    /// </summary>
    void Stop();

    /// <summary>Clears the session, history, and translation configuration. Idempotent.</summary>
    void Reset();

    /// <summary>
    /// Enables or disables translation for the session (the common
    /// <see cref="CaptionState.TranslationEnabled"/>/<see cref="CaptionState.TargetLanguage"/> state).
    /// When disabled, translation-origin input is rejected and translated content is scrubbed.
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
    /// Sets whether a live audio translation engine (Gemini) currently owns
    /// translation for the session. True when the live engine is the display (the overlay is
    /// target-language-only and source transcription finals are hidden), false when translation is
    /// off. Drives the overlay's
    /// live-translation display mode explicitly — the mode must reflect the actual engine, never be
    /// inferred from stale history content (an engine swap could otherwise flash the previous
    /// session's untranslated source).
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
    /// Commits a caption built from a final transcript. Ignored while not running.
    /// </summary>
    /// <param name="transcript">The final transcript. Must not be null.</param>
    void ProcessFinal(FinalTranscript transcript);

    /// <summary>
    /// Replaces the active translation line with a caption built from a partial translation. The
    /// translation lineage is independent from the transcription lineage: a transcription partial and a
    /// translation partial arriving at the same moment do not overwrite one another. Ignored while not running.
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
    /// translation line, so a runtime reconfiguration (target-language change or toggle cycle) starts
    /// clean: the translation-origin lines the live engine (Gemini) produces are dropped — the
    /// overlay must never mix one target's output into the next. Source transcription history is
    /// untouched. The active translation line is cleared so a stopped engine's in-progress line cannot
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
}
