using UniversalCaptions.Audio.Processing;
using UniversalCaptions.Core.Audio;

namespace UniversalCaptions.Audio.Tests;

public sealed class EnergyVadTests
{
    private static readonly AudioFormat Format = new(48000, 1, 32);

    [Fact]
    public void Silence_IsNotSpeech()
    {
        var vad = new EnergyVad();

        bool first = vad.IsSpeech(Silence());
        bool second = vad.IsSpeech(Silence());

        Assert.False(first);
        Assert.False(second);
    }

    [Fact]
    public void Speech_RequiresConsecutiveActiveChunks()
    {
        var vad = new EnergyVad();

        Assert.False(vad.IsSpeech(Speech()));
        Assert.True(vad.IsSpeech(Speech()));
    }

    [Fact]
    public void Silence_WithinHangover_KeepsSpeech()
    {
        var vad = new EnergyVad();
        vad.IsSpeech(Speech());
        vad.IsSpeech(Speech());

        for (int i = 0; i < 5; i++)
        {
            Assert.True(vad.IsSpeech(Silence()), $"silence {i + 1} within hangover should keep speech active");
        }
    }

    [Fact]
    public void Silence_BeyondHangover_EndsSpeech()
    {
        var vad = new EnergyVad();
        vad.IsSpeech(Speech());
        vad.IsSpeech(Speech());

        for (int i = 0; i < 6; i++)
        {
            vad.IsSpeech(Silence());
        }

        Assert.False(vad.IsSpeech(Silence()));
    }

    [Fact]
    public void LowEnergySignal_BelowThreshold_IsSilence()
    {
        var vad = new EnergyVad(new VadOptions(RmsThreshold: 0.5));
        var quiet = new AudioChunk([0.1f, 0.1f, 0.1f], Format, DateTime.UtcNow, 1);

        Assert.False(vad.IsSpeech(quiet));
        Assert.False(vad.IsSpeech(quiet));
    }

    [Fact]
    public void Reset_ClearsSpeechState()
    {
        var vad = new EnergyVad();
        vad.IsSpeech(Speech());
        vad.IsSpeech(Speech());
        Assert.True(vad.IsSpeech(Speech()));

        vad.Reset();

        Assert.False(vad.IsSpeech(Speech()));
    }

    [Fact]
    public void EmptyChunk_ReturnsCurrentStateWithoutChangingIt()
    {
        var vad = new EnergyVad();
        Assert.False(vad.IsSpeech(Empty()));

        vad.IsSpeech(Speech());
        vad.IsSpeech(Speech());

        Assert.True(vad.IsSpeech(Empty()));
        Assert.True(vad.IsSpeech(Empty()));
    }

    [Fact]
    public void NullChunk_Throws()
    {
        var vad = new EnergyVad();
        Assert.Throws<ArgumentNullException>(() => vad.IsSpeech(null!));
    }

    private static AudioChunk Speech() => new([0.5f, 0.5f, 0.5f], Format, DateTime.UtcNow, 0);

    private static AudioChunk Silence() => new([0f, 0f, 0f], Format, DateTime.UtcNow, 0);

    private static AudioChunk Empty() => new([], Format, DateTime.UtcNow, 0);
}
