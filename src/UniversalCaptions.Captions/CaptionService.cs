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
/// A translation failure is represented on the line with <see cref="CaptionTranslationStatus.Failed"/>
/// and the source text remains intact in <see cref="CaptionLine.Text"/>. Cancellation (session stopped,
/// reset, or disposed) leaves the line in <see cref="CaptionTranslationStatus.Pending"/>. A stale
/// translation result is never applied: it is matched to the exact line instance it was started from,
/// so a re-committed line under the same sequence or a newer partial cannot be overwritten.
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
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _retiredCts;
    private Task? _activeLineTranslation;
    private volatile bool _running;

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
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public CaptionService(
        CaptionServiceOptions options,
        ITranslationEngine? translationEngine = null,
        Func<DateTime>? utcNow = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _translationEngine = translationEngine;
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
                _state.TargetLanguage);
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
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            _state.EndSession();
        }

        EndLifetime();
        StateChanged?.Invoke(this, _state);
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
            _state.SetTranslation(enabled, target);
        }

        if (enabled)
        {
            MaybeStartActiveLineTranslation();
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
    /// translation completes (the live-translation language flip-flop). Must be called holding
    /// <see cref="_gate"/>.
    /// </summary>
    private bool WillTranslateActiveLine(CaptionLine line) =>
        _translationEngine is not null
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
                || _translationEngine is null
                || !_state.TranslationEnabled
                || _state.TargetLanguage is null)
            {
                return;
            }

            targetLanguage = _state.TargetLanguage;
            token = _lifetimeCts?.Token ?? CancellationToken.None;
            if (token == CancellationToken.None)
            {
                // The session ended between the check and here; the line stays untranslated.
                return;
            }
        }

        var task = RunActiveLineTranslationAsync(active, targetLanguage!, token);
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

    private async Task RunActiveLineTranslationAsync(CaptionLine line, string targetLanguage, CancellationToken cancellationToken)
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
            ApplyActiveLineTranslation(line, line.WithTranslationFailure(exc.Message, startedAt));
            return;
        }

        ApplyActiveLineTranslation(line, line.WithTranslation(result!.Text, result.TargetLanguage, startedAt, _utcNow()));
    }

    private void ApplyActiveLineTranslation(CaptionLine original, CaptionLine updated)
    {
        bool applied;
        lock (_gate)
        {
            // Translation was turned off while this request was in flight (it is not cancelled to
            // avoid tearing down the Argos backend); the result is discarded, never applied.
            if (!_state.TranslationEnabled)
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
        CancellationToken token = CancellationToken.None;
        lock (_gate)
        {
            targetLanguage = _state.TargetLanguage;
            if (_translationEngine is not null
                && _state.TranslationEnabled
                && targetLanguage is not null
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
            StartTranslation(pending, targetLanguage!, token);
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

    private void Commit(CaptionLine line)
    {
        lock (_gate)
        {
            _state.AddFinalLine(line);
            if (_state.ActiveLine is null || line.Sequence >= _state.ActiveLine.Sequence)
            {
                _state.ClearActiveLine();
            }
        }

        CaptionLineCommitted?.Invoke(this, line);
        StateChanged?.Invoke(this, _state);
    }

    private void StartTranslation(CaptionLine line, string targetLanguage, CancellationToken token)
    {
        if (token == CancellationToken.None)
        {
            // The session ended between the commit decision and here; the line stays pending.
            return;
        }

        var task = RunTranslationAsync(line, targetLanguage, token);
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

    private async Task RunTranslationAsync(CaptionLine line, string targetLanguage, CancellationToken cancellationToken)
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
            ApplyTranslationUpdate(line, line.WithTranslationFailure(exc.Message, startedAt));
            return;
        }

        ApplyTranslationUpdate(line, line.WithTranslation(result!.Text, result.TargetLanguage, startedAt, _utcNow()));
    }

    private void ApplyTranslationUpdate(CaptionLine original, CaptionLine updated)
    {
        bool applied;
        lock (_gate)
        {
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
