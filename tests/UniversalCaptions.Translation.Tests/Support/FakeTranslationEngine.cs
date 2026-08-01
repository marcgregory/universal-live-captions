using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.Translation.Tests.Support;

/// <summary>
/// A deterministic <see cref="ITranslationEngine"/> used to test translation behavior without a
/// real engine. Scripts translations via a keyed map, records every call, and can inject latency,
/// failures, and pivoting metadata.
/// </summary>
public sealed class FakeTranslationEngine : ITranslationEngine
{
    private readonly Dictionary<(string Source, string Target, string Text), string> _map = [];
    private readonly List<TranslationCall> _calls = [];
    private readonly List<TranslationException> _failures = [];

    /// <summary>True once <see cref="TranslateAsync"/> has been called.</summary>
    public bool WasCalled => _calls.Count > 0;

    /// <summary>Number of times <see cref="TranslateAsync"/> has been called.</summary>
    public int CallCount => _calls.Count;

    /// <summary>Whether each call should report a pivot.</summary>
    public bool UsedPivot { get; set; }

    /// <summary>The pivot language to report, when <see cref="UsedPivot"/> is set.</summary>
    public string? PivotLanguage { get; set; }

    /// <summary>Artificial delay added to each translation.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>The resolved source language to report. When null, the requested source is used.</summary>
    public string? DetectedSourceLanguage { get; set; }

    /// <summary>Sequence number of the next translation to produce.</summary>
    public long NextSequence { get; set; } = 1;

    /// <summary>A snapshot of every call made, in order.</summary>
    public IReadOnlyList<TranslationCall> Calls => _calls;

    /// <summary>Registers a translation output for a given input.</summary>
    public void Register(string source, string target, string text, string translatedText)
    {
        _map[(source, target, text)] = translatedText;
    }

    /// <summary>Makes the next call throw the given exception.</summary>
    public void FailNext(TranslationException exception) => _failures.Add(exception);

    /// <summary>Makes the next call throw a failure of the given kind.</summary>
    public void FailNext(TranslationErrorKind kind, string message) =>
        FailNext(new TranslationException(kind, message));

    /// <summary>Clears the recorded calls.</summary>
    public void Reset() => _calls.Clear();

    /// <inheritdoc />
    public async Task<TranslationResult> TranslateAsync(
        string text,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
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

        if (_failures.Count > 0)
        {
            var failure = _failures[0];
            _failures.RemoveAt(0);
            throw failure;
        }

        var call = new TranslationCall(sourceLanguage, targetLanguage, text, NextSequence++);
        _calls.Add(call);

        var resolvedSource = DetectedSourceLanguage ?? sourceLanguage ?? "auto";
        var translated = _map.TryGetValue((resolvedSource, targetLanguage, text), out var mapped)
            ? mapped
            : $"[{sourceLanguage ?? "auto"}->{targetLanguage}] {text}";

        if (Latency > TimeSpan.Zero)
        {
            await Task.Delay(Latency, cancellationToken);
        }

        var startedUtc = DateTime.UtcNow;
        var completedUtc = startedUtc + Latency;
        return new TranslationResult(
            translated,
            resolvedSource,
            targetLanguage,
            call.Sequence,
            startedUtc,
            completedUtc,
            DetectedSourceLanguage,
            UsedPivot,
            PivotLanguage);
    }

    /// <summary>A recorded translation invocation.</summary>
    public sealed record TranslationCall(string? Source, string Target, string Text, long Sequence);
}
