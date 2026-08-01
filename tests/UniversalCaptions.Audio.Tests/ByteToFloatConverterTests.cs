using System.IO;
using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;
using UniversalCaptions.Audio.Converters;

namespace UniversalCaptions.Audio.Tests;

public sealed class ByteToFloatConverterTests
{
    [Fact]
    public void Converts16BitPcmToFloat()
    {
        var converter = new ByteToFloatConverter(new WaveFormat(8000, 16, 1));
        byte[] source = [0x00, 0x40, 0x00, 0xC0];

        var destination = new float[2];
        int written = converter.ConvertToFloat(source, 0, source.Length, destination);

        Assert.Equal(2, written);
        Assert.Equal(0.5f, destination[0], 4);
        Assert.Equal(-0.5f, destination[1], 4);
    }

    [Fact]
    public void Converts8BitPcmToFloat()
    {
        var converter = new ByteToFloatConverter(new WaveFormat(8000, 8, 1));

        var destination = new float[3];
        converter.ConvertToFloat([128, 0, 255], 0, 3, destination);

        Assert.Equal(0f, destination[0], 6);
        Assert.Equal(-1f, destination[1], 6);
        Assert.Equal(0.9921875f, destination[2], 6);
    }

    [Fact]
    public void Converts24BitPcmToFloat()
    {
        var converter = new ByteToFloatConverter(new WaveFormat(8000, 24, 1));

        var destination = new float[1];
        converter.ConvertToFloat([0x00, 0x00, 0x40], 0, 3, destination);

        Assert.Equal(0.5f, destination[0], 4);
    }

    [Fact]
    public void ConvertsNegative24BitPcmToFloat()
    {
        var converter = new ByteToFloatConverter(new WaveFormat(8000, 24, 1));

        var destination = new float[1];
        converter.ConvertToFloat([0x00, 0x00, 0xC0], 0, 3, destination);

        Assert.Equal(-0.5f, destination[0], 4);
    }

    [Fact]
    public void Converts32BitFloatToFloat()
    {
        var converter = new ByteToFloatConverter(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        byte[] source = BitConverter.GetBytes(0.25f);

        var destination = new float[1];
        int written = converter.ConvertToFloat(source, 0, source.Length, destination);

        Assert.Equal(1, written);
        Assert.Equal(0.25f, destination[0], 6);
    }

    [Fact]
    public void ConvertsIeeeFloatExtensibleSubFormat()
    {
        var format = BuildExtensibleFormat(48000, 2, 32, AudioSubtypes.MFAudioFormat_Float);
        var converter = new ByteToFloatConverter(format);
        byte[] source = BitConverter.GetBytes(-0.75f);

        var destination = new float[1];
        int written = converter.ConvertToFloat(source, 0, source.Length, destination);

        Assert.Equal(1, written);
        Assert.Equal(-0.75f, destination[0], 6);
    }

    [Fact]
    public void ConvertsPcmExtensibleSubFormat()
    {
        var format = BuildExtensibleFormat(48000, 1, 16, AudioSubtypes.MFAudioFormat_PCM);
        var converter = new ByteToFloatConverter(format);
        byte[] source = BitConverter.GetBytes((short)16384);

        var destination = new float[1];
        int written = converter.ConvertToFloat(source, 0, source.Length, destination);

        Assert.Equal(1, written);
        Assert.Equal(0.5f, destination[0], 4);
    }

    [Fact]
    public void HandlesStereoInterleaving()
    {
        var converter = new ByteToFloatConverter(new WaveFormat(8000, 16, 2));
        byte[] source = [
            .. BitConverter.GetBytes((short)16384),  // left  0.5
            .. BitConverter.GetBytes((short)-16384), // right -0.5
        ];

        var destination = new float[2];
        int written = converter.ConvertToFloat(source, 0, source.Length, destination);

        Assert.Equal(2, written);
        Assert.Equal(0.5f, destination[0], 4);
        Assert.Equal(-0.5f, destination[1], 4);
    }

    [Fact]
    public void UnsupportedEncoding_Throws()
    {
        var alaw = WaveFormat.CreateALawFormat(8000, 1);
        Assert.Throws<NotSupportedException>(() => new ByteToFloatConverter(alaw));
    }

    [Fact]
    public void Convert_WithInvalidRange_Throws()
    {
        var converter = new ByteToFloatConverter(new WaveFormat(8000, 16, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => converter.ConvertToFloat([1, 2, 3, 4], 0, 100, new float[4]));
    }

    [Fact]
    public void Convert_WithTooSmallDestination_Throws()
    {
        var converter = new ByteToFloatConverter(new WaveFormat(8000, 16, 1));
        byte[] source = BitConverter.GetBytes((short)0);
        Assert.Throws<ArgumentException>(() => converter.ConvertToFloat(source, 0, 2, new float[0]));
    }

    private static WaveFormatExtensible BuildExtensibleFormat(int sampleRate, int channels, int bitsPerSample, Guid subFormat)
    {
        int blockAlign = channels * (bitsPerSample / 8);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(unchecked((short)0xFFFE));   // wFormatTag = WAVE_FORMAT_EXTENSIBLE
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);    // nAvgBytesPerSec
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);
        writer.Write((short)22);                  // cbSize
        writer.Write((short)bitsPerSample);       // wValidBitsPerSample
        writer.Write(0x3u);                       // dwChannelMask (front left + right)
        writer.Write(subFormat.ToByteArray());    // SubFormat GUID
        writer.Flush();

        stream.Position = 0;
        var buffer = stream.GetBuffer();
        IntPtr ptr = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, ptr, buffer.Length);
            return (WaveFormatExtensible)WaveFormat.MarshalFromPtr(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
