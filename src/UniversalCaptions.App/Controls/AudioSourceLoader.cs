using System.Runtime.InteropServices;
using UniversalCaptions.Audio.Capture;

namespace UniversalCaptions.App.Controls;

/// <summary>
/// The outcome of resolving the audio source list for the control window.
/// </summary>
/// <param name="Devices">The enumerated render devices (empty when enumeration failed).</param>
/// <param name="Preferred">The default render device, or null when none is preferred.</param>
/// <param name="Failure">The enumeration failure, or null on success.</param>
public sealed record AudioSourceLoadResult(
    IReadOnlyList<LoopbackDevice> Devices,
    LoopbackDevice? Preferred,
    COMException? Failure)
{
    /// <summary>True when the device list was enumerated successfully.</summary>
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Loads the audio source list for the control window without letting device-enumeration failures
/// escape into the WPF startup path (PRD FR-10 surface errors; NFR-6 no crash on device loss).
/// NAudio's device enumerator throws <see cref="COMException"/> when the Windows audio service is
/// stopped or disabled; this loader converts that into a result the window can show as a status
/// instead of an unhandled exception. It is delegate-driven so it is testable without NAudio or WPF.
/// </summary>
public static class AudioSourceLoader
{
    /// <summary>
    /// Enumerates render devices and resolves the preferred default, containing device failures.
    /// </summary>
    /// <param name="enumerate">Lists the active render devices.</param>
    /// <param name="getDefault">Returns the default render device, or null.</param>
    /// <returns>The load result; never throws for a device-enumeration failure.</returns>
    public static AudioSourceLoadResult Load(
        Func<IReadOnlyList<LoopbackDevice>> enumerate,
        Func<LoopbackDevice?> getDefault)
    {
        ArgumentNullException.ThrowIfNull(enumerate);
        ArgumentNullException.ThrowIfNull(getDefault);

        try
        {
            IReadOnlyList<LoopbackDevice> devices = enumerate();
            LoopbackDevice? preferred = devices.Count == 0 ? null : getDefault();
            return new AudioSourceLoadResult(devices, preferred, null);
        }
        catch (COMException ex)
        {
            return new AudioSourceLoadResult([], null, ex);
        }
    }
}
