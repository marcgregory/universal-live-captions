using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.Translation.Argos;

/// <summary>
/// A translation request to be sent to the Argos process.
/// </summary>
internal sealed record ArgosRequest(long Id, string Text, string? Source, string Target);

/// <summary>
/// A response from the Argos process.
/// </summary>
internal sealed record ArgosResponse(
    bool Ok,
    string? Text,
    string? DetectedSource,
    bool UsedPivot,
    string? PivotLanguage,
    IReadOnlyList<string>? Models,
    TranslationErrorKind? ErrorKind,
    string? ErrorMessage);
