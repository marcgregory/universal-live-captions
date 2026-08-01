namespace UniversalCaptions.Core.Audio;

/// <summary>
/// Describes the sample layout of a PCM audio stream.
/// </summary>
/// <param name="SampleRate">Samples per second (per channel).</param>
/// <param name="Channels">Number of interleaved channels.</param>
/// <param name="BitsPerSample">Bits per single sample value.</param>
public readonly record struct AudioFormat(int SampleRate, int Channels, int BitsPerSample)
{
    /// <summary>Number of bytes that make up one multi-channel frame.</summary>
    public int FrameSizeInBytes => Channels * (BitsPerSample / 8);

    /// <summary>Number of bytes that pass per second at this format.</summary>
    public int BytesPerSecond => SampleRate * FrameSizeInBytes;

    /// <inheritdoc />
    public override string ToString() => $"{SampleRate} Hz, {Channels} ch, {BitsPerSample}-bit";
}
