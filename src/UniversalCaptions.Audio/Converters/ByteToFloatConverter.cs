using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace UniversalCaptions.Audio.Converters;

/// <summary>
/// Converts raw PCM bytes (from a NAudio <see cref="IWaveIn"/>) into normalized float samples.
/// Supports 8/16/24/32-bit integer PCM and 32/64-bit IEEE float PCM, including via
/// <see cref="WaveFormatExtensible"/> sub-format detection.
/// </summary>
public sealed class ByteToFloatConverter
{
    private readonly WaveFormat _format;

    /// <summary>
    /// Creates a converter for the given wave format.
    /// </summary>
    /// <param name="format">The format of the input bytes.</param>
    /// <exception cref="NotSupportedException">The encoding or bit depth is not supported.</exception>
    public ByteToFloatConverter(WaveFormat format)
    {
        _format = format;
        ValidateFormat(format);
    }

    /// <summary>The number of bytes that make up one multi-channel frame.</summary>
    public int BytesPerFrame => _format.Channels * (_format.BitsPerSample / 8);

    /// <summary>
    /// Converts a byte buffer into normalized float samples (range [-1, 1]).
    /// </summary>
    /// <param name="source">The byte buffer from the capture device.</param>
    /// <param name="offset">Offset into <paramref name="source"/>.</param>
    /// <param name="count">Number of bytes to convert.</param>
    /// <param name="destination">The destination float array. Must be large enough for the converted samples.</param>
    /// <returns>The number of float samples written.</returns>
    public int ConvertToFloat(byte[] source, int offset, int count, float[] destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (offset < 0 || count < 0 || offset + count > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Byte range is outside the source buffer.");
        }

        int bytesPerSample = _format.BitsPerSample / 8;
        int samples = count / bytesPerSample;

        if (destination.Length < samples)
        {
            throw new ArgumentException("Destination array is too small for the converted samples.", nameof(destination));
        }

        bool isFloat = IsFloatFormat();
        for (int i = 0; i < samples; i++)
        {
            int byteIndex = offset + (i * bytesPerSample);
            destination[i] = _format.BitsPerSample switch
            {
                8 when !isFloat => ((source[byteIndex] - 128) / 128f),
                16 when !isFloat => BitConverter.ToInt16(source, byteIndex) / 32768f,
                24 when !isFloat => Convert24BitPcm(source, byteIndex),
                32 when isFloat => BitConverter.ToSingle(source, byteIndex),
                32 when !isFloat => BitConverter.ToInt32(source, byteIndex) / 2147483648f,
                64 when isFloat => (float)(BitConverter.ToDouble(source, byteIndex) / 1d),
                _ => throw new NotSupportedException($"Unsupported bit depth {_format.BitsPerSample} for encoding {_format.Encoding}."),
            };
        }

        return samples;
    }

    /// <summary>
    /// Decodes a little-endian 24-bit PCM sample into the range [-1, 1].
    /// The three bytes are packed into the top 24 bits of a 32-bit value and
    /// arithmetic-shifted right so the sign bit is preserved.
    /// </summary>
    private static float Convert24BitPcm(byte[] source, int byteIndex)
    {
        int value = (source[byteIndex] << 8) | (source[byteIndex + 1] << 16) | (source[byteIndex + 2] << 24);
        return (value >> 8) / 8388608f;
    }

    private bool IsFloatFormat()
    {
        if (_format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            return true;
        }

        return _format is WaveFormatExtensible extensible && extensible.SubFormat == AudioSubtypes.MFAudioFormat_Float;
    }

    private static void ValidateFormat(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == AudioSubtypes.MFAudioFormat_Float);
        bool isIntegerPcm = format.Encoding == WaveFormatEncoding.Pcm
            || (format is WaveFormatExtensible ext2 && ext2.SubFormat == AudioSubtypes.MFAudioFormat_PCM);

        if (!isFloat && !isIntegerPcm)
        {
            throw new NotSupportedException($"Unsupported wave encoding '{format.Encoding}'.");
        }

        if (isFloat && format.BitsPerSample is not (32 or 64))
        {
            throw new NotSupportedException($"Unsupported float bit depth {format.BitsPerSample}.");
        }

        if (isIntegerPcm && format.BitsPerSample is not (8 or 16 or 24 or 32))
        {
            throw new NotSupportedException($"Unsupported integer PCM bit depth {format.BitsPerSample}.");
        }
    }
}
