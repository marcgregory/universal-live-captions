namespace UniversalCaptions.Core.Captions;

/// <summary>
/// Options that control how <see cref="ICaptionService"/> implementations build captions.
/// </summary>
public sealed class CaptionServiceOptions
{
    /// <summary>
    /// Creates caption service options.
    /// </summary>
    /// <param name="sourceLanguage">The ISO 639-1 language code of the source-language text.</param>
    /// <param name="targetLanguage">The ISO 639-1 language code translation targets when enabled.</param>
    /// <param name="historyCapacity">The maximum number of committed lines retained. Zero retains none.</param>
    /// <exception cref="ArgumentException"><paramref name="sourceLanguage"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="historyCapacity"/> is negative.</exception>
    public CaptionServiceOptions(string sourceLanguage, string? targetLanguage = null, int historyCapacity = 50)
    {
        if (string.IsNullOrWhiteSpace(sourceLanguage))
        {
            throw new ArgumentException("SourceLanguage must be provided.", nameof(sourceLanguage));
        }

        if (historyCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(historyCapacity), historyCapacity, "HistoryCapacity must be zero or greater.");
        }

        SourceLanguage = Normalize(sourceLanguage);
        TargetLanguage = targetLanguage is null ? null : Normalize(targetLanguage);
        HistoryCapacity = historyCapacity;
    }

    /// <summary>The ISO 639-1 language code of the source-language text.</summary>
    public string SourceLanguage { get; }

    /// <summary>The ISO 639-1 language code translation targets when enabled.</summary>
    public string? TargetLanguage { get; }

    /// <summary>The maximum number of committed lines retained.</summary>
    public int HistoryCapacity { get; }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
