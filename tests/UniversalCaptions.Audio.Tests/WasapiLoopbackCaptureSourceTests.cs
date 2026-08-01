using System.Runtime.InteropServices;
using UniversalCaptions.Audio.Capture;
using UniversalCaptions.Audio.Tests.Support;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Capture;

namespace UniversalCaptions.Audio.Tests;

public sealed class WasapiLoopbackCaptureSourceTests
{
    private const int DeviceInvalidated = unchecked((int)0x88890004);
    private const int DeviceInUse = unchecked((int)0x8889000A);

    [Fact]
    public void Format_MatchesWaveFormat()
    {
        using var source = new WasapiLoopbackCaptureSource(new FakeWaveIn());

        Assert.Equal(new AudioFormat(48000, 2, 32), source.Format);
    }

    [Fact]
    public void Start_SetsCapturing()
    {
        var fake = new FakeWaveIn();
        using var source = new WasapiLoopbackCaptureSource(fake);

        source.Start();

        Assert.True(source.IsCapturing);
        Assert.True(fake.IsRecording);
        Assert.Equal(1, fake.StartCount);
    }

    [Fact]
    public void Start_WhenAlreadyCapturing_IsIdempotent()
    {
        var fake = new FakeWaveIn();
        using var source = new WasapiLoopbackCaptureSource(fake);

        source.Start();
        source.Start();

        Assert.Equal(1, fake.StartCount);
        Assert.True(source.IsCapturing);
    }

    [Fact]
    public void Start_OnDeviceFailure_RaisesInitializationFailed()
    {
        var fake = new FakeWaveIn();
        fake.ThrowOnStart(new COMException("endpoint in use", DeviceInUse));
        using var source = new WasapiLoopbackCaptureSource(fake);

        AudioCaptureError? error = null;
        source.CaptureFailed += (_, e) => error = e;
        source.Start();

        Assert.NotNull(error);
        Assert.Equal(AudioCaptureErrorKind.InitializationFailed, error!.Kind);
        Assert.False(source.IsCapturing);
    }

    [Fact]
    public void DataAvailable_EmitsChunkInWaveFormat()
    {
        var fake = new FakeWaveIn();
        using var source = new WasapiLoopbackCaptureSource(fake);

        AudioChunk? chunk = null;
        source.AudioAvailable += (_, c) => chunk = c;

        fake.EmitData([.. BitConverter.GetBytes(0.5f), .. BitConverter.GetBytes(-0.25f)]);

        Assert.NotNull(chunk);
        Assert.Equal(new AudioFormat(48000, 2, 32), chunk!.Format);
        Assert.Equal(2, chunk.Samples.Length);
        Assert.Equal(0.5f, chunk.Samples[0], 5);
        Assert.Equal(-0.25f, chunk.Samples[1], 5);
    }

    [Fact]
    public void DataAvailable_SequencesIncreasePerChunk()
    {
        var fake = new FakeWaveIn();
        using var source = new WasapiLoopbackCaptureSource(fake);

        var sequences = new List<long>();
        source.AudioAvailable += (_, c) => sequences.Add(c.Sequence);

        fake.EmitData([.. BitConverter.GetBytes(0.5f), .. BitConverter.GetBytes(0.5f)]);
        fake.EmitData([.. BitConverter.GetBytes(0.25f), .. BitConverter.GetBytes(0.25f)]);

        Assert.Equal([1L, 2L], sequences);
    }

    [Fact]
    public void DataAvailable_ZeroBytes_RaisesNoChunk()
    {
        var fake = new FakeWaveIn();
        using var source = new WasapiLoopbackCaptureSource(fake);

        int raised = 0;
        source.AudioAvailable += (_, _) => raised++;

        fake.EmitData([]);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void RecordingStopped_WithoutException_ClearsCapturing()
    {
        var fake = new FakeWaveIn();
        using var source = new WasapiLoopbackCaptureSource(fake);
        source.Start();
        Assert.True(source.IsCapturing);

        fake.EmitStopped();

        Assert.False(source.IsCapturing);
    }

    [Fact]
    public void RecordingStopped_DeviceInvalidated_RaisesDeviceDisconnected()
    {
        var fake = new FakeWaveIn();
        using var source = new WasapiLoopbackCaptureSource(fake);

        AudioCaptureError? error = null;
        source.CaptureFailed += (_, e) => error = e;

        fake.EmitStopped(new COMException("device invalidated", DeviceInvalidated));

        Assert.NotNull(error);
        Assert.Equal(AudioCaptureErrorKind.DeviceDisconnected, error!.Kind);
    }

    [Fact]
    public void RecordingStopped_DeviceInUse_RaisesDeviceUnavailable()
    {
        var fake = new FakeWaveIn();
        using var source = new WasapiLoopbackCaptureSource(fake);

        AudioCaptureError? error = null;
        source.CaptureFailed += (_, e) => error = e;

        fake.EmitStopped(new COMException("device in use", DeviceInUse));

        Assert.NotNull(error);
        Assert.Equal(AudioCaptureErrorKind.DeviceUnavailable, error!.Kind);
    }

    [Fact]
    public void RecordingStopped_UnknownException_RaisesUnknown()
    {
        var fake = new FakeWaveIn();
        using var source = new WasapiLoopbackCaptureSource(fake);

        AudioCaptureError? error = null;
        source.CaptureFailed += (_, e) => error = e;

        fake.EmitStopped(new InvalidOperationException("unexpected"));

        Assert.NotNull(error);
        Assert.Equal(AudioCaptureErrorKind.Unknown, error!.Kind);
    }

    [Fact]
    public void Stop_StopsWaveInAndClearsCapturing()
    {
        var fake = new FakeWaveIn();
        using var source = new WasapiLoopbackCaptureSource(fake);
        source.Start();

        source.Stop();

        Assert.False(source.IsCapturing);
        Assert.False(fake.IsRecording);
        Assert.Equal(1, fake.StopCount);
    }

    [Fact]
    public void Stop_WhenNotCapturing_DoesNothing()
    {
        var fake = new FakeWaveIn();
        using var source = new WasapiLoopbackCaptureSource(fake);

        source.Stop();

        Assert.Equal(0, fake.StopCount);
    }

    [Fact]
    public void Dispose_UnsubscribesAndDisposesWaveIn()
    {
        var fake = new FakeWaveIn();
        var source = new WasapiLoopbackCaptureSource(fake);

        int raised = 0;
        source.AudioAvailable += (_, _) => raised++;
        source.Dispose();

        fake.EmitData([.. BitConverter.GetBytes(0.5f), .. BitConverter.GetBytes(0.5f)]);

        Assert.True(fake.Disposed);
        Assert.Equal(0, raised);
    }
}
