using UniversalCaptions.Core.Audio;

namespace UniversalCaptions.Core.Capture;

/// <summary>
/// Captures system audio and raises <see cref="AudioAvailable"/> for each buffer of PCM audio.
/// Implementations must be hardware-boundary independent of the capture mechanism used underneath.
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Raised for each buffer of captured audio. The chunk must be consumed before the next event.</summary>
    event EventHandler<AudioChunk>? AudioAvailable;

    /// <summary>Raised when capture fails or the device disconnects.</summary>
    event EventHandler<AudioCaptureError>? CaptureFailed;

    /// <summary>The format of the captured audio.</summary>
    AudioFormat Format { get; }

    /// <summary>True while capture is running.</summary>
    bool IsCapturing { get; }

    /// <summary>
    /// Starts capture. Synchronous initialization failures are raised via <see cref="CaptureFailed"/>.
    /// </summary>
    void Start();

    /// <summary>Stops capture. Idempotent.</summary>
    void Stop();
}
