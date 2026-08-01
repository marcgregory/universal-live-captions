using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace UniversalCaptions.Audio.Capture;

/// <summary>A loopback-capable render (output) device.</summary>
/// <param name="Id">The Windows endpoint ID.</param>
/// <param name="FriendlyName">A human-readable device name.</param>
public sealed record LoopbackDevice(string Id, string FriendlyName);

/// <summary>
/// Enumerates render (output) devices that can be used for WASAPI loopback capture.
/// </summary>
public static class LoopbackDeviceEnumerator
{
    /// <summary>
    /// Lists all active render devices. Returns an empty list when none exist.
    /// </summary>
    /// <returns>The active render devices.</returns>
    public static IReadOnlyList<LoopbackDevice> EnumerateRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var result = new List<LoopbackDevice>(devices.Count);
        foreach (MMDevice device in devices)
        {
            using (device)
            {
                result.Add(new LoopbackDevice(device.ID, device.FriendlyName));
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the default render device, or null when none exists.
    /// </summary>
    /// <returns>The default render device or null.</returns>
    public static LoopbackDevice? GetDefaultRenderDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            using MMDevice? device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device is null ? null : new LoopbackDevice(device.ID, device.FriendlyName);
        }
        catch (COMException)
        {
            return null;
        }
    }
}
