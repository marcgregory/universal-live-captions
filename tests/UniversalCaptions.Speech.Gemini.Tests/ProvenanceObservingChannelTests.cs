using System.Text.Json;
using UniversalCaptions.Speech.Gemini.Tests.Spikes;

namespace UniversalCaptions.Speech.Gemini.Tests;

/// <summary>
/// Deterministic tests for the SPIKE-ONLY provenance harness
/// (<see cref="ProvenanceAccumulator"/>, <see cref="ProvenanceObservingChannel"/>,
/// <see cref="UtteranceProvenance"/>). These prove the structural evidence rules that a real-wire
/// run must satisfy for the chain "English WAV → Gemini WebSocket → serverContent → generated
/// audio → outputAudioTranscription → FinalText" to be verified — with Argos provably out of the
/// chain (ArgosCalls == 0, ArgosOutput == "NONE"). No API key, no network: frames are hand-crafted
/// fixtures. The production Gemini channel/protocol/engine code is never touched by these tests.
/// </summary>
public sealed class ProvenanceObservingChannelTests
{
    // A serverContent frame that carries the canonical Live Translate shape: modelTurn.parts[]
    // containing BOTH a text part (compatibility fallback) and an inlineData audio part (the
    // generated audio), plus the outputTranscription.text side-channel and turnComplete. The
    // base64 "AAAA//8=" decodes to 5 bytes (0x00 0x00 0x00 0xFF 0xFF).
    private const string AudioAndSideChannelFrame =
        "{\"serverContent\":{" +
        "\"modelTurn\":{\"parts\":[" +
        "{\"text\":\"older modelTurn shape\"}," +
        "{\"inlineData\":{\"mimeType\":\"audio/pcm\",\"data\":\"AAAA//8=\"}}" +
        "]}," +
        "\"outputTranscription\":{\"text\":\"Ang nag-iisang pagsasalin\"}," +
        "\"turnComplete\":true}}";

    private const string PartialTextOnlyFrame =
        "{\"serverContent\":{" +
        "\"modelTurn\":{\"parts\":[{\"text\":\"partial fallback\"}]}," +
        "\"outputTranscription\":{\"text\":\"Bahagyang pagsasalin\"}," +
        "\"partial\":true}}";

    [Fact]
    public void ServerContentFrame_WithGeneratedAudioAndSideChannel_AccumulatesProvenance()
    {
        var accumulator = new ProvenanceAccumulator();

        accumulator.ObserveFrame(AudioAndSideChannelFrame);

        Assert.Equal(1, accumulator.ServerContentFrames);
        Assert.Equal(1, accumulator.AudioParts);
        Assert.Equal(1, accumulator.ServerContentFramesWithAudio);
        Assert.Equal(5, accumulator.AudioBytes);
        Assert.Equal(new[] { "audio/pcm" }, accumulator.AudioMimeTypes);
        Assert.Equal(1, accumulator.OutputTranscriptionFrames);
        Assert.Equal("Ang nag-iisang pagsasalin", accumulator.LastOutputTranscriptionText);
        Assert.Equal(1, accumulator.ModelTurnTextParts);
        Assert.Equal(1, accumulator.TurnCompleteFrames);
        Assert.Equal(0, accumulator.PartialFrames);
        Assert.Equal(0, accumulator.ErrorFrames);
        Assert.Equal(0, accumulator.UnknownFrames);
        Assert.Equal(0, accumulator.MalformedFrames);
    }

    [Fact]
    public void ServerContentFrame_PartialTextOnly_CountsPartialAndNoAudio()
    {
        var accumulator = new ProvenanceAccumulator();

        accumulator.ObserveFrame(PartialTextOnlyFrame);

        Assert.Equal(1, accumulator.ServerContentFrames);
        Assert.Equal(0, accumulator.AudioParts);
        Assert.Equal(0, accumulator.ServerContentFramesWithAudio);
        Assert.Equal(1, accumulator.PartialFrames);
        Assert.Equal(1, accumulator.OutputTranscriptionFrames);
        Assert.Equal("Bahagyang pagsasalin", accumulator.LastOutputTranscriptionText);
        Assert.Equal(1, accumulator.ModelTurnTextParts);
        Assert.Equal(0, accumulator.TurnCompleteFrames);
    }

    [Fact]
    public void MultipleFrames_AccumulateFrameKindCounts()
    {
        var accumulator = new ProvenanceAccumulator();

        accumulator.ObserveFrame(AudioAndSideChannelFrame);
        accumulator.ObserveFrame(PartialTextOnlyFrame);
        accumulator.ObserveFrame("{\"setupComplete\":{}}");
        accumulator.ObserveFrame("{\"goAway\":{\"reason\":\"time\"}}");
        accumulator.ObserveFrame("{\"sessionResumptionUpdate\":{\"resumable\":true,\"newHandle\":\"abc\"}}");
        accumulator.ObserveFrame("{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\",\"message\":\"quota\"}}");

        Assert.Equal(2, accumulator.ServerContentFrames);
        Assert.Equal(1, accumulator.SetupCompleteFrames);
        Assert.Equal(1, accumulator.GoAwayFrames);
        Assert.Equal(1, accumulator.SessionResumptionFrames);
        Assert.Equal(1, accumulator.ErrorFrames);
        Assert.Equal(0, accumulator.UnknownFrames);
        Assert.Equal(0, accumulator.MalformedFrames);
        Assert.Equal(2, accumulator.OutputTranscriptionFrames);
    }

