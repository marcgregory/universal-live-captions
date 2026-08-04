using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using UniversalCaptions.Audio.Capture;
using UniversalCaptions.Core.Capture;
using CDeviceState = UniversalCaptions.Core.Capture.DeviceState;
using NDeviceState = NAudio.CoreAudioApi.DeviceState;

namespace UniversalCaptions.Audio.Tests;

/// <summary>
/// Verifies the device-change notifier's notification contract deterministically by invoking its
/// <see cref="IMMNotificationClient"/> methods directly (no COM registration, no audio service
/// required — the enumerator is created lazily in <see cref="WasapiDeviceChangeNotifier.Start"/>).
/// </summary>
public class WasapiDeviceChangeNotifierTests
{
    private static (WasapiDeviceChangeNotifier Notifier, List<DeviceChangeNotification> Events) Create()
    {
        var events = new List<DeviceChangeNotification>();
        var notifier = new WasapiDeviceChangeNotifier();
        notifier.DeviceChanged += (_, n) => events.Add(n);
        return (notifier, events);
    }

    private static IMMNotificationClient Notify(WasapiDeviceChangeNotifier notifier) => notifier;

    [Fact]
    public void DefaultDeviceChanged_RenderFlow_RaisesDefaultChangedWithDeviceId()
    {
        var (notifier, events) = Create();
        using (notifier)
        {
            Notify(notifier).OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, "render-device-123");
        }

        var single = Assert.Single(events);
        Assert.Equal(DeviceChangeKind.DefaultDeviceChanged, single.Kind);
        Assert.Equal("render-device-123", single.DeviceId);
        Assert.Null(single.State);
    }

    [Fact]
    public void DefaultDeviceChanged_CaptureFlow_IsFilteredOut()
    {
        var (notifier, events) = Create();
        using (notifier)
        {
            Notify(notifier).OnDefaultDeviceChanged(DataFlow.Capture, Role.Multimedia, "mic");
        }

        Assert.Empty(events);
    }

    [Fact]
    public void DeviceStateChanged_Unplugged_MapsStateAndDeviceId()
    {
        var (notifier, events) = Create();
        using (notifier)
        {
            Notify(notifier).OnDeviceStateChanged("headset", NDeviceState.Unplugged);
        }

        var single = Assert.Single(events);
        Assert.Equal(DeviceChangeKind.StateChanged, single.Kind);
        Assert.Equal("headset", single.DeviceId);
        Assert.Equal(CDeviceState.Unplugged, single.State);
    }

    [Fact]
    public void DeviceStateChanged_Disabled_MapsToDisabled()
    {
        var (notifier, events) = Create();
        using (notifier)
        {
            Notify(notifier).OnDeviceStateChanged("speaker", NDeviceState.Disabled);
        }

        Assert.Equal(CDeviceState.Disabled, Assert.Single(events).State);
    }

    [Theory]
    [InlineData(NDeviceState.Active, CDeviceState.Active)]
    [InlineData(NDeviceState.NotPresent, CDeviceState.NotPresent)]
    [InlineData(NDeviceState.All, CDeviceState.All)]
    public void DeviceStateChanged_MapsAllStates(NDeviceState naudioState, CDeviceState coreState)
    {
        var (notifier, events) = Create();
        using (notifier)
        {
            Notify(notifier).OnDeviceStateChanged("dev", naudioState);
        }

        Assert.Equal(coreState, Assert.Single(events).State);
    }

    [Fact]
    public void DeviceAdded_RaisesAdded()
    {
        var (notifier, events) = Create();
        using (notifier)
        {
            Notify(notifier).OnDeviceAdded("new-device");
        }

        Assert.Equal(DeviceChangeKind.Added, Assert.Single(events).Kind);
    }

    [Fact]
    public void DeviceRemoved_RaisesRemoved()
    {
        var (notifier, events) = Create();
        using (notifier)
        {
            Notify(notifier).OnDeviceRemoved("gone-device");
        }

        Assert.Equal(DeviceChangeKind.Removed, Assert.Single(events).Kind);
    }

    [Fact]
    public void PropertyValueChanged_IsIgnored()
    {
        var (notifier, events) = Create();
        using (notifier)
        {
            Notify(notifier).OnPropertyValueChanged("dev", new PropertyKey(default, 0));
        }

        Assert.Empty(events);
    }

    [Fact]
    public void AfterDispose_NoEventsAreRaised()
    {
        var (notifier, events) = Create();
        notifier.Dispose();
        Notify(notifier).OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, "dev");
        Notify(notifier).OnDeviceStateChanged("dev", NDeviceState.Unplugged);
        Assert.Empty(events);
    }
}
