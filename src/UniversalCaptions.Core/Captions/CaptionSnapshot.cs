namespace UniversalCaptions.Core.Captions;

/// <summary>
/// An immutable, internally consistent copy of <see cref="CaptionState"/> taken by
/// <see cref="ICaptionService.GetSnapshot"/> under the service's serialization gate. Caption events
/// are raised outside that gate, so a consumer that read <see cref="ICaptionService.State"/> directly
/// could observe a history mid-mutation; reading a snapshot never races with a commit or a reset.
/// </summary>
/// <remarks>
/// The snapshot owns its <see cref="History"/> array: it is not a view of the live state, and later
/// commits do not change an already-taken snapshot. Lines are immutable, so no defensive copy is
/// needed for the line objects themselves.
/// </remarks>
public sealed record CaptionSnapshot(
    CaptionLine? ActiveLine,
    IReadOnlyList<CaptionLine> History,
    bool IsSessionActive,
    bool TranslationEnabled,
    string? TargetLanguage);
