using UniversalCaptions.App.Pipeline;
using UniversalCaptions.Core.Capture;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Verifies the default-device auto-recovery contract (TD-002) deterministically: the coordinator
/// restarts only while the session is on the default device, only for trigger notifications, and
/// coalesces bursts into a single restart.
/// </summary>
public class DefaultDeviceAutoRecoveryTests
{
    private sealed class FakeDeviceChangeMonitor : IDeviceChangeMonitor
    {
        public event EventHandler<DeviceChangeNotification>? DeviceChanged;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }

        public void Start() => Started = true;
        public void Stop() => Started = false;
        public void Dispose() => Disposed = true;

        public void Raise(DeviceChangeNotification notification) => DeviceChanged?.Invoke(this, notification);
    }

    [Fact]
    public void DefaultDeviceChanged_WhileOnDefaultDevice_RestartsDefault()
    {
        using var monitor = new FakeDeviceChangeMonitor();
        var restarts = new List<string>();
        using var recovery = new DefaultDeviceAutoRecovery(
            monitor, () => true, id => { restarts.Add(id ?? "<default>"); return Task.CompletedTask; });

        monitor.Raise(DeviceChangeNotification.DefaultChanged("new-default"));

        Assert.Equal(["<default>"], restarts);
        Assert.Equal(1, recovery.RestartCount);
    }

    [Fact]
    public void DefaultDeviceChanged_WhileOnExplicitDevice_DoesNotRestart()
    {
        using var monitor = new FakeDeviceChangeMonitor();
        var restarts = new List<string>();
        using var recovery = new DefaultDeviceAutoRecovery(
            monitor, () => false, id => { restarts.Add(id ?? "<default>"); return Task.CompletedTask; });

        monitor.Raise(DeviceChangeNotification.DefaultChanged("new-default"));

        Assert.Empty(restarts);
        Assert.Equal(0, recovery.RestartCount);
    }

    [Theory]
    [InlineData(DeviceState.NotPresent)]
    [InlineData(DeviceState.Unplugged)]
    public void StateChanged_UnpluggedOrNotPresent_WhileOnDefaultDevice_Restarts(DeviceState state)
    {
        using var monitor = new FakeDeviceChangeMonitor();
        var restarts = new List<string>();
        using var recovery = new DefaultDeviceAutoRecovery(
            monitor, () => true, id => { restarts.Add(id ?? "<default>"); return Task.CompletedTask; });

        monitor.Raise(DeviceChangeNotification.StateChangedOf("device", state));

        Assert.Equal(["<default>"], restarts);
    }

    [Fact]
    public void StateChanged_Active_DoesNotRestart()
    {
        using var monitor = new FakeDeviceChangeMonitor();
        var restarts = new List<string>();
        using var recovery = new DefaultDeviceAutoRecovery(
            monitor, () => true, id => { restarts.Add(id ?? "<default>"); return Task.CompletedTask; });

        monitor.Raise(DeviceChangeNotification.StateChangedOf("device", DeviceState.Active));

        Assert.Empty(restarts);
    }

    [Fact]
    public void DeviceRemoved_WhileOnDefaultDevice_Restarts()
    {
        using var monitor = new FakeDeviceChangeMonitor();
        var restarts = new List<string>();
        using var recovery = new DefaultDeviceAutoRecovery(
            monitor, () => true, id => { restarts.Add(id ?? "<default>"); return Task.CompletedTask; });

        monitor.Raise(DeviceChangeNotification.Removed("gone-device"));

        Assert.Equal(["<default>"], restarts);
    }

    [Fact]
    public void DeviceAdded_DoesNotRestart()
    {
        using var monitor = new FakeDeviceChangeMonitor();
        var restarts = new List<string>();
        using var recovery = new DefaultDeviceAutoRecovery(
            monitor, () => true,
            id => { restarts.Add(id ?? "<default>"); return Task.CompletedTask; });

        monitor.Raise(DeviceChangeNotification.Added("new-device"));

        Assert.Empty(restarts);
    }

    [Fact]
    public void BurstOfNotifications_CoalescesIntoSingleRestart()
    {
        using var monitor = new FakeDeviceChangeMonitor();
        var restarts = new List<string>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var recovery = new DefaultDeviceAutoRecovery(
            monitor, () => true, id =>
            {
                restarts.Add(id ?? "<default>");
                return gate.Task;
            });

        monitor.Raise(DeviceChangeNotification.DefaultChanged("a"));
        monitor.Raise(DeviceChangeNotification.DefaultChanged("b"));
        monitor.Raise(DeviceChangeNotification.StateChangedOf("device", DeviceState.Unplugged));

        // The first notification launched a restart that is still in flight; the burst was coalesced.
        Assert.Single(restarts);
        Assert.Equal(0, recovery.RestartCount);

        gate.SetResult();
        SpinWait.SpinUntil(() => recovery.RestartCount == 1, TimeSpan.FromSeconds(2));
        Assert.Equal(1, recovery.RestartCount);
    }

    [Fact]
    public void AfterDispose_DoesNotRestart()
    {
        using var monitor = new FakeDeviceChangeMonitor();
        var restarts = new List<string>();
        var recovery = new DefaultDeviceAutoRecovery(
            monitor, () => true, id => { restarts.Add(id ?? "<default>"); return Task.CompletedTask; });
        recovery.Dispose();

        monitor.Raise(DeviceChangeNotification.DefaultChanged("new-default"));

        Assert.Empty(restarts);
    }
}
