using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using UniversalCaptions.Core.Capture;
using CDeviceState = UniversalCaptions.Core.Capture.DeviceState;
using NDeviceState = NAudio.CoreAudioApi.DeviceState;

namespace UniversalCaptions.Audio.Capture;

/// <summary>
/// Reports render-device endpoint changes to the Core <see cref="IDeviceChangeMonitor"/> contract by
/// registering as an <see cref="IMMNotificationClient"/> with a live <see cref="MMDeviceEnumerator"/>
/// (<c>RegisterEndpointNotificationCallback</c>). Only render (output) endpoint changes are
/// surfaced, matching the app's loopback-capture scope. The enumerator is created lazily in
/// <see cref="Start"/>, so constructing the notifier touches no COM — the notification path can be
/// driven deterministically in tests by invoking this class's <see cref="IMMNotificationClient"/>
/// methods directly.
/// </summary>
public sealed class WasapiDeviceChangeNotifier : IDeviceChangeMonitor, IMMNotificationClient
{
    private MMDeviceEnumerator? _enumerator;
    private bool _started;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<DeviceChangeNotification>? DeviceChanged;

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        var enumerator = new MMDeviceEnumerator();
        try
        {
            enumerator.RegisterEndpointNotificationCallback(this);
        }
        catch
        {
            enumerator.Dispose();
            throw;
        }

        _enumerator = enumerator;
        _started = true;
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        try
        {
            _enumerator?.UnregisterEndpointNotificationCallback(this);
        }
        finally
        {
            _enumerator?.Dispose();
            _enumerator = null;
            _started = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void Raise(DeviceChangeNotification notification)
    {
        if (!_disposed)
        {
            DeviceChanged?.Invoke(this, notification);
        }
    }

    /// <summary>Only render (output) endpoint changes are surfaced, matching the loopback capture scope.</summary>
    private void FilteredRaise(DataFlow flow, DeviceChangeNotification onRender)
    {
        if (!_disposed && flow == DataFlow.Render)
        {
            DeviceChanged?.Invoke(this, onRender);
        }
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string pwstrDeviceId) =>
        FilteredRaise(flow, DeviceChangeNotification.DefaultChanged(pwstrDeviceId));

    void IMMNotificationClient.OnDeviceStateChanged(string pwstrDeviceId, NDeviceState dwNewState)
    {
        CDeviceState state = MapState(dwNewState);
        Raise(DeviceChangeNotification.StateChangedOf(pwstrDeviceId, state));
    }

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) =>
        Raise(DeviceChangeNotification.Added(pwstrDeviceId));

    void IMMNotificationClient.OnDeviceRemoved(string pwstrDeviceId) =>
        Raise(DeviceChangeNotification.Removed(pwstrDeviceId));

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Property changes are not part of the recovery contract; ignore.
    }

    private static CDeviceState MapState(NDeviceState state) => state switch
    {
        NDeviceState.Active => CDeviceState.Active,
        NDeviceState.Disabled => CDeviceState.Disabled,
        NDeviceState.NotPresent => CDeviceState.NotPresent,
        NDeviceState.Unplugged => CDeviceState.Unplugged,
        _ => CDeviceState.All,
    };
}
