namespace UniversalCaptions.Core.Audio;

/// <summary>
/// Categorizes audio capture failures so they can be surfaced to the user.
/// </summary>
public enum AudioCaptureErrorKind
{
    /// <summary>The system has no active audio output device.</summary>
    NoOutputDevice,

    /// <summary>The audio endpoint exists but is unavailable or in use.</summary>
    DeviceUnavailable,

    /// <summary>WASAPI loopback could not be initialized.</summary>
    InitializationFailed,

    /// <summary>The audio device disconnected during capture.</summary>
    DeviceDisconnected,

    /// <summary>An unexpected failure occurred.</summary>
    Unknown,
}

/// <summary>
/// A user-readable capture failure. Raised via <see cref="IAudioCapture.CaptureFailed"/>.
/// </summary>
public sealed class AudioCaptureError
{
    /// <summary>
    /// Creates a new capture error.
    /// </summary>
    /// <param name="kind">The failure category.</param>
    /// <param name="message">A message suitable for display to the user.</param>
    /// <param name="exception">The underlying exception, when available.</param>
    public AudioCaptureError(AudioCaptureErrorKind kind, string message, Exception? exception = null)
    {
        Kind = kind;
        Message = message;
        Exception = exception;
    }

    /// <summary>The failure category.</summary>
    public AudioCaptureErrorKind Kind { get; }

    /// <summary>A message suitable for display to the user.</summary>
    public string Message { get; }

    /// <summary>The underlying exception, when available.</summary>
    public Exception? Exception { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind}: {Message}";
}

/// <summary>
/// Thrown when audio capture cannot be created (for example, no output device exists).
/// </summary>
public sealed class AudioCaptureException : Exception
{
    /// <summary>
    /// Creates a new audio capture exception.
    /// </summary>
    /// <param name="kind">The failure category.</param>
    /// <param name="message">A message suitable for display to the user.</param>
    /// <param name="innerException">The underlying exception, when available.</param>
    public AudioCaptureException(AudioCaptureErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>The failure category.</summary>
    public AudioCaptureErrorKind Kind { get; }
}
