using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Speech.Tests.Support;

namespace UniversalCaptions.Speech.Tests;

/// <summary>
/// Verifies the direct-trigger surface of <see cref="FakeSpeechToTextEngine"/>
/// (used by downstream caption-service tests in later slices).
/// </summary>
public sealed class FakeSpeechToTextEngineTests
{
    [Fact]
    public void EmitPartialNow_RaisesPartialWithIncrementingSequence()
    {
        using var engine = new FakeSpeechToTextEngine();
        var partials = new List<PartialTranscript>();
        engine.PartialTranscriptAvailable += (_, t) => partials.Add(t);

        engine.EmitPartialNow("hel");
        engine.EmitPartialNow("hello");

        Assert.Equal(2, partials.Count);
        Assert.Equal("hel", partials[0].Text);
        Assert.Equal("hello", partials[1].Text);
        Assert.Equal([0L, 1L], partials.Select(p => p.Sequence));
    }

    [Fact]
    public void EmitFinalNow_RaisesFinalWithIncrementingSequence()
    {
        using var engine = new FakeSpeechToTextEngine();
        var finals = new List<FinalTranscript>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t);

        engine.EmitFinalNow("done");

        Assert.Single(finals);
        Assert.Equal("done", finals[0].Text);
        Assert.Equal(0, finals[0].Sequence);
    }

    [Fact]
    public void DirectEmit_WorksWithoutStart()
    {
        using var engine = new FakeSpeechToTextEngine();
        FinalTranscript? final = null;
        engine.FinalTranscriptAvailable += (_, t) => final = t;

        engine.EmitFinalNow("offline trigger");

        Assert.NotNull(final);
    }

    [Fact]
    public void ScheduleError_AfterTranscription_ThenError()
    {
        using var engine = new FakeSpeechToTextEngine();
        var events = new List<string>();
        engine.PartialTranscriptAvailable += (_, _) => events.Add("partial");
        engine.RecognitionFailed += (_, _) => events.Add("error");
        engine.SchedulePartial(TimeSpan.Zero, "first");
        engine.ScheduleError(TimeSpan.Zero, SpeechRecognitionErrorKind.Unknown, "kaboom");
        engine.Start();

        engine.Process(BuildOneSecondChunk());

        Assert.Equal(["partial", "error"], events);
    }

    private static AudioChunk BuildOneSecondChunk()
    {
        const int sampleRate = 16000;
        return new AudioChunk(new float[sampleRate], new AudioFormat(sampleRate, 1, 32), DateTime.UtcNow, 1);
    }
}
