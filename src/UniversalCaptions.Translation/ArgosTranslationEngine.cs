using UniversalCaptions.Core.Translation;
using UniversalCaptions.Translation.Argos;

namespace UniversalCaptions.Translation;

/// <summary>
/// An <see cref="ITranslationEngine"/> backed by a local Argos Translate process. Text never
/// leaves the machine; the engine owns a child Python process running the bundled line-protocol
/// server.
/// </summary>
public sealed class ArgosTranslationEngine : ITranslationEngine, IDisposable
{
    private readonly IArgosProcess _process;
    private readonly object _startLock = new();
    private readonly object _warmLock = new();
    private Task? _startTask;
    private Task? _warmTask;
    private string? _warmedTarget;
    private bool _disposed;
    private long _nextSequence = 1;

    /// <summary>
    /// Creates an engine with the default options.
    /// </summary>
    public ArgosTranslationEngine()
        : this(new ArgosTranslationEngineOptions())
    {
    }

    /// <summary>
    /// Creates an engine with the given options.
    /// </summary>
    /// <param name="options">Controls the Python executable, server script path, and timeouts.</param>
    public ArgosTranslationEngine(ArgosTranslationEngineOptions options)
        : this(new LineProtocolArgosProcess(options), options)
    {
    }

    internal ArgosTranslationEngine(IArgosProcess process)
        : this(process, new ArgosTranslationEngineOptions())
    {
    }

