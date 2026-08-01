namespace UniversalCaptions.App.Pipeline;

/// <summary>
/// The high-level state of the caption pipeline as surfaced to the control window.
/// </summary>
public enum PipelineStatusKind
{
    /// <summary>Not capturing (either never started or stopped).</summary>
    Stopped,

    /// <summary>System audio is being captured and captioned.</summary>
    Capturing,

    /// <summary>A capture, recognition, or setup failure occurred.</summary>
    Error,
}

/// <summary>
/// A pipeline state change: a <see cref="Kind"/> plus a user-readable message.
/// </summary>
public sealed record PipelineStatus(PipelineStatusKind Kind, string Message);
