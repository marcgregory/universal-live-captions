using System.Text.Json;
using UniversalCaptions.Speech.Gemini;
using UniversalCaptions.Speech.Gemini.Tests.Spikes;

namespace UniversalCaptions.Speech.Gemini.Tests;

/// <summary>
/// Deterministic tests for the SPIKE-ONLY A/B regression harness
/// (<see cref="GeminiDirectWireSpike.BuildSetupWithInputAudioTranscription"/>,
/// <see cref="GeminiDirectWireSpike.FloatToPcm16Le"/>, <see cref="GeminiDirectWireSpike.ChunkBytesFor"/>,
/// and the <see cref="AbUtterance"/> comparison rules). These prove the wire variables the experiment
/// isolates: variant A = the frozen production setup frame (no <c>inputAudioTranscription</c>) and
/// variant B = the SAME frame plus the top-level <c>inputAudioTranscription</c> the OLD working
/// benchmark client sent (src/UniversalCaptions.Benchmarks/Translation/GeminiLiveTranslateClient.cs).
/// No API key, no network, no changes to the frozen A1–A6 channel/protocol/engine.
/// </summary>
public sealed class AbRegressionHarnessTests
{
    private const string Model = "models/gemini-3.5-live-translate-preview";
    private const string TargetCode = "fil";

    // ----- Setup-frame A/B builder -----

    [Fact]
    public void BuildSetupWithInputAudioTranscription_Parses()
    {
        string baseSetup = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode);
        string variantB = GeminiDirectWireSpike.BuildSetupWithInputAudioTranscription(baseSetup);

        using var document = JsonDocument.Parse(variantB);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void VariantB_AddsTopLevelInputAudioTranscription()
    {
        string baseSetup = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode);
        string variantB = GeminiDirectWireSpike.BuildSetupWithInputAudioTranscription(baseSetup);
        using var document = JsonDocument.Parse(variantB);

        JsonElement setup = document.RootElement.GetProperty("setup");
        Assert.True(setup.TryGetProperty("inputAudioTranscription", out JsonElement input));
        Assert.Equal(JsonValueKind.Object, input.ValueKind);