    private ArgosTranslationEngine(IArgosProcess process, ArgosTranslationEngineOptions options)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _warmUpText = options?.WarmUpText ?? "The quick brown fox jumps over the lazy dog.";
    }

    private readonly string _warmUpText;

    /// <inheritdoc />
    public async Task<TranslationResult> TranslateAsync(
        string text,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new TranslationException(TranslationErrorKind.EmptyInput, "The text to translate must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new TranslationException(TranslationErrorKind.UnsupportedLanguage, "A target language code is required.");
        }

        if (sourceLanguage is not null &&
            string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            throw new TranslationException(
                TranslationErrorKind.SourceEqualsTarget,
                $"Source and target language must differ (both were '{sourceLanguage}').");
        }

        var sequence = Interlocked.Increment(ref _nextSequence);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var normalizedTarget = NormalizeLanguageCode(targetLanguage);
        // A real caption arriving while a pre-warm for the same target is still in flight must wait
        // for that warm-up (its throwaway translate loads the lazy model into the process) instead of
        // racing it through the request gate and paying the cold model-load inline on the first
        // caption. Warm-up failures are swallowed by RunWarmUpAsync; a faulting warm task falls back
        // to the normal lazy path below.
        await AwaitInFlightWarmupAsync(normalizedTarget).ConfigureAwait(false);

        var startedUtc = DateTime.UtcNow;
        ArgosResponse response;
        try
        {
            response = await _process.TranslateAsync(
                new ArgosRequest(sequence, text, NormalizeLanguageCode(sourceLanguage), normalizedTarget ?? targetLanguage),
                cancellationToken);
        }
        catch (TranslationProcessException exc)
        {
            ThrowOrResetError(exc.Kind);
            throw new TranslationException(exc.Kind, exc.Message, exc);
        }

        var completedUtc = DateTime.UtcNow;
        return new TranslationResult(
            response.Text ?? string.Empty,
            response.DetectedSource ?? NormalizeLanguageCode(sourceLanguage) ?? "auto",
            NormalizeLanguageCode(targetLanguage) ?? targetLanguage,
            sequence,
            startedUtc,
            completedUtc,
            response.DetectedSource,
            response.UsedPivot,
            response.PivotLanguage);
    }

    /// <summary>
    /// Starts the Argos process and loads the model in the background, then performs one throwaway
    /// warm-up translation so the first real caption reuses the warmed process/model instead of
    /// paying the cold-start cost inline. Runs off the caller thread and never surfaces or records a
    /// caption. Runs off the caller thread and never surfaces or records a caption. Idempotent while
    /// a warm-up is running or already completed *for the same target language*; concurrent callers
    /// (pre-warm, real translations) all await the same <see cref="_warmTask"/> and the same shared
    /// <see cref="_startTask"/>, so no duplicate process/initialization can start. Changing the
    /// target language triggers a fresh warm-up so the first caption in the new language is not cold.
    /// Failures are logged and swallowed; the normal lazy <see cref="TranslateAsync"/> path
    /// remains the safe fallback.
    /// </summary>
    public Task TriggerPreWarmAsync(
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_warmLock)
        {
            var normalizedTarget = NormalizeLanguageCode(targetLanguage);
            // Reuse an already-completed warm-up only if it targets the same language; a changed
            // target needs its own warm-up so the first caption in that language is not cold.
            if (_warmTask is not null && _warmedTarget == normalizedTarget)
            {
                return _warmTask;
            }

            _warmedTarget = normalizedTarget;
            _warmTask = RunWarmUpAsync(sourceLanguage, targetLanguage, cancellationToken);
            return _warmTask;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process.Dispose();
    }

    /// <summary>
    /// Returns the single shared process-start task. Every caller (pre-warm or real translation)
    /// awaits the same task so at most one process/initialization is ever started. If that task
    /// faults, it is cleared so a later call retries. <see cref="LineProtocolArgosProcess.StartAsync"/>
    /// itself guards against a second process, but sharing the task here also means a real request
    /// arriving mid-warm-up waits for warm-up rather than racing it.
    /// </summary>
    private Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        lock (_startLock)
        {
            if (_startTask is not null)
            {
                // A previous start attempt may have faulted (e.g. a pre-warm that hit a process
                // error and was swallowed). Clear the faulted task so the caller retries rather
                // than being handed a task that can only fault again.
                if (_startTask.IsCompleted && _startTask.Status != TaskStatus.RanToCompletion)
                {
                    _startTask = null;
                    _startTask = StartCoreAsync(cancellationToken);
                }

                return _startTask;
            }

            _startTask = StartCoreAsync(cancellationToken);
            return _startTask;
        }
    }

    /// <summary>
    /// When a pre-warm for the same target language is still in progress, awaits it so the warm-up's
    /// throwaway translation (which loads the lazy model into the process) completes before the real
    /// caption issues its own request. Without this, a real request arriving mid-warm-up races the
    /// warm-up through the single request gate and can pay the cold model-load inline on the first
    /// caption. Awaiting is safe: the warm task runs to completion (failures are swallowed), and a
    /// warm-up that faults from cancellation falls back to the lazy path.
    /// </summary>
    private async Task AwaitInFlightWarmupAsync(string? normalizedTarget)
    {
        if (normalizedTarget is null)
        {
            return;
        }

        Task? warmTask;
        lock (_warmLock)
        {
            warmTask = !string.Equals(_warmedTarget, normalizedTarget, StringComparison.OrdinalIgnoreCase) ? null : _warmTask;
        }

        if (warmTask is null)
        {
            return;
        }

        try
        {
            await warmTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancelled warm-up is a no-op; the lazy path below remains the fallback.
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _process.StartAsync(cancellationToken).ConfigureAwait(false);
            lock (_startLock)
            {
                _startTask = Task.CompletedTask;
            }
        }
        catch (TranslationProcessException exc)
        {
            // Re-throw as the domain kind and reset the task so a later call can retry.
            lock (_startLock)
            {
                _startTask = null;
            }

            throw new TranslationException(exc.Kind, exc.Message, exc);
        }
    }

    private async Task<TranslationResult> RunWarmUpAsync(
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var startedUtc = DateTime.UtcNow;
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

            long sequence = Interlocked.Increment(ref _nextSequence);
            ArgosResponse response = await _process.TranslateAsync(
                new ArgosRequest(
                    sequence,
                    _warmUpText,
                    NormalizeLanguageCode(sourceLanguage),
                    NormalizeLanguageCode(targetLanguage) ?? targetLanguage),
                cancellationToken);

            var completedUtc = DateTime.UtcNow;
            Console.Error.WriteLine(
                $"[ARGOS-DIAG] pre-warm ready in {(completedUtc - startedUtc).TotalSeconds:F3}s; warm text len={response.Text?.Length ?? 0}");
            return new TranslationResult(
                response.Text ?? string.Empty,
                response.DetectedSource ?? NormalizeLanguageCode(sourceLanguage) ?? "en",
                NormalizeLanguageCode(targetLanguage) ?? targetLanguage,
                sequence,
                startedUtc,
                completedUtc,
                response.DetectedSource,
                response.UsedPivot,
                response.PivotLanguage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("[ARGOS-DIAG] pre-warm cancelled; lazy start remains the fallback.");
            throw;
        }
        catch (Exception exc)
        {
            // A fatal process error (timeout/unavailable/unknown) kills the underlying process; if we
            // swallow it without resetting the shared start task, the next real caption would be
            // handed a "completed" start while the process is dead and be lost. Reset the start task
            // here so a real translation re-creates the process instead of failing once silently.
            if (exc is TranslationProcessException processExc)
            {
                // A fatal process error kills the underlying process; reset the shared start task so
                // the next real translation re-creates it rather than being handed a dead "completed"
                // start. (Do not clear _warmTask here: async warm-up runs to completion before the
                // assignment in Trigger, so clearing it would be lost; a same-target re-warm is not
                // needed because the lazy path is the fallback in that case.)
                ThrowOrResetError(processExc.Kind);
            }

            Console.Error.WriteLine(
                $"[UniversalCaptions] Argos pre-warm failed (lazy start remains the fallback): {exc.Message}");
            return new TranslationResult(
                string.Empty,
                string.Empty,
                targetLanguage,
                0,
                startedUtc,
                DateTime.UtcNow,
                null,
                false,
                null);
        }
    }

    /// <summary>
    /// Resets the shared start task when a fatal process error occurs so a later real translation
    /// request can restart the process.
    /// </summary>
    private void ThrowOrResetError(TranslationErrorKind kind)
    {
        if (IsFatalProcessError(kind))
        {
            lock (_startLock)
            {
                _startTask = null;
            }
        }
    }

    private static bool IsFatalProcessError(TranslationErrorKind kind) =>
        kind is TranslationErrorKind.EngineUnavailable
            or TranslationErrorKind.Timeout
            or TranslationErrorKind.Unknown;

    private static string? NormalizeLanguageCode(string? code) => code?.Trim().ToLowerInvariant();
}
