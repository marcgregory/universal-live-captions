using System.Text.RegularExpressions;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.Captions;

/// <summary>
/// Turns speech transcripts into <see cref="CaptionState"/>: partials replace the active line and
/// finals commit lines to history. When translation is enabled and an <see cref="ITranslationEngine"/>
/// is available, the in-progress active line and committed lines are translated in the background and
/// the result or failure is applied to the line without ever replacing its source text.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProcessPartial"/> and <see cref="ProcessFinal"/> are synchronous so they can be called
/// directly from speech engine event handlers. Translations are launched as background tasks and
/// applied to <see cref="State"/>; <see cref="FlushAsync"/> waits for them to settle and is the
/// deterministic hook tests use. State mutations and the in-flight task set are serialized on an
/// internal gate; events are raised outside the gate so a slow subscriber cannot stall the pipeline.
/// </para>
/// <para>
/// The active line is translated live with a single in-flight slot: at most one active-line
/// translation runs at a time (the translation backend serializes requests), and when it completes,
/// a newer partial that arrived meanwhile is translated next. A partial that is going to be
/// translated is not published to subscribers in its source language — doing so would flash the
/// source language on the overlay between translations — so the overlay shows the previous
/// translated caption until the live translation of the newest partial completes, and then captions
/// read in the target language while the speaker is still talking.
/// </para>
/// <para>
/// The caption-line translation path is gated by <see cref="SetCaptionLineTranslation"/>: when a live
/// audio translation engine owns translation (Gemini), the service never starts its own translations
/// of source lines — it only relays translation-origin lines via
/// <see cref="ProcessPartialTranslation"/>/<see cref="ProcessFinalTranslation"/>. The common
/// <see cref="CaptionState.TranslationEnabled"/>/<see cref="CaptionState.TargetLanguage"/> state is
/// provider-independent: it always reflects the user's translation toggle, so the overlay behaves the
/// same for every provider.
/// </para>
/// <para>
/// A translation failure is represented on the line with <see cref="CaptionTranslationStatus.Failed"/>
/// and the source text remains intact in <see cref="CaptionLine.Text"/>. Cancellation (session stopped,
/// reset, or disposed) leaves the line in <see cref="CaptionTranslationStatus.Pending"/>. A stale
/// translation result is never applied: it is matched to the exact line instance it was started from,
/// so a re-committed line under the same sequence or a newer partial cannot be overwritten, and it is
/// guarded by the translation-session identity captured when the request started — a result requested
/// in a previous session (toggle off then on, target change, provider change) is discarded even when
/// the current enabled/target state happens to match again.
/// </para>
/// </remarks>
public sealed class CaptionService : ICaptionService
{
    private readonly CaptionServiceOptions _options;
    private readonly ITranslationEngine? _translationEngine;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private readonly HashSet<Task> _inFlight = new();
    private readonly CaptionState _state;
    private readonly TimeSpan _stopDrainBudget;
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _retiredCts;
    private Task? _activeLineTranslation;
    private bool _useCaptionLineTranslation = true;
    private bool _isLiveTranslationSession;
    private volatile bool _running;

    /// <summary>
    /// Monotonic translation-session identity. Bumped on every session boundary — translation toggle
    /// on/off, target-language change, caption-line/live mode change, provider content reset — so a
    /// translation result that was requested in a PREVIOUS session is discarded even when the current
    /// state (enabled + target) happens to match again. This is what makes "toggle OFF then ON with
    /// the same target" a fresh session: without it, a pre-OFF in-flight result re-armed the stale
    /// guards and landed its old-target text on the still-live active line (the reported leak).
    /// </summary>
    private long _translationSessionEpoch;

    /// <summary>
    /// The instant a NEW live-translation session (Gemini engine) became active. Translation-origin
    /// input that was produced BEFORE this boundary is stale — it belongs to the previous session and
    /// must not commit into this one even when it carries the same target language (toggle OFF → ON
    /// with the same target re-arms the enabled/target guards, exactly like the Argos epoch guard
    /// protects against pre-OFF engine results).
    /// </summary>
    private DateTime? _liveSessionStartedAtUtc;

