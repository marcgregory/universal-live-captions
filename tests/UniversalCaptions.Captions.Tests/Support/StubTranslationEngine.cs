using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.Captions.Tests.Support;

/// <summary>
/// A deterministic <see cref="ITranslationEngine"/> used to test <see cref="CaptionService"/>
/// without a real translation backend.
/// </summary>
internal sealed class StubTranslationEngine : ITranslationEngine
{
    private readonly Func<string, string?, string, Task<TranslationResult>> _handler;

    private StubTranslationEngine(Func<string, string?, string, Task<TranslationResult>> handler)
    {
        _handler = handler;
    }

    /// <summary>All translation requests, in order.</summary>
    public List<(string Text, string? Source, string Target)> Requests { get; } = [];

    /// <summary>Creates an engine that appends an exclamation mark to each translation.</summary>
    public static StubTranslationEngine Success() =>
        new((text, source, target) =>
            Task.FromResult(new TranslationResult(text + "!", source ?? "en", target, 0, DateTime.UtcNow, DateTime.UtcNow)));

    /// <summary>Creates an engine that throws a translation exception for every request.</summary>
    public static StubTranslationEngine Failure(TranslationErrorKind kind, string message) =>
        new((_, _, _) => Task.FromException<TranslationResult>(new TranslationException(kind, message)));

    /// <summary>Creates an engine that throws an unexpected exception for every request.</summary>
    public static StubTranslationEngine Unexpected(Exception exception) =>
        new((_, _, _) => throw exception);

    /// <inheritdoc />
    public Task<TranslationResult> TranslateAsync(
        string text, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        Requests.Add((text, sourceLanguage, targetLanguage));
        return _handler(text, sourceLanguage, targetLanguage);
    }
}
