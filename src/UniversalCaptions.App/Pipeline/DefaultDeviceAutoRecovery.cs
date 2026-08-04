using UniversalCaptions.Core.Capture;

namespace UniversalCaptions.App.Pipeline;

/// <summary>
/// Automatic recovery for a default-device capture session (TD-002). Subscribes to an
/// <see cref="IDeviceChangeMonitor"/> and, while the live session is capturing the system default
/// render device, restarts that session when the default device changes, the current endpoint is
/// unplugged/disappears, or any render device is removed. When the user is capturing an explicitly
/// chosen (non-default) device, no notification triggers a restart — their explicit choice is
/// preserved. Each notification window coalesces into a single restart so a burst of endpoint events
/// cannot pile up overlapping restarts.
///
/// This component is the recovery coordinator: the <see cref="CaptionPipeline"/> supplies the
/// <c>isOnDefaultDevice</c> and <c>restartAsync</c> delegates and owns the recovery session logic,
/// so the coordination contract is exercised deterministically with fakes.
/// </summary>
public sealed class DefaultDeviceAutoRecovery : IDisposable
{
    private readonly IDeviceChangeMonitor _monitor;
    private readonly Func<bool> _isOnDefaultDevice;
    private readonly Func<string?, Task> _restartAsync;
    private readonly object _gate = new();
    private bool _restartPending;
    private bool _disposed;

    /// <summary>
    /// Creates a recovery coordinator.
    /// </summary>
    /// <param name="monitor">The endpoint-change source.</param>
    /// <param name="isOnDefaultDevice">True while the live session is capturing the system default device.</param>
    /// <param name="restartAsync">Recreates and starts a capture session for a device (null = the system default). The returned task completes when the new session is live.</param>
    public DefaultDeviceAutoRecovery(
        IDeviceChangeMonitor monitor,
        Func<bool> isOnDefaultDevice,
        Func<string?, Task> restartAsync)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _isOnDefaultDevice = isOnDefaultDevice ?? throw new ArgumentNullException(nameof(isOnDefaultDevice));
        _restartAsync = restartAsync ?? throw new ArgumentNullException(nameof(restartAsync));
        _monitor.DeviceChanged += OnDeviceChanged;
    }

    /// <summary>The number of restarts this coordinator has launched since construction.</summary>
    public int RestartCount { get; private set; }

    private void OnDeviceChanged(object? sender, DeviceChangeNotification notification)
    {
        if (_disposed || !_isOnDefaultDevice() || !ShouldRestart(notification))
        {
            return;
        }

        bool launch;
        lock (_gate)
        {
            if (_disposed || _restartPending)
            {
                return;
            }

            _restartPending = true;
            launch = true;
        }

        if (launch)
        {
            _ = FireRestartAsync();
        }
    }

    private static bool ShouldRestart(DeviceChangeNotification notification) =>
        notification.Kind is DeviceChangeKind.DefaultDeviceChanged
        || (notification.Kind is DeviceChangeKind.StateChanged
            && notification.State is DeviceState.NotPresent or DeviceState.Unplugged)
        || notification.Kind is DeviceChangeKind.Removed;

    private async Task FireRestartAsync()
    {
        try
        {
            await _restartAsync(null).ConfigureAwait(false);
            RestartCount++;
        }
        catch
        {
            // A restart failure surfaces through the pipeline's own status/error path; this coordinator
            // only clears the pending flag so a later notification can retry.
        }
        finally
        {
            lock (_gate)
            {
                _restartPending = false;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _monitor.DeviceChanged -= OnDeviceChanged;
    }
}
