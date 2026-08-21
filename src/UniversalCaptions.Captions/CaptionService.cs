using System.Text.RegularExpressions;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.Captions;

/// <summary>
/// Turns speech transcripts into <see cref="CaptionState"/>: partials replace the active line and
/// finals commit lines to history. The service never translates anything itself — source captions
/// arrive via <see cref="ProcessPartial"/>/<see cref="ProcessFinal"/> (fed from the Gemini live
/// session's input transcription) and translated captions are relayed via
/// <see cref="ProcessPartialTranslation"/>/<see cref="ProcessFinalTranslation"/> (the same Gemini
/// session's output transcription). A translation failure on the live engine is handled by the
/// pipeline; the service only ever relays what engines emit.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProcessPartial"/> and <see cref="ProcessFinal"/> are synchronous so they can be called
/// directly from engine event handlers. State mutations are serialized on an internal gate; events
/// are raised outside the gate so a slow subscriber cannot stall the pipeline.
/// </para>
/// <para>
/// Translation-origin ingress is gated by the common
/// <see cref="CaptionState.TranslationEnabled"/>/<see cref="CaptionState.TargetLanguage"/> state plus
/// two stale guards: a stale-target guard (the event's target must match the session target) and a
/// stale-session guard (<see cref="_liveSessionStartedAtUtc"/>) so content produced before the
/// current live session began can never commit into it.
/// </para>
/// </remarks>
public sealed class CaptionService : ICaptionService
{
    private readonly CaptionServiceOptions _options;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private readonly CaptionState _state;
    private CancellationTokenSource? _lifetimeCts;
    private bool _isLiveTranslationSession;
    private volatile bool _running;

    /// <summary>
    /// Monotonic translation-session identity. Bumped on every session boundary — translation toggle
    /// on/off, target-language change, caption-line/live mode change, provider content reset — so a
    /// translation result that was requested in a PREVIOUS session is discarded even when the current
    /// state (enabled + target) happens to match again.
    /// </summary>
    private long _translationSessionEpoch;

    /// <summary>
    /// The instant a NEW live-translation session (Gemini engine) became active. Translation-origin
    /// input that was produced BEFORE this boundary is stale — it belongs to the previous session and
    /// must not commit into this one even when it carries the same target language (toggle OFF → ON
    /// with the same target re-arms the enabled/target guards).
    /// </summary>
    private DateTime? _liveSessionStartedAtUtc;

