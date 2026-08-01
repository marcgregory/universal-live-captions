using UniversalCaptions.Core.Processing;

namespace UniversalCaptions.Audio.Buffering;

/// <summary>
/// A thread-safe FIFO ring buffer of normalized float PCM samples.
/// On overflow the oldest samples are discarded so the newest data is always retained.
/// </summary>
public sealed class PcmRingBuffer : IAudioBuffer
{
    private readonly object _gate = new();
    private readonly float[] _buffer;
    private int _readPosition;
    private int _writePosition;
    private int _count;

    /// <summary>
    /// Creates a ring buffer with the given capacity.
    /// </summary>
    /// <param name="capacityInSamples">Maximum number of float samples held. Must be greater than zero.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacityInSamples"/> is not positive.</exception>
    public PcmRingBuffer(int capacityInSamples)
    {
        if (capacityInSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityInSamples), "Capacity must be greater than zero.");
        }

        _buffer = new float[capacityInSamples];
    }

    /// <inheritdoc />
    public int CapacityInSamples => _buffer.Length;

    /// <inheritdoc />
    public int ReadableCount
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <inheritdoc />
    public int Write(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            if (samples.Length >= _buffer.Length)
            {
                // The whole buffer will be replaced; keep only the newest portion.
                samples[^_buffer.Length..].CopyTo(_buffer);
                _writePosition = 0;
                _readPosition = 0;
                _count = _buffer.Length;
                return samples.Length;
            }

            int dropped = Math.Max(0, _count + samples.Length - _buffer.Length);
            if (dropped > 0)
            {
                _readPosition = (_readPosition + dropped) % _buffer.Length;
                _count -= dropped;
            }

            int firstChunk = Math.Min(samples.Length, _buffer.Length - _writePosition);
            samples[..firstChunk].CopyTo(_buffer.AsSpan(_writePosition, firstChunk));
            if (firstChunk < samples.Length)
            {
                samples[firstChunk..].CopyTo(_buffer);
                _writePosition = samples.Length - firstChunk;
            }
            else
            {
                _writePosition += firstChunk;
            }

            if (_writePosition == _buffer.Length)
            {
                _writePosition = 0;
            }

            _count += samples.Length;
            return samples.Length;
        }
    }

    /// <inheritdoc />
    public int Read(Span<float> destination)
    {
        if (destination.Length == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            int toRead = Math.Min(destination.Length, _count);
            int firstChunk = Math.Min(toRead, _buffer.Length - _readPosition);
            _buffer.AsSpan(_readPosition, firstChunk).CopyTo(destination);
            if (firstChunk < toRead)
            {
                _buffer.AsSpan(0, toRead - firstChunk).CopyTo(destination[firstChunk..]);
            }

            _readPosition = (_readPosition + toRead) % _buffer.Length;
            _count -= toRead;
            return toRead;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            _readPosition = 0;
            _writePosition = 0;
            _count = 0;
        }
    }
}