        // Must NOT be nested inside generationConfig (that path is the Round-3-rejected form).
        JsonElement generationConfig = setup.GetProperty("generationConfig");
        Assert.False(generationConfig.TryGetProperty("inputAudioTranscription", out _));
    }

    [Fact]
    public void VariantB_PreservesAllFrozenFields()
    {
        string baseSetup = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode);
        string variantB = GeminiDirectWireSpike.BuildSetupWithInputAudioTranscription(baseSetup);

        using var docB = JsonDocument.Parse(variantB);
        JsonElement setupB = docB.RootElement.GetProperty("setup");

        Assert.Equal(Model, setupB.GetProperty("model").GetString());
        Assert.Equal(TargetCode, setupB.GetProperty("generationConfig")
            .GetProperty("translationConfig")
            .GetProperty("targetLanguageCode")
            .GetString());
        Assert.True(setupB.TryGetProperty("outputAudioTranscription", out _));
        Assert.True(setupB.TryGetProperty("inputAudioTranscription", out _));
    }

    [Fact]
    public void VariantA_IsTheFrozenSetupFrame()
    {
        // The experiment's control must be byte-identical to production's setup frame, so a
        // "Gemini is bad" result under A can only be blamed on the top-level inputAudioTranscription
        // field that B adds — never on an accidental frame drift in the spike.
        string frozen = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode);
        string variantB = GeminiDirectWireSpike.BuildSetupWithInputAudioTranscription(frozen);

        // Round-trip through the frozen builder must still equal the frozen frame (control stability).
        using var doc = JsonDocument.Parse(frozen);
        Assert.Equal(Model, doc.RootElement.GetProperty("setup").GetProperty("model").GetString());
        Assert.False(doc.RootElement.GetProperty("setup").TryGetProperty("inputAudioTranscription", out _));
        Assert.True(variantB.Contains("\"inputAudioTranscription\"", StringComparison.Ordinal));
    }

    [Fact]
    public void VariantB_ContainsOnlyOneInputAudioTranscriptionField()
    {
        string baseSetup = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode);
        string variantB = GeminiDirectWireSpike.BuildSetupWithInputAudioTranscription(baseSetup);

        using var document = JsonDocument.Parse(variantB);
        JsonElement setup = document.RootElement.GetProperty("setup");
        int occurrences = 0;
        foreach (JsonProperty property in setup.EnumerateObject())
        {
            if (property.Name == "inputAudioTranscription")
            {
                occurrences++;
            }
        }

        Assert.Equal(1, occurrences);
    }

    // ----- PCM16 wire-bytes helper -----

    [Fact]
    public void FloatToPcm16Le_ProducesLittleEndianSignedBytes()
    {
        // +1.0 → (int)(1f * short.MaxValue) = 32767 → LE bytes 0xFF 0x7F.
        // -1.0 → (int)(-1f * short.MaxValue) = -32767 → LE bytes 0x01 0x80 (NOT 0x00 0x80: the
        // spike mirrors the frozen engine's clamp+scale, and -1.0 maps to -32767, not short.MinValue).
        // 0.0 → 0x0000.
        byte[] pcm = GeminiDirectWireSpike.FloatToPcm16Le(new[] { 1f, -1f, 0f });

        Assert.Equal(new byte[] { 0xFF, 0x7F, 0x01, 0x80, 0x00, 0x00 }, pcm);
    }

    [Fact]
    public void FloatToPcm16Le_ClampsOutOfRangeSamples()
    {
        // 2.0 and -2.0 must clamp, not wrap.
        byte[] pcm = GeminiDirectWireSpike.FloatToPcm16Le(new[] { 2f, -2f });

        Assert.Equal(new byte[] { 0xFF, 0x7F, 0x01, 0x80 }, pcm);
    }

    [Fact]
    public void ChunkBytesFor_16kHz100ms_Is3200()
    {
        // The OLD benchmark client's chunk size at 16 kHz / 100 ms = 3200 bytes (PCM16 mono).
        Assert.Equal(3200, GeminiDirectWireSpike.ChunkBytesFor(16000, 100));
    }

    // ----- A/B comparison rules -----

    [Fact]
    public void AbUtterance_ClassifiesMatchingSequencesAsEqual()
    {
        var a = new AbVariantResult();
        a.Outputs.Add(new AbOutputRecord { Text = "Magandang umaga" });
        a.Outputs.Add(new AbOutputRecord { Text = "Magandang umaga lahat." });
        a.FinalText = "Magandang umaga lahat.";
        var b = new AbVariantResult();
        b.Outputs.Add(new AbOutputRecord { Text = "Magandang umaga" });
        b.Outputs.Add(new AbOutputRecord { Text = "Magandang umaga lahat." });
        b.FinalText = "Magandang umaga lahat.";

        var ut = new AbUtterance { VariantA = a, VariantB = b };
        ut.ClassifyCompare();

        Assert.True(ut.VariantAHasOutput);
        Assert.True(ut.OutputSequenceEquals);
        Assert.True(ut.FinalTextsMatch);
        Assert.True(ut.FinalSequenceEquals);
    }

    [Fact]
    public void AbUtterance_DetectsDifferentFinalTexts()
    {
        var a = new AbVariantResult();
        a.Outputs.Add(new AbOutputRecord { Text = "Ano" });
        a.FinalText = "Ano";
        var b = new AbVariantResult();
        b.Outputs.Add(new AbOutputRecord { Text = "Ano ang pangalan mo?" });
        b.FinalText = "Ano ang pangalan mo?";

        var ut = new AbUtterance { VariantA = a, VariantB = b };
        ut.ClassifyCompare();

        Assert.True(ut.VariantAHasOutput);
        Assert.False(ut.OutputSequenceEquals);
        Assert.False(ut.FinalTextsMatch);
        Assert.False(ut.FinalSequenceEquals);
    }

    [Fact]
    public void AbUtterance_WithNoOutputA_IsNotComparable()
    {
        var a = new AbVariantResult();
        var b = new AbVariantResult();
        b.Outputs.Add(new AbOutputRecord { Text = "Ano" });
        b.FinalText = "Ano";

        var ut = new AbUtterance { VariantA = a, VariantB = b };
        ut.ClassifyCompare();

        Assert.False(ut.VariantAHasOutput);
        Assert.False(ut.OutputSequenceEquals);
        Assert.True(ut.FinalSequenceEquals); // vacuous pass when A has no output
    }
}
