using NAudio.Wave;

namespace UniversalCaptions.Audio.Tests.Support;

/// <summary>
/// A controllable <see cref="IWaveIn"/> used to test capture behavior without hardware.
/// </summary>
public sealed class FakeWaveIn : IWaveIn
{
    private Exception? _startException;

    public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public bool IsRecording { get; private set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public bool Disposed { get; private set; }

    public void StartRecording()
    {
        StartCount++;
        if (_startException is not null)
        {
            throw _startException;
        }

        IsRecording = true;
    }

    public void StopRecording()
    {
        StopCount++;
        IsRecording = false;
    }

    public void Dispose() => Disposed = true;

    /// <summary>Makes the next <see cref="StartRecording"/> throw.</summary>
    public void ThrowOnStart(Exception exception) => _startException = exception;

    /// <summary>Raises <see cref="DataAvailable"/> with the given bytes.</summary>
    public void EmitData(byte[] bytes) => DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));

    /// <summary>Raises <see cref="RecordingStopped"/>, optionally with an exception.</summary>
    public void EmitStopped(Exception? exception = null) => RecordingStopped?.Invoke(this, new StoppedEventArgs(exception!));
}
