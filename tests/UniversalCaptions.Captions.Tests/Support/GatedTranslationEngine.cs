using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.Captions.Tests.Support;

/// <summary>
/// A translation engine whose requests are completed manually by the test, so timing is fully
/// deterministic. Each request gets its own completion source, so tests can settle requests out of
/// order. Honours cancellation by completing as cancelled.
/// </summary>
internal sealed class GatedTranslationEngine : ITranslationEngine
{
    private readonly List<TaskCompletionSource<TranslationResult>> _completions = [];

    /// <summary>All translation requests, in order.</summary>
    public List<(string Text, string? Source, string Target)> Requests { get; } = [];

    /// <summary>The number of requests started so far.</summary>
    public int RequestCount => _completions.Count;

    /// <inheritdoc />
    public Task<TranslationResult> TranslateAsync(
        string text, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        Requests.Add((text, sourceLanguage, targetLanguage));
        var completion = NewCompletion();
        _completions.Add(completion);
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        }

        return completion.Task;
    }

    /// <summary>Completes the <paramref name="index"/>-th request with a successful result.</summary>
    public void Complete(int index, string translatedText, string targetLanguage) =>
        _completions[index].TrySetResult(new TranslationResult(translatedText, "en", targetLanguage, 0, DateTime.UtcNow, DateTime.UtcNow));

    /// <summary>Completes the most recently started request with a successful result.</summary>
    public void CompleteLatest(string translatedText, string targetLanguage) =>
        Complete(_completions.Count - 1, translatedText, targetLanguage);

    private static TaskCompletionSource<TranslationResult> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
