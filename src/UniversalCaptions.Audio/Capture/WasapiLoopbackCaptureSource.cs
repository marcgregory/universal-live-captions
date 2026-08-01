using System.Runtime.InteropServices;
using NAudio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using UniversalCaptions.Audio.Converters;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Capture;

namespace UniversalCaptions.Audio.Capture;

/// <summary>
/// Captures the Windows system audio mix via WASAPI loopback, wrapped behind
/// <see cref="IAudioCapture"/>. The underlying NAudio <see cref="IWaveIn"/> is injected so tests
/// can use a fake device and pump synthetic PCM.
/// </summary>
public sealed class WasapiLoopbackCaptureSource : IAudioCapture
{
    private const int AudcltEDeviceInvalidated = unchecked((int)0x88890004);
    private const int AudcltEDeviceInUse = unchecked((int)0x8889000A);
    private const int AudcltEEndpointCreateFailed = unchecked((int)0x88890005);

    private readonly IWaveIn _waveIn;
    private readonly ByteToFloatConverter _converter;
    private readonly AudioFormat _format;
    private long _sequence;
    private bool _disposed;

    /// <summary>
    /// Creates a capture source over an existing NAudio wave input.
    /// </summary>
    /// <param name="waveIn">The wave input (normally a NAudio loopback capture device).</param>
    /// <exception cref="ArgumentNullException"><paramref name="waveIn"/> is null.</exception>
    public WasapiLoopbackCaptureSource(IWaveIn waveIn)
    {
        _waveIn = waveIn ?? throw new ArgumentNullException(nameof(waveIn));
        _converter = new ByteToFloatConverter(waveIn.WaveFormat);
        _format = new AudioFormat(waveIn.WaveFormat.SampleRate, waveIn.WaveFormat.Channels, waveIn.WaveFormat.BitsPerSample);

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
    }

    /// <inheritdoc />
    public event EventHandler<AudioChunk>? AudioAvailable;

    /// <inheritdoc />
    public event EventHandler<AudioCaptureError>? CaptureFailed;

    /// <inheritdoc />
    public AudioFormat Format => _format;

    /// <inheritdoc />
    public bool IsCapturing { get; private set; }

    /// <summary>
    /// Creates a capture source on a specific render device.
    /// </summary>
    /// <param name="deviceId">The Windows endpoint ID of the render device to capture.</param>
    /// <returns>The capture source.</returns>
    /// <exception cref="AudioCaptureException">The device is unavailable or loopback could not be initialized.</exception>
    public static WasapiLoopbackCaptureSource CreateForDevice(string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        MMDevice device;
        using (var enumerator = new MMDeviceEnumerator())
        {
            try
            {
                device = enumerator.GetDevice(deviceId);
            }
            catch (Exception ex) when (ex is COMException or MmException)
            {
                throw new AudioCaptureException(
                    AudioCaptureErrorKind.DeviceUnavailable,
                    $"The selected audio output device is unavailable: {ex.Message}",
                    ex);
            }
        }

        try
        {
            return new WasapiLoopbackCaptureSource(new WasapiLoopbackCapture(device));
        }
        catch (Exception ex) when (ex is COMException or MmException)
        {
            device.Dispose();
            throw new AudioCaptureException(
                AudioCaptureErrorKind.InitializationFailed,
                $"Could not initialize WASAPI loopback capture: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Creates a capture source on the default render device.
    /// </summary>
    /// <returns>The capture source.</returns>
    /// <exception cref="AudioCaptureException">No output device exists or loopback could not be initialized.</exception>
    public static WasapiLoopbackCaptureSource CreateDefault()
    {
        if (LoopbackDeviceEnumerator.EnumerateRenderDevices().Count == 0)
        {
            throw new AudioCaptureException(
                AudioCaptureErrorKind.NoOutputDevice,
                "No audio output device was found. Connect a speaker or headset and try again.");
        }

        try
        {
            return new WasapiLoopbackCaptureSource(new WasapiLoopbackCapture());
        }
        catch (Exception ex) when (ex is COMException or MmException)
        {
            throw new AudioCaptureException(
                AudioCaptureErrorKind.InitializationFailed,
                $"Could not initialize WASAPI loopback capture: {ex.Message}",
                ex);
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsCapturing)
        {
            return;
        }

        try
        {
            _waveIn.StartRecording();
            IsCapturing = true;
        }
        catch (Exception ex) when (ex is COMException or MmException)
        {
            RaiseFailed(AudioCaptureErrorKind.InitializationFailed, $"Could not start audio capture: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_disposed || !IsCapturing)
        {
            return;
        }

        try
        {
            _waveIn.StopRecording();
        }
        catch (Exception ex) when (ex is COMException or MmException)
        {
            RaiseFailed(AudioCaptureErrorKind.DeviceUnavailable, $"Could not stop audio capture cleanly: {ex.Message}", ex);
        }
        finally
        {
            IsCapturing = false;
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
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;

        try
        {
            _waveIn.Dispose();
        }
        catch (Exception ex) when (ex is COMException or MmException)
        {
            // Best effort disposal; the underlying device is being torn down anyway.
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        int samples = e.BytesRecorded / _converter.BytesPerFrame * _format.Channels;
        if (samples <= 0)
        {
            return;
        }

        var buffer = new float[samples];
        _converter.ConvertToFloat(e.Buffer, 0, e.BytesRecorded, buffer);
        var chunk = new AudioChunk(buffer, _format, DateTime.UtcNow, Interlocked.Increment(ref _sequence));
        AudioAvailable?.Invoke(this, chunk);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsCapturing = false;
        if (e.Exception is null)
        {
            return;
        }

        AudioCaptureErrorKind kind = MapError(e.Exception);
        RaiseFailed(kind, e.Exception.Message, e.Exception);
    }

    private void RaiseFailed(AudioCaptureErrorKind kind, string message, Exception? exception)
    {
        CaptureFailed?.Invoke(this, new AudioCaptureError(kind, message, exception));
    }

    private static AudioCaptureErrorKind MapError(Exception exception)
    {
        if (exception is not COMException com)
        {
            return AudioCaptureErrorKind.Unknown;
        }

        return com.HResult switch
        {
            AudcltEDeviceInvalidated => AudioCaptureErrorKind.DeviceDisconnected,
            AudcltEDeviceInUse => AudioCaptureErrorKind.DeviceUnavailable,
            AudcltEEndpointCreateFailed => AudioCaptureErrorKind.DeviceUnavailable,
            _ => AudioCaptureErrorKind.Unknown,
        };
    }
}
