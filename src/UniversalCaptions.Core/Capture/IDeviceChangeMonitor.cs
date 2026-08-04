namespace UniversalCaptions.Core.Capture;

/// <summary>
/// Reports Windows render-device endpoint changes (default-device change, state change, add, remove)
/// so capture can recover automatically when the audio output switches or disappears (TD-002).
/// Implementations on Windows subscribe to <c>RegisterEndpointNotificationCallback</c>.
/// </summary>
public interface IDeviceChangeMonitor : IDisposable
{
    /// <summary>Raised when a render endpoint changes. The notification must be treated as transient.</summary>
    event EventHandler<DeviceChangeNotification>? DeviceChanged;

    /// <summary>
    /// Starts reporting endpoint changes. Idempotent. Synchronous registration failures are raised
    /// from this method.
    /// </summary>
    void Start();

    /// <summary>Stops reporting endpoint changes. Idempotent.</summary>
    void Stop();
}