    [Fact]
    public void UnknownTopLevelFrame_RecordsStructuralFingerprint_NotPayload()
    {
        var accumulator = new ProvenanceAccumulator();

        accumulator.ObserveFrame("{\"newThing\":{\"a\":1}}");

        Assert.Equal(1, accumulator.UnknownFrames);
        Assert.Equal("newThing:Object", Assert.Single(accumulator.UnknownFingerprints));
    }

    [Fact]
    public void UnknownTopLevelFrame_SameShape_RecordedOnce()
    {
        var accumulator = new ProvenanceAccumulator();

        accumulator.ObserveFrame("{\"newThing\":{\"a\":1}}");
        accumulator.ObserveFrame("{\"newThing\":{\"a\":2}}");

        Assert.Equal(2, accumulator.UnknownFrames);
        Assert.Single(accumulator.UnknownFingerprints);
    }

    [Fact]
    public void MalformedJson_CountsWithoutThrowing()
    {
        var accumulator = new ProvenanceAccumulator();

        accumulator.ObserveFrame("not json {{{");

        Assert.Equal(1, accumulator.MalformedFrames);
        Assert.Equal(0, accumulator.ServerContentFrames);
        Assert.Equal(0, accumulator.UnknownFrames);
    }

    [Fact]
    public void NonObjectRoot_CountsAsMalformed()
    {
        var accumulator = new ProvenanceAccumulator();

        accumulator.ObserveFrame("[1,2,3]");

        Assert.Equal(1, accumulator.MalformedFrames);
        Assert.Equal(0, accumulator.UnknownFrames);
    }

    [Fact]
    public void Snapshot_ProvenanceVerified_WhenAudioAndSideChannelPresent_ArgosPinnedZero()
    {
        var accumulator = new ProvenanceAccumulator();
        accumulator.ObserveFrame(AudioAndSideChannelFrame);

        UtteranceProvenance snapshot = accumulator.ToSnapshot("Ang nag-iisang pagsasalin");

        Assert.True(snapshot.ProvenanceVerified);
        Assert.Equal(0, snapshot.ArgosCalls);
        Assert.Equal("NONE", snapshot.ArgosOutput);
        Assert.True(snapshot.FinalTextMatchesOutputTranscription);
    }

    [Fact]
    public void Snapshot_NotVerified_WhenNoGeneratedAudio()
    {
        var accumulator = new ProvenanceAccumulator();
        accumulator.ObserveFrame(PartialTextOnlyFrame);

        UtteranceProvenance snapshot = accumulator.ToSnapshot("Bahagyang pagsasalin");

        Assert.False(snapshot.ProvenanceVerified);
        Assert.Equal(0, snapshot.ArgosCalls);
        Assert.Equal("NONE", snapshot.ArgosOutput);
    }

    [Fact]
    public void Snapshot_NotVerified_WhenSideChannelTextMissing()
    {
        var accumulator = new ProvenanceAccumulator();
        accumulator.ObserveFrame(
            "{\"serverContent\":{\"modelTurn\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"audio/pcm\",\"data\":\"AAAA//8=\"}}]},\"turnComplete\":true}}");

        UtteranceProvenance snapshot = accumulator.ToSnapshot("Ano");

        Assert.False(snapshot.ProvenanceVerified);
        Assert.Null(snapshot.GeminiOutputTranscription);
    }

    [Fact]
    public void Snapshot_FinalTextMismatch_WhenEngineTextDiffersFromSideChannel()
    {
        var accumulator = new ProvenanceAccumulator();
        accumulator.ObserveFrame(AudioAndSideChannelFrame);

        UtteranceProvenance snapshot = accumulator.ToSnapshot("iba ang text");

        Assert.False(snapshot.FinalTextMatchesOutputTranscription);
        Assert.True(snapshot.ProvenanceVerified);
    }

    [Fact]
    public async Task DecoratorChannel_ForwardsFramesAndAccumulatesProvenance()
    {
        var fake = new FakeGeminiChannel();
        await using var decorator = new ProvenanceObservingChannel(fake);

        await decorator.OpenAsync(new Uri("wss://example.com"), CancellationToken.None);
        fake.QueueServerFrame(AudioAndSideChannelFrame);
        string? received = await decorator.ReceiveTextAsync(CancellationToken.None);
        await decorator.SendTextAsync("{\"realtimeInput\":{\"audio\":{}}}", CancellationToken.None);
        await decorator.CloseAsync("done", CancellationToken.None);

        Assert.NotNull(received);
        using var document = JsonDocument.Parse(received);
        Assert.True(document.RootElement.TryGetProperty("serverContent", out _));
        Assert.Equal(1, decorator.Provenance.AudioParts);
        Assert.Equal(1, decorator.Provenance.ServerContentFrames);
        Assert.Equal("Ang nag-iisang pagsasalin", decorator.Provenance.LastOutputTranscriptionText);
        Assert.Equal("{\"realtimeInput\":{\"audio\":{}}}", fake.LastSentFrame);
        Assert.Equal(1, fake.CloseCount);
    }
}