    /// <summary>
    /// How long <see cref="BeginStopDrain"/> waits for in-flight committed-final translations to
    /// settle before force-cancelling the remaining work. <see cref="Stop"/> never blocks the caller
    /// for this long — the drain runs in the background — so a modest budget only bounds how far the
    /// already-queued finals are allowed to complete, not how long the caller waits.
    /// </summary>
    private static readonly TimeSpan DefaultStopDrainBudget = TimeSpan.FromSeconds(8);

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
    /// Creates a caption service that builds captions in <see cref="CaptionServiceOptions.SourceLanguage"/>
    /// and, when translation is enabled, translates committed lines via <paramref name="translationEngine"/>.
    /// </summary>
    /// <param name="options">The caption service options.</param>
    /// <param name="translationEngine">The optional translation engine used when translation is enabled.</param>
    /// <param name="utcNow">An optional clock used to stamp translation start/completion times (defaults to <see cref="DateTime.UtcNow"/>). Inject a deterministic clock in tests.</param>
    /// <param name="stopDrainBudget">
    /// How long <see cref="Stop"/> allows already-queued committed-final translations to drain before
    /// force-cancelling them. Defaults to <see cref="DefaultStopDrainBudget"/>. <see cref="Stop"/>
    /// returns immediately regardless; this only bounds the background drain.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stopDrainBudget"/> is not positive.</exception>
    public CaptionService(
        CaptionServiceOptions options,
        ITranslationEngine? translationEngine = null,
        Func<DateTime>? utcNow = null,
        TimeSpan? stopDrainBudget = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _translationEngine = translationEngine;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _state = new CaptionState(options.HistoryCapacity);
        var budget = stopDrainBudget ?? DefaultStopDrainBudget;
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stopDrainBudget), budget, "StopDrainBudget must be positive.");
        }

        _stopDrainBudget = budget;
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
        }

        // Do not cancel in-flight translations yet: already-committed finals must drain and be applied
        // so captions recognized just before the stop are not dropped. Stop returns immediately and a
        // bounded background drain finishes them in FIFO order, then force-cancels whatever remains.
        BeginStopDrain(lifetime);

        StateChanged?.Invoke(this, _state);
    }

    /// <summary>
    /// Starts a bounded, asynchronous drain of the translations already in flight at the moment
    /// <see cref="Stop"/> was called. Enabling no new transcripts (session is not running), the drain
    /// lets any already-committed finals complete their translation and be applied in order. When the
    /// budget elapses, the session's cancellation source is cancelled and disposed, force-ending any
    /// remaining request. A session re-started in the meantime owns a different token and is never
    /// touched, because the drain retires the specific source it captured at stop.
    /// </summary>
    private void BeginStopDrain(CancellationTokenSource? lifetime)
    {
        if (lifetime is null)
        {
            EndLifetime();
            return;
        }

        _ = Task.Run(() => DrainThenStopAsync(lifetime));
    }

    private async Task DrainThenStopAsync(CancellationTokenSource lifetime)
    {
        // Wait for the in-flight translations to settle, but never longer than the stop budget. A
        // request that hangs must not block the drain indefinitely, so the completion wait itself is
        // bounded by the remaining budget (Task.WhenAny is raced against a delay); once the deadline
        // passes, RetireStoppedLifetime cancels the token and force-ends whatever still runs.
        DateTime deadline = _utcNow().Add(_stopDrainBudget);
        while (true)
        {
            Task[] snapshot;
            lock (_gate)
            {
                snapshot = _inFlight.ToArray();
            }

            if (snapshot.Length == 0)
            {
                break;
            }

            TimeSpan remaining = deadline - _utcNow();
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            Task completion = Task.WhenAll(snapshot);
            Task timeout = Task.Delay(remaining);
            if (await Task.WhenAny(completion, timeout).ConfigureAwait(false) == timeout)
            {
                break;
            }

            // completion won; loop re-snapshots (some tasks may have been superseded) until drained.
        }

        RetireStoppedLifetime(lifetime);
    }

    /// <summary>
    /// Cancels and disposes the specific capture-time lifetime token captured at stop, guarded by
    /// reference identity so a re-created session does not get its token touched. Only when the
    /// in-flight set is empty, or after the stop budget expired, is cancellation performed; if work
    /// is still running the token is retired and the last task to finish disposes it.
    /// </summary>
    private void RetireStoppedLifetime(CancellationTokenSource lifetime)
    {
        CancellationTokenSource? toCancel;
        CancellationTokenSource? toDispose;
        lock (_gate)
        {
            if (!ReferenceEquals(_lifetimeCts, lifetime))
            {
                // A new session has already started (or the service was reset/disposed): it owns a new
                // token, so this retired token is released without ever touching the current session.
                // The drain already awaited the in-flight set (or the budget elapsed), so it is safe
                // to cancel and dispose it here.
                toCancel = lifetime;
                toDispose = null;
            }
            else if (_inFlight.Count == 0)
            {
                // Everything drained during the budget; dispose directly, no cancellation outstanding.
                _lifetimeCts = null;
                _retiredCts = null;
                toCancel = null;
                toDispose = lifetime;
            }
            else
            {
                // Budget elapsed with work still running: cancel, and let the last one to finish
                // dispose it via the retired path so disposal races a live HoweverUse.
                _lifetimeCts = null;
                _retiredCts = lifetime;
                toCancel = lifetime;
                toDispose = null;
            }
        }

        try
        {
            toCancel?.Cancel();
        }
        catch (AggregateException)
        {
            // A subscriber's cancellation callback threw; the session still ends.
        }

        toDispose?.Dispose();
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_gate)
        {
            _running = false;
            _state.Reset();
        }

        EndLifetime();
        StateChanged?.Invoke(this, _state);
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

            // Every toggle or target change is a FRESH translation-session boundary. The in-flight
            // requests from the previous session must not apply afterwards, even when the user
            // re-enables with the same target (toggle OFF → ON): the stale-result guards below check
            // TranslationEnabled + target, which the re-enable re-arms — only the session identity
            // (captured at request start, compared at apply) can tell a pre-OFF result apart.
            _translationSessionEpoch++;

            _state.SetTranslation(enabled, target);
            if (!enabled)
            {
                // The live translation line belongs to a session that has been switched off: drop it
                // so re-enabling never resurfaces stale translated text from before the toggle.
                _state.ClearTranslationActiveLine();

                // Toggling translation OFF must not leave target-language history mixed into the new
                // English-only source stream. RevertTranslatedContentToSource removes every
                // LineOrigin.Translation entry (language-agnostic: Tagalog, Japanese, French, etc.)
                // AND strips the translated text off Argos-translated SourceStt lines — WITHOUT that
                // second half, Argos's Japanese stays attached to its source line and mixes with the
                // new source captions (the reported en→ja then switch-to-English mix). The English
                // ground truth of those lines survives. The active translation line is handled by
                // ClearTranslationActiveLine above; this method is scoped to the committed history.
                _state.RevertTranslatedContentToSource();
            }
            else if (languageChanged)
            {
                // Switching target language mid-session (e.g. tl → ja) must drop the previous target's
                // output so the new session starts clean: Translation-origin lines (Gemini) are removed
                // and Argos-translated SourceStt lines revert to English (their shared ground truth).
                // The new target's history will populate as the engine emits.
                _state.RevertTranslatedContentToSource();
                _state.ClearTranslationActiveLine();
            }
        }

        if (enabled)
        {
            MaybeStartActiveLineTranslation();
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
    public void SetCaptionLineTranslation(bool enabled)
    {
        lock (_gate)
        {
            if (_useCaptionLineTranslation == enabled)
            {
                return;
            }

            _useCaptionLineTranslation = enabled;

            // Switching the translation MECHANISM (live-engine vs caption-line) is a session
            // boundary: results produced under the previous mechanism must not apply under this one.
            _translationSessionEpoch++;
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
            // Provider change = a fresh translation session: any result still in flight under the
            // previous provider must not apply under the new one.
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
            // clear the active line when audio stops/pauses and Whisper decodes silence.
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

        MaybeStartActiveLineTranslation();

        // Always fire StateChanged so the overlay rerenders: it may have translated history
        // from previous sentences to show, and the display model already hides any untranslated
        // active line when translation is enabled (returns active = null).
        // Only suppress ActiveLineChanged — that event directly surfaces the raw-English line
        // and is the one that would trigger English flash.
        if (!WillTranslateActiveLine(line))
        {
            ActiveLineChanged?.Invoke(this, line);
        }

        StateChanged?.Invoke(this, _state);
    }

    /// <summary>
    /// True when the active line will be translated live, so its source-language text must not be
    /// published to subscribers: showing it would flash the source language on the overlay until the
    /// translation completes (the live-translation language flip-flop). False when a live audio
    /// translation engine owns translation (the caption-line path is suppressed, so no source-line
    /// translation is pending). Must be called holding <see cref="_gate"/>.
    /// </summary>
    private bool WillTranslateActiveLine(CaptionLine line) =>
        _useCaptionLineTranslation
        && _translationEngine is not null
        && _state.TranslationEnabled
        && _state.TargetLanguage is not null
        && !string.Equals(_state.TargetLanguage, _options.SourceLanguage, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(line.Text);

    /// <summary>
    /// Starts a translation of the current active line when translation is enabled and no active-line
    /// translation is already in flight. A single in-flight slot is used because the translation backend
    /// serializes requests and cannot be cancelled per partial without being torn down; when the slot
    /// completes, it self-replenishes and translates the newest partial that arrived in the meantime.
    /// </summary>
    private void MaybeStartActiveLineTranslation()
    {
        if (!_running)
        {
            return;
        }

        CaptionLine? active;
        string? targetLanguage;
        long sessionEpoch;
        CancellationToken token;
        lock (_gate)
        {
            if (_activeLineTranslation is { IsCompleted: false })
            {
                return;
            }

            active = _state.ActiveLine;
            if (active is null
                || string.IsNullOrWhiteSpace(active.Text)
                || active.TranslationStatus != CaptionTranslationStatus.NotRequested
                || !_useCaptionLineTranslation
                || _translationEngine is null
                || !_state.TranslationEnabled
                || _state.TargetLanguage is null
                || string.Equals(_state.TargetLanguage, _options.SourceLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            targetLanguage = _state.TargetLanguage;
            sessionEpoch = _translationSessionEpoch;
            token = _lifetimeCts?.Token ?? CancellationToken.None;
            if (token == CancellationToken.None)
            {
                // The session ended between the check and here; the line stays untranslated.
                return;
            }
        }

        var task = RunActiveLineTranslationAsync(active, targetLanguage!, sessionEpoch, token);
        lock (_gate)
        {
            _activeLineTranslation = task;
            _inFlight.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_gate)
                {
                    _inFlight.Remove(completed);
                    if (ReferenceEquals(_activeLineTranslation, completed))
                    {
                        _activeLineTranslation = null;
                    }

                    if (_retiredCts is { } retired && _inFlight.Count == 0)
                    {
                        _retiredCts = null;
                        retired.Dispose();
                    }
                }

                MaybeStartActiveLineTranslation();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunActiveLineTranslationAsync(CaptionLine line, string targetLanguage, long sessionEpoch, CancellationToken cancellationToken)
    {
        DateTime startedAt = _utcNow();
        TranslationResult? result;
        try
        {
            UniversalCaptions.Core.Diagnostics.DiagnosticTracer.Record(5, "First translation request");
            result = await _translationEngine!.TranslateAsync(
                line.Text, line.SourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The session was stopped, reset, or disposed; the active line stays untranslated.
            return;
        }
        catch (Exception exc)
        {
            System.Diagnostics.Trace.WriteLine($"[UniversalCaptions] Active line translation failed: {exc}");
            // A failure is represented on the active line rather than breaking the caption pipeline.
            ApplyActiveLineTranslation(line, line.WithTranslationFailure(exc.Message, startedAt), targetLanguage, sessionEpoch);
            return;
        }

        ApplyActiveLineTranslation(line, line.WithTranslation(result!.Text, result.TargetLanguage, startedAt, _utcNow()), targetLanguage, sessionEpoch);
    }

    private void ApplyActiveLineTranslation(CaptionLine original, CaptionLine updated, string requestedTargetLanguage, long sessionEpoch)
    {
        bool applied;
        lock (_gate)
        {
            // The result belongs to a REQUESTED session: if translation was toggled off, the target
            // changed, or a fresh translation session began (toggle OFF → ON with the same target,
            // provider change) while this request was in flight (it is not cancelled to avoid tearing
            // down the Argos backend), the result is discarded — never applied — otherwise the
            // previous session's output would leak into the new session.
            // Compared against the REQUESTED target (the line's own TargetLanguage is null until a
            // translation attaches, so a failure result cannot carry it).
            if (sessionEpoch != _translationSessionEpoch
                || !_state.TranslationEnabled
                || !string.Equals(_state.TargetLanguage, requestedTargetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            applied = _state.ReplaceActiveLine(original, updated);
        }

        if (!applied)
        {
            // A newer partial replaced this line; the stale result is discarded.
            return;
        }

        CaptionLineUpdated?.Invoke(this, updated);
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
        // Whisper's sliding-window epoch resets produce three kinds of repeat emissions:
        //
        //  (A) Exact duplicate:   prev == new  → drop new.
        //  (B) Truncated repeat:  new is a leading subset of prev (Whisper re-emits a
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

        CaptionLine? pending = null;
        string? targetLanguage = null;
        long sessionEpoch = 0;
        CancellationToken token = CancellationToken.None;
        lock (_gate)
        {
            targetLanguage = _state.TargetLanguage;
            sessionEpoch = _translationSessionEpoch;
            if (_useCaptionLineTranslation
                && _translationEngine is not null
                && _state.TranslationEnabled
                && targetLanguage is not null
                && !string.Equals(targetLanguage, _options.SourceLanguage, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(line.Text))
            {
                // To avoid flashing English text in the overlay when a line is committed (which starts
                // translation from scratch on the final text), see if the active line had already
                // completed translation under the same sequence, and seed the committed line with it.
                string? tempTranslatedText = null;
                CaptionTranslationStatus tempStatus = CaptionTranslationStatus.Pending;
                DateTime? tempStartedAt = null;
                DateTime? tempCompletedAt = null;

                if (_state.ActiveLine is { } active
                    && active.Sequence == line.Sequence
                    && active.TranslationStatus == CaptionTranslationStatus.Completed)
                {
                    tempTranslatedText = active.TranslatedText;
                    tempStatus = CaptionTranslationStatus.Completed;
                    tempStartedAt = active.TranslationStartedAtUtc;
                    tempCompletedAt = active.TranslationCompletedAtUtc;
                }

                pending = new CaptionLine(
                    line.Text,
                    line.SourceLanguage,
                    line.Sequence,
                    line.CapturedAtUtc,
                    CaptionLineState.Final,
                    committedAtUtc: line.CommittedAtUtc,
                    targetLanguage: targetLanguage,
                    translatedText: tempTranslatedText,
                    translationStatus: tempStatus,
                    translationErrorMessage: null,
                    translationStartedAtUtc: tempStartedAt,
                    translationCompletedAtUtc: tempCompletedAt);

                token = _lifetimeCts?.Token ?? CancellationToken.None;
            }
        }

        if (pending is not null)
        {
            Commit(pending);
            StartTranslation(pending, targetLanguage!, sessionEpoch, token);
        }
        else
        {
            Commit(line);
        }
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task[] snapshot;
            lock (_gate)
            {
                snapshot = _inFlight.ToArray();
            }

            if (snapshot.Length == 0)
            {
                return;
            }

            await Task.WhenAll(snapshot).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _running = false;
        }

        EndLifetime();
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
        // the user). `SourceText` records the engine-provided source string when available, so a
        // consumer can recover the source↔translation relationship. The caption service never invokes
        // `ITranslationEngine` on translation-origin lines: translation was already performed by the
        // engine that produced this event, so translation status stays NotRequested.
        var line = new CaptionLine(
            cleanText,
            translation.TargetLanguage,
            translation.Sequence,
            translation.CapturedAtUtc,
            CaptionLineState.Active,
            targetLanguage: translation.TargetLanguage,
            translatedText: cleanText,
            translationStatus: CaptionTranslationStatus.NotRequested,
            origin: LineOrigin.Translation);

        // Preserve the original source text (when the engine provides one) on a side channel: the
        // CaptionLine shape does not currently carry SourceText, but the engine's TranslationTranscript
        // did — so for translation-origin lines we stash it on the line via the SourceLanguage field's
        // sibling. The simplest non-invasive approach is to record it via a derived caption line that
        // keeps the source text in TranslationErrorMessage... no — that is misleading. Instead, we
        // surface it through TranslationStartedAtUtc/CompletedAtUtc semantics: the line IS the
        // translation; SourceText is not surfaced to the overlay by this slice. (Future: extend
        // CaptionLine with SourceText if/when the overlay wants it.)

        lock (_gate)
        {
            _state.UpdateTranslationActiveLine(line);
        }

        ActiveLineChanged?.Invoke(this, line);
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
            origin: LineOrigin.Translation);

        CommitTranslation(line);
    }

    private void CommitTranslation(CaptionLine line)
    {
        lock (_gate)
        {
            _state.AddFinalLine(line);
            // AddFinalLine already clears the matching origin's active slot; no extra work needed.
        }

        CaptionLineCommitted?.Invoke(this, line);
        StateChanged?.Invoke(this, _state);
    }

    private void Commit(CaptionLine line)
    {
        lock (_gate)
        {
            _state.AddFinalLine(line);
            // AddFinalLine already clears the matching origin's active slot; no extra work needed.
        }

        CaptionLineCommitted?.Invoke(this, line);
        StateChanged?.Invoke(this, _state);
    }

    private void StartTranslation(CaptionLine line, string targetLanguage, long sessionEpoch, CancellationToken token)
    {
        if (token == CancellationToken.None)
        {
            // The session ended between the commit decision and here; the line stays pending.
            return;
        }

        var task = RunTranslationAsync(line, targetLanguage, sessionEpoch, token);
        lock (_gate)
        {
            _inFlight.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_gate)
                {
                    _inFlight.Remove(completed);
                    if (_retiredCts is { } retired && _inFlight.Count == 0)
                    {
                        _retiredCts = null;
                        retired.Dispose();
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunTranslationAsync(CaptionLine line, string targetLanguage, long sessionEpoch, CancellationToken cancellationToken)
    {
        DateTime startedAt = _utcNow();
        TranslationResult? result;
        try
        {
            UniversalCaptions.Core.Diagnostics.DiagnosticTracer.Record(5, "First translation request");
            result = await _translationEngine!.TranslateAsync(
                line.Text, line.SourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The session was stopped, reset, or disposed; the line is left pending.
            return;
        }
        catch (Exception exc)
        {
            Console.Error.WriteLine($"[UniversalCaptions] Committed line translation failed: {exc}");
            System.Diagnostics.Trace.WriteLine($"[UniversalCaptions] Committed line translation failed: {exc}");
            // Any translation failure is represented on the line rather than breaking the caption pipeline.
            ApplyTranslationUpdate(line, line.WithTranslationFailure(exc.Message, startedAt), sessionEpoch);
            return;
        }

        ApplyTranslationUpdate(line, line.WithTranslation(result!.Text, result.TargetLanguage, startedAt, _utcNow()), sessionEpoch);
    }

    private void ApplyTranslationUpdate(CaptionLine original, CaptionLine updated, long sessionEpoch)
    {
        bool applied;
        lock (_gate)
        {
            // Same stale-result guard as ApplyActiveLineTranslation: a committed-line translation that
            // started before translation was toggled off, the target changed, or a fresh translation
            // session began (toggle OFF → ON with the same target, provider change) must be discarded,
            // so an old-session result can never re-mix into the new session after the reset.
            if (sessionEpoch != _translationSessionEpoch
                || !_state.TranslationEnabled
                || !string.Equals(_state.TargetLanguage, updated.TargetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            applied = _state.ReplaceFinalLine(original, updated);
        }

        if (!applied)
        {
            return;
        }

        CaptionLineUpdated?.Invoke(this, updated);
        StateChanged?.Invoke(this, _state);
    }

    private void EndLifetime()
    {
        CancellationTokenSource? toCancel;
        CancellationTokenSource? toDispose;
        lock (_gate)
        {
            toCancel = _lifetimeCts;
            _lifetimeCts = null;
            if (toCancel is null)
            {
                return;
            }

            if (_inFlight.Count == 0)
            {
                toDispose = toCancel;
                _retiredCts = null;
            }
            else
            {
                // The last in-flight task disposes it once the set drains.
                toDispose = null;
                _retiredCts = toCancel;
            }
        }

        try
        {
            toCancel.Cancel();
        }
        catch (AggregateException)
        {
            // A subscriber's cancellation callback threw; the session still ends.
        }

        toDispose?.Dispose();
    }

    /// <summary>
    /// Strips any word-level suffix of <paramref name="prev"/> that appears as a prefix of
    /// <paramref name="next"/>, returning the remainder of <paramref name="next"/> that is genuinely
    /// new.  Words are compared case-insensitively so that capitalisation changes at the start of a
    /// new Whisper epoch do not defeat the deduplication.
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
    /// This detects case (B) — Whisper re-emitting a truncated version of a sentence it already
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
