namespace UniversalCaptions.Core.Capture;

/// <summary>
/// The kinds of endpoint change a device-change monitor can report (TD-002).
/// </summary>
public enum DeviceChangeKind
{
    /// <summary>The default render device changed to a different endpoint.</summary>
    DefaultDeviceChanged,

    /// <summary>A device's connection state changed (for example unplugged or disabled).</summary>
    StateChanged,

    /// <summary>A render device was added.</summary>
    Added,

    /// <summary>A render device was removed.</summary>
    Removed,
}

/// <summary>
/// The connection state of an audio endpoint. Core-neutral mirror of the Windows endpoint states so
/// the pure contract layer does not depend on NAudio.
/// </summary>
public enum DeviceState
{
    Active,
    Disabled,
    NotPresent,
    Unplugged,
    All,
}

/// <summary>
/// A reported endpoint change on the render-device side. Carries the affected endpoint id and, for
/// <see cref="DeviceChangeKind.StateChanged"/>, the new state.
/// </summary>
public sealed record DeviceChangeNotification(DeviceChangeKind Kind, string? DeviceId, DeviceState? State)
{
    /// <summary>Creates a default-device-changed notification for <paramref name="deviceId"/>.</summary>
    public static DeviceChangeNotification DefaultChanged(string deviceId) =>
        new(DeviceChangeKind.DefaultDeviceChanged, deviceId, null);

    /// <summary>Creates a state-changed notification for <paramref name="deviceId"/>.</summary>
    public static DeviceChangeNotification StateChangedOf(string deviceId, DeviceState state) =>
        new(DeviceChangeKind.StateChanged, deviceId, state);

    /// <summary>Creates a device-added notification.</summary>
    public static DeviceChangeNotification Added(string deviceId) =>
        new(DeviceChangeKind.Added, deviceId, null);

    /// <summary>Creates a device-removed notification.</summary>
    public static DeviceChangeNotification Removed(string deviceId) =>
        new(DeviceChangeKind.Removed, deviceId, null);
}