    private static readonly Regex BracketRegex = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    private static string CleanText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        return BracketRegex.Replace(text, "").Trim();
    }

    /// <summary>
    /// Creates a caption service that builds captions in <see cref="CaptionServiceOptions.SourceLanguage"/>.
    /// </summary>
    /// <param name="options">The caption service options.</param>
    /// <param name="utcNow">An optional clock used to stamp live-session boundaries (defaults to <see cref="DateTime.UtcNow"/>). Inject a deterministic clock in tests.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public CaptionService(CaptionServiceOptions options, Func<DateTime>? utcNow = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _state = new CaptionState(options.HistoryCapacity);
    }

    /// <inheritdoc />
    public event EventHandler<CaptionLine>? ActiveLineChanged;

    /// <inheritdoc />
    public event EventHandler<CaptionLine>? CaptionLineCommitted;

    /// <inheritdoc />
    public event EventHandler<CaptionLine>? CaptionLineUpdated;

    /// <inheritdoc />
    public event EventHandler<CaptionState>? StateChanged;

    /// <inheritdoc />
    public CaptionState State => _state;

    /// <inheritdoc />
    public CaptionSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new CaptionSnapshot(
                _state.ActiveLine,
                _state.History,
                _state.IsSessionActive,
                _state.TranslationEnabled,
                _state.TargetLanguage,
                _state.ActiveTranslationLine,
                _isLiveTranslationSession);
        }
    }

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <inheritdoc />
    public void Start()
    {
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _lifetimeCts = new CancellationTokenSource();
            _state.BeginSession();
        }

        StateChanged?.Invoke(this, _state);
    }

    /// <inheritdoc />
    public void Stop()
    {
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            _state.EndSession();
            lifetime = _lifetimeCts;
            _lifetimeCts = null;
        }

        CancelLifetime(lifetime);
        StateChanged?.Invoke(this, _state);
    }

    /// <inheritdoc />
    public void Reset()
    {
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            _running = false;
            _state.Reset();
            lifetime = _lifetimeCts;
            _lifetimeCts = null;
        }

        CancelLifetime(lifetime);
        StateChanged?.Invoke(this, _state);
    }

    /// <summary>
    /// Cancels and disposes a retired session's lifetime token. The token is captured under the gate
    /// by reference, so a session re-started in the meantime owns a different token and is never touched.
    /// </summary>
    private static void CancelLifetime(CancellationTokenSource? lifetime)
    {
        if (lifetime is null)
        {
            return;
        }

        try
        {
            lifetime.Cancel();
        }
        catch (AggregateException)
        {
            // A subscriber's cancellation callback threw; the session still ends.
        }

        lifetime.Dispose();
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">Translation is enabled but no target language is configured.</exception>
    public void SetTranslationEnabled(bool enabled, string? targetLanguage = null)
    {
        string? target = targetLanguage ?? _options.TargetLanguage;
        lock (_gate)
        {
            // Detect a target-language change while translation stays on: scrubbing the previous
            // target's history prevents mixed-language history (e.g. Tagalog lines bleeding into a
            // newly-selected Japanese session). Comparing to the state's current target (lowercased
            // by CaptionState.SetTranslation) means "set same language again" is correctly a no-op.
            bool languageChanged = enabled
                && _state.TranslationEnabled
                && !string.Equals(_state.TargetLanguage, target, StringComparison.Ordinal);

            // Every toggle or target change is a FRESH translation-session boundary.
            _translationSessionEpoch++;

            _state.SetTranslation(enabled, target);
            if (!enabled)
            {
                // The live translation line belongs to a session that has been switched off: drop it
                // so re-enabling never resurfaces stale translated text from before the toggle.
                _state.ClearTranslationActiveLine();

                // Toggling translation OFF must not leave target-language history mixed into the new
                // source-only stream. RevertTranslatedContentToSource removes every
                // LineOrigin.Translation entry (language-agnostic). The active translation line is
                // handled by ClearTranslationActiveLine above; this method is scoped to the history.
                _state.RevertTranslatedContentToSource();
            }
            else if (languageChanged)
            {
                // Switching target language mid-session (e.g. tl → ja) must drop the previous target's
                // output so the new session starts clean. The new target's history will populate as
                // the engine emits.
                _state.RevertTranslatedContentToSource();
                _state.ClearTranslationActiveLine();
            }
        }

        StateChanged?.Invoke(this, _state);
    }

    /// <inheritdoc />
    public void ClearTranslationHistory()
    {
        if (!_running)
        {
            return;
        }

        bool cleared;
        lock (_gate)
        {
            cleared = _state.ClearTranslationHistory() > 0;
        }

        if (cleared)
        {
            StateChanged?.Invoke(this, _state);
        }
    }

    /// <inheritdoc />
    public void ClearLiveTranslationActiveLine()
    {
        if (!_running || !_state.TranslationEnabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_state.ActiveTranslationLine is null)
            {
                return;
            }

            _state.ClearTranslationActiveLine();
        }

        StateChanged?.Invoke(this, _state);
    }

    /// <inheritdoc />
    public void ResetTranslatedContent()
    {
        if (!_running)
        {
            return;
        }

        bool cleared;
        lock (_gate)
        {
            _translationSessionEpoch++;
            cleared = _state.RevertTranslatedContentToSource() > 0;
            if (_state.ActiveTranslationLine is not null)
            {
                _state.ClearTranslationActiveLine();
                cleared = true;
            }
        }

        if (cleared)
        {
            StateChanged?.Invoke(this, _state);
        }
    }

    /// <inheritdoc />
    public void SetLiveTranslationSession(bool active)
    {
        lock (_gate)
        {
            // A false → true transition means the pipeline started a NEW live engine (a fresh
            // translation session). Record the boundary so translation-origin input produced before
            // it is dropped: an old session's message that carries the same target (toggle OFF → ON)
            // must never commit into the new session.
            if (!_isLiveTranslationSession && active)
            {
                _liveSessionStartedAtUtc = _utcNow();
            }

            if (_isLiveTranslationSession == active)
            {
                return;
            }

            _isLiveTranslationSession = active;
        }

        StateChanged?.Invoke(this, _state);
    }

    /// <inheritdoc />
    public void ClearCaptionContent()
    {
        lock (_gate)
        {
            // Content reset = a fresh translation session: any result still in flight under the
            // previous configuration must not apply under the new one.
            _translationSessionEpoch++;
            _state.ClearContent();
        }

        StateChanged?.Invoke(this, _state);
    }

    /// <inheritdoc />
    public void ProcessPartial(PartialTranscript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        if (!_running)
        {
            return;
        }

        string cleanText = CleanText(transcript.Text);
        if (string.IsNullOrWhiteSpace(cleanText))
        {
            // Ignore empty transcripts (and those that were just bracketed noise) so we don't
            // clear the active line when audio stops/pauses and the engine emits silence.
            return;
        }

        var line = new CaptionLine(
            cleanText,
            _options.SourceLanguage,
            transcript.Sequence,
            transcript.CapturedAtUtc,
            CaptionLineState.Active);

        lock (_gate)
        {
            _state.UpdateActiveLine(line);
        }

        ActiveLineChanged?.Invoke(this, line);
        CaptionLineUpdated?.Invoke(this, line);
        StateChanged?.Invoke(this, _state);
    }

    /// <inheritdoc />
    public void ProcessFinal(FinalTranscript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        if (!_running)
        {
            return;
        }

        string cleanText = CleanText(transcript.Text);
        if (string.IsNullOrWhiteSpace(cleanText))
        {
            // Ignore empty finals (and those that were just bracketed noise) to avoid committing
            // empty/blank rows to the history list.
            return;
        }

        // Three-way duplicate / overlap check against the most-recent committed line.
        // Streaming engines re-emitting overlapping windows produce three kinds of repeat emissions:
        //
        //  (A) Exact duplicate:   prev == new  → drop new.
        //  (B) Truncated repeat:  new is a leading subset of prev (the engine re-emits a
        //                         shorter cut of a sentence it already committed in full).
        //                         e.g. prev  = "AI. So we're talking about where the space is."
        //                              new   = "AI. So we're talking about where the space"
        //                         → drop new.
        //  (C) Extension:         prev is a prefix of new (new text adds words after prev).
        //                         e.g. prev  = "Where the space"
        //                              new   = "where the space is, what is this whole AI shift…"
        //                         → strip the shared prefix from new before committing.
        lock (_gate)
        {
            if (_state.History.Count > 0)
            {
                string prevText = _state.History[^1].Text;

                // (A) Exact duplicate.
                if (string.Equals(prevText, cleanText, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // (B) New text is a truncated version of the previous sentence.
                if (IsLeadingSubsetOf(prevText, cleanText))
                {
                    return;
                }

                // (C) New text extends the previous — strip the overlapping prefix.
                string stripped = StripHistoryOverlap(prevText, cleanText);
                if (string.IsNullOrWhiteSpace(stripped))
                {
                    return;
                }

                cleanText = stripped;
            }
        }

        var line = new CaptionLine(
            cleanText,
            _options.SourceLanguage,
            transcript.Sequence,
            transcript.CapturedAtUtc,
            CaptionLineState.Final,
            committedAtUtc: transcript.EmittedAtUtc);

        Commit(line);
    }

    /// <inheritdoc />
    public void ProcessPartialTranslation(PartialTranslation translation)
    {
        ArgumentNullException.ThrowIfNull(translation);
        if (!_running || !_state.TranslationEnabled)
        {
            // Not running, or the user toggled translation off: translation-origin content is not
            // accepted, so the overlay returns to the source captions immediately even if a live
            // engine event is still in flight from just before the toggle.
            return;
        }

        // Stale-target guard: a live engine event whose target does not match the current session
        // target (the user changed target mid-session before the engine swap completed) must be
        // dropped so the previous target's output never bleeds into the new session.
        if (!string.Equals(_state.TargetLanguage, translation.TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Stale-session guard: translation-origin content produced before the current live session
        // began (a message from the previous engine that still carries the same target — toggle OFF
        // → ON with the same target) must not update the new session.
        if (IsStaleLiveSessionInput(translation.EmittedAtUtc))
        {
            return;
        }

        string cleanText = CleanText(translation.TranslatedText);
        if (string.IsNullOrWhiteSpace(cleanText))
        {
            // Ignore empty translation partials so we don't clear the active translation line when
            // the engine emits whitespace-only frames.
            return;
        }

        // The translation active line carries the translated text in `Text` (it is the text shown to
        // the user). Translation was already performed by the engine that produced this event, so
        // translation status stays NotRequested. Timing: request start = the engine's capture-time
        // stamp; completion = the moment this result is actually applied (drives E2E latency).
        var line = new CaptionLine(
            cleanText,
            translation.TargetLanguage,
            translation.Sequence,
            translation.CapturedAtUtc,
            CaptionLineState.Active,
            targetLanguage: translation.TargetLanguage,
            translatedText: cleanText,
            translationStatus: CaptionTranslationStatus.NotRequested,
            translationStartedAtUtc: translation.CapturedAtUtc,
            translationCompletedAtUtc: _utcNow(),
            origin: LineOrigin.Translation);

        lock (_gate)
        {
            _state.UpdateTranslationActiveLine(line);
        }

        ActiveLineChanged?.Invoke(this, line);
        CaptionLineUpdated?.Invoke(this, line);
        StateChanged?.Invoke(this, _state);
    }

    /// <inheritdoc />
    public void ProcessFinalTranslation(FinalTranslation translation)
    {
        ArgumentNullException.ThrowIfNull(translation);
        if (!_running || !_state.TranslationEnabled)
        {
            // See ProcessPartialTranslation: translation-origin content is dropped once translation
            // is disabled so the overlay reflects the toggle without stale translated finals.
            return;
        }

        // Stale-target guard: see ProcessPartialTranslation — an old engine's final must not commit
        // into a session whose target already changed.
        if (!string.Equals(_state.TargetLanguage, translation.TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Stale-session guard: see ProcessPartialTranslation — an old session's final that still
        // carries the same target (toggle OFF → ON with the same target) must not commit into the
        // new live session.
        if (IsStaleLiveSessionInput(translation.EmittedAtUtc))
        {
            return;
        }

        string cleanText = CleanText(translation.TranslatedText);
        if (string.IsNullOrWhiteSpace(cleanText))
        {
            return;
        }

        var line = new CaptionLine(
            cleanText,
            translation.TargetLanguage,
            translation.Sequence,
            translation.CapturedAtUtc,
            CaptionLineState.Final,
            committedAtUtc: translation.CommittedAtUtc,
            targetLanguage: translation.TargetLanguage,
            translatedText: cleanText,
            translationStatus: CaptionTranslationStatus.NotRequested,
            translationStartedAtUtc: translation.CapturedAtUtc,
            translationCompletedAtUtc: _utcNow(),
            origin: LineOrigin.Translation);

        Commit(line);
    }

    private void Commit(CaptionLine line)
    {
        lock (_gate)
        {
            _state.AddFinalLine(line);
            // AddFinalLine already clears the matching origin's active slot; no extra work needed.
        }

        CaptionLineCommitted?.Invoke(this, line);
        CaptionLineUpdated?.Invoke(this, line);
        StateChanged?.Invoke(this, _state);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            _running = false;
            lifetime = _lifetimeCts;
            _lifetimeCts = null;
        }

        CancelLifetime(lifetime);
    }

    /// <summary>
    /// True when translation-origin input was emitted before the current live session began, i.e. it
    /// belongs to a previous engine's session (toggle OFF → ON or a stale message racing a boundary).
    /// Must be called holding <see cref="_gate"/> or with the state already stable.
    /// <para>
    /// Compares <see cref="TranslationTranscript.EmittedAtUtc"/> — NOT <see cref="TranslationTranscript.CapturedAtUtc"/> —
    /// because the Gemini live engine stamps every transcript with a single fixed session-start base
    /// timestamp (its audio capture time, not a per-message value). A boundary recorded after the new
    /// engine starts would therefore classify every legitimate new transcript as stale if the guard
    /// compared capture time. EmittedAtUtc is the real per-message time, so it cleanly separates a
    /// message produced before the boundary from one produced after it.
    /// </para>
    /// </summary>
    private bool IsStaleLiveSessionInput(DateTime emittedAtUtc) =>
        _liveSessionStartedAtUtc is { } boundary && emittedAtUtc < boundary;

    /// <summary>
    /// Strips any word-level suffix of <paramref name="prev"/> that appears as a prefix of
    /// <paramref name="next"/>, returning the remainder of <paramref name="next"/> that is genuinely
    /// new.  Words are compared case-insensitively so that capitalisation changes at the start of a
    /// new utterance do not defeat the deduplication.
    /// </summary>
    /// <remarks>
    /// We look for the longest suffix of <paramref name="prev"/>'s words that matches a prefix of
    /// <paramref name="next"/>'s words. When found, we drop those leading words from
    /// <paramref name="next"/> and return the trimmed result.  If no overlap is found we return
    /// <paramref name="next"/> unchanged.
    /// </remarks>
    private static string StripHistoryOverlap(string prev, string next)
    {
        if (string.IsNullOrWhiteSpace(prev) || string.IsNullOrWhiteSpace(next))
        {
            return next;
        }

        // Split into lower-cased word tokens for comparison.
        string[] prevWords = prev.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] nextWords = next.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (prevWords.Length == 0 || nextWords.Length == 0)
        {
            return next;
        }

        // Try progressively shorter suffixes of prevWords against a matching prefix of nextWords.
        // Start with the minimum of (prevWords.Length, nextWords.Length) to avoid scanning past
        // what can possibly overlap.
        int maxOverlap = Math.Min(prevWords.Length, nextWords.Length);

        for (int overlap = maxOverlap; overlap >= 1; overlap--)
        {
            bool match = true;
            for (int i = 0; i < overlap; i++)
            {
                string pw = prevWords[prevWords.Length - overlap + i];
                string nw = nextWords[i];
                // Strip trailing punctuation before comparing so "sentence." == "sentence".
                pw = pw.TrimEnd('.', ',', '!', '?', ';', ':');
                nw = nw.TrimEnd('.', ',', '!', '?', ';', ':');
                if (!string.Equals(pw, nw, StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                // Drop the overlapping prefix words from next.
                string[] remaining = nextWords[overlap..];
                return remaining.Length == 0 ? string.Empty : string.Join(' ', remaining);
            }
        }

        return next;
    }

    /// <summary>
    /// Returns true when <paramref name="candidate"/> is a leading subset of <paramref name="prev"/>:
    /// all words of <paramref name="candidate"/> match the beginning of <paramref name="prev"/> in
    /// order (case-insensitive, punctuation-stripped), and <paramref name="candidate"/> is strictly
    /// shorter than <paramref name="prev"/> by at least one word.
    /// </summary>
    /// <remarks>
    /// This detects case (B) — the engine re-emitting a truncated version of a sentence it already
    /// committed in full, e.g. committing "AI. So we're talking about where the space is." and
    /// then emitting "AI. So we're talking about where the space" as a spurious follow-up final.
    /// The minimum of 3 words guards against accidental matches on very short shared prefixes
    /// (e.g. "The cat" matching "The cat sat on the mat" would be too aggressive).
    /// </remarks>
    private static bool IsLeadingSubsetOf(string prev, string candidate)
    {
        if (string.IsNullOrWhiteSpace(prev) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string[] prevWords = prev.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] candWords = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Candidate must be strictly shorter than prev.
        if (candWords.Length >= prevWords.Length)
        {
            return false;
        }

        // Every word of candidate must match the corresponding leading word of prev.
        for (int i = 0; i < candWords.Length; i++)
        {
            string pw = prevWords[i].TrimEnd('.', ',', '!', '?', ';', ':');
            string cw = candWords[i].TrimEnd('.', ',', '!', '?', ';', ':');
            if (!string.Equals(pw, cw, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
