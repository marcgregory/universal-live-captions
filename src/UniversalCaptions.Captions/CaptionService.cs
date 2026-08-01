using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.Captions;

/// <summary>
/// Turns speech transcripts into <see cref="CaptionState"/>: partials replace the active line and
/// finals commit lines to history. When translation is enabled and an <see cref="ITranslationEngine"/>
/// is available, committed lines are translated in the background and the result or failure is applied
/// to the line without ever replacing its source text.
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
/// A translation failure is represented on the line with <see cref="CaptionTranslationStatus.Failed"/>
/// and the source text remains intact in <see cref="CaptionLine.Text"/>. Cancellation (session stopped,
/// reset, or disposed) leaves the line in <see cref="CaptionTranslationStatus.Pending"/>. A stale
/// translation result is never applied: it is matched to the exact committed line instance it was
/// started from, so a re-committed line under the same sequence cannot be overwritten.
/// </para>
/// </remarks>
public sealed class CaptionService : ICaptionService
{
    private readonly CaptionServiceOptions _options;
    private readonly ITranslationEngine? _translationEngine;
    private readonly object _gate = new();
    private readonly HashSet<Task> _inFlight = new();
    private readonly CaptionState _state;
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _retiredCts;
    private volatile bool _running;

    /// <summary>
    /// Creates a caption service that builds captions in <see cref="CaptionServiceOptions.SourceLanguage"/>
    /// and, when translation is enabled, translates committed lines via <paramref name="translationEngine"/>.
    /// </summary>
    /// <param name="options">The caption service options.</param>
    /// <param name="translationEngine">The optional translation engine used when translation is enabled.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public CaptionService(CaptionServiceOptions options, ITranslationEngine? translationEngine = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _translationEngine = translationEngine;
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

        var line = new CaptionLine(
            transcript.Text,
            _options.SourceLanguage,
            transcript.Sequence,
            transcript.CapturedAtUtc,
            CaptionLineState.Active);

        lock (_gate)
        {
            _state.UpdateActiveLine(line);
        }

        ActiveLineChanged?.Invoke(this, line);
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

        var line = new CaptionLine(
            transcript.Text,
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
                pending = line.WithPendingTranslation(targetLanguage);
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
        TranslationResult? result;
        try
        {
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
            // Any translation failure is represented on the line rather than breaking the caption pipeline.
            ApplyTranslationUpdate(line, line.WithTranslationFailure(exc.Message));
            return;
        }

        ApplyTranslationUpdate(line, line.WithTranslation(result!.Text, result.TargetLanguage));
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
}
