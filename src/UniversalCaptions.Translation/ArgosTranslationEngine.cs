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
    private bool _started;
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
        : this(new LineProtocolArgosProcess(options))
    {
    }

    internal ArgosTranslationEngine(IArgosProcess process)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
    }

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
        await EnsureStartedAsync(cancellationToken);

        var normalizedTarget = NormalizeLanguageCode(targetLanguage);
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
            if (IsFatalProcessError(exc.Kind))
            {
                lock (_startLock)
                {
                    _started = false;
                }
            }

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

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        lock (_startLock)
        {
            if (_started)
            {
                return;
            }
        }

        try
        {
            await _process.StartAsync(cancellationToken);
        }
        catch (TranslationProcessException exc)
        {
            throw new TranslationException(exc.Kind, exc.Message, exc);
        }

        lock (_startLock)
        {
            _started = true;
        }
    }

    private static bool IsFatalProcessError(TranslationErrorKind kind) =>
        kind is TranslationErrorKind.EngineUnavailable
            or TranslationErrorKind.Timeout
            or TranslationErrorKind.Unknown;

    private static string? NormalizeLanguageCode(string? code) => code?.Trim().ToLowerInvariant();
}
