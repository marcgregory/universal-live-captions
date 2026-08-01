using System.Runtime.InteropServices;
using UniversalCaptions.App.Controls;
using UniversalCaptions.Audio.Capture;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Verifies <see cref="AudioSourceLoader"/> contains device-enumeration failures (PRD FR-10, NFR-6)
/// instead of letting a <see cref="COMException"/> escape into the WPF startup path.
/// </summary>
public class AudioSourceLoaderTests
{
    [Fact]
    public void Load_returns_devices_and_preferred_default()
    {
        var devices = new List<LoopbackDevice>
        {
            new("a", "Speakers"),
            new("b", "Headset"),
        };

        AudioSourceLoadResult result = AudioSourceLoader.Load(() => devices, () => devices[1]);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Devices.Count);
        Assert.Equal("b", result.Preferred!.Id);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void Load_enumeration_failure_surfaces_without_throwing()
    {
        AudioSourceLoadResult result = AudioSourceLoader.Load(
            () => throw new COMException("The audio service is disabled."),
            () => null);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Devices);
        Assert.Null(result.Preferred);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public void Load_empty_devices_has_no_preferred()
    {
        AudioSourceLoadResult result = AudioSourceLoader.Load(
            () => new List<LoopbackDevice>(),
            () => new LoopbackDevice("x", "Ghost"));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Devices);
        Assert.Null(result.Preferred);
    }

    [Fact]
    public void Load_skips_default_when_no_devices()
    {
        bool getDefaultCalled = false;
        AudioSourceLoadResult result = AudioSourceLoader.Load(
            () => new List<LoopbackDevice>(),
            () =>
            {
                getDefaultCalled = true;
                return null;
            });

        Assert.True(result.Succeeded);
        Assert.False(getDefaultCalled);
    }
}
