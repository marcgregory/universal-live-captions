namespace UniversalCaptions.Speech;

/// <summary>Identifies a faster-whisper worker failure.</summary>
public enum FasterWhisperErrorKind
{
    /// <summary>The worker process could not be started (missing Python, venv, or model).</summary>
    EngineUnavailable,
    /// <summary>The worker did not respond within the configured timeout.</summary>
    Timeout,
    /// <summary>The worker closed the protocol stream or returned an invalid response.</summary>
    Protocol,
    /// <summary>The worker reported a decode/engine error.</summary>
    EngineFailed,
}

/// <summary>An exception thrown when the faster-whisper worker process fails.</summary>
public sealed class FasterWhisperProcessException : Exception
{
    /// <summary>The kind of failure.</summary>
    public FasterWhisperErrorKind Kind { get; }

    /// <summary>Creates a new exception.</summary>
    public FasterWhisperProcessException(FasterWhisperErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
}
