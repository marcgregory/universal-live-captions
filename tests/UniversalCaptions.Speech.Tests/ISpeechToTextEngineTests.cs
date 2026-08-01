using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Speech.Tests.Support;

namespace UniversalCaptions.Speech.Tests;

/// <summary>
/// Verifies the <see cref="ISpeechToTextEngine"/> contract through the deterministic
/// <see cref="FakeSpeechToTextEngine"/>.
/// </summary>
public sealed class ISpeechToTextEngineTests
{
    private const int SampleRate = 16000;

    [Fact]
    public void Process_BeforeStart_IsIgnored()
    {
        using var engine = new FakeSpeechToTextEngine();

        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.5), 1));

        Assert.Equal(0, engine.ProcessedChunks);
        Assert.Equal(TimeSpan.Zero, engine.ProcessedDuration);
    }

    [Fact]
    public void Start_EnablesRecognition_AndStop_DisablesIt()
    {
        using var engine = new FakeSpeechToTextEngine();

        Assert.False(engine.IsRecognizing);
        engine.Start();
        Assert.True(engine.IsRecognizing);
        engine.Stop();
        Assert.False(engine.IsRecognizing);
        Assert.Equal(1, engine.StartCount);
        Assert.Equal(1, engine.StopCount);
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        using var engine = new FakeSpeechToTextEngine();
        engine.Start();

        engine.Stop();
        engine.Stop();

        Assert.False(engine.IsRecognizing);
        Assert.Equal(2, engine.StopCount);
    }

    [Fact]
    public void RaisesPartialTranscripts_AsAudioAccumulates()
    {
        using var engine = new FakeSpeechToTextEngine();
        var captured = new List<PartialTranscript>();
        engine.PartialTranscriptAvailable += (_, t) => captured.Add(t);
        engine.SchedulePartial(TimeSpan.FromSeconds(0.5), "hel");
        engine.SchedulePartial(TimeSpan.FromSeconds(1.0), "hello");
        engine.Start();

        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.25), 1));
        Assert.Empty(captured);

        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.25), 2));
        Assert.Single(captured);
        Assert.Equal("hel", captured[0].Text);

        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.25), 3));
        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.25), 4));
        Assert.Equal(2, captured.Count);
        Assert.Equal("hello", captured[1].Text);
    }

    [Fact]
    public void RaisesFinalTranscript_ThenPartial_FinalOrderingIsPreserved()
    {
        using var engine = new FakeSpeechToTextEngine();
        var events = new List<string>();
        engine.PartialTranscriptAvailable += (_, _) => events.Add("partial");
        engine.FinalTranscriptAvailable += (_, _) => events.Add("final");
        engine.SchedulePartial(TimeSpan.FromSeconds(0.5), "hello");
        engine.ScheduleFinal(TimeSpan.FromSeconds(1.0), "hello");
        engine.Start();

        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.25), 1));
        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.25), 2));
        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.25), 3));
        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.25), 4));

        Assert.Equal(["partial", "final"], events);
    }

    [Fact]
    public void Transcripts_HaveMonotonicallyIncreasingSequence()
    {
        using var engine = new FakeSpeechToTextEngine();
        var sequences = new List<long>();
        engine.PartialTranscriptAvailable += (_, t) => sequences.Add(t.Sequence);
        engine.FinalTranscriptAvailable += (_, t) => sequences.Add(t.Sequence);
        engine.SchedulePartial(TimeSpan.FromSeconds(0.2), "a");
        engine.ScheduleFinal(TimeSpan.FromSeconds(0.4), "a");
        engine.SchedulePartial(TimeSpan.FromSeconds(0.6), "b");
        engine.Start();

        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.3), 1));
        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.3), 2));
        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.3), 3));

        Assert.Equal([0, 1, 2], sequences);
    }

    [Fact]
    public void Stop_CancelsInProgressWork_NoFurtherTranscripts()
    {
        using var engine = new FakeSpeechToTextEngine();
        var finals = 0;
        engine.FinalTranscriptAvailable += (_, _) => finals++;
        engine.SchedulePartial(TimeSpan.FromSeconds(0.5), "hello");
        engine.ScheduleFinal(TimeSpan.FromSeconds(1.5), "hello");
        engine.Start();

        engine.Process(BuildChunk(TimeSpan.FromSeconds(1.0), 1));
        engine.Stop();
        engine.Process(BuildChunk(TimeSpan.FromSeconds(1.0), 2));

        Assert.Equal(0, finals);
        Assert.False(engine.IsRecognizing);
    }

    [Fact]
    public void Process_AfterStop_IsIgnored()
    {
        using var engine = new FakeSpeechToTextEngine();
        engine.Start();
        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.25), 1));
        engine.Stop();
        long chunksBefore = engine.ProcessedChunks;

        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.25), 2));

        Assert.Equal(chunksBefore, engine.ProcessedChunks);
    }

    [Fact]
    public void RecognitionFailed_IsRaised_ForRuntimeError()
    {
        using var engine = new FakeSpeechToTextEngine();
        SpeechRecognitionError? error = null;
        engine.RecognitionFailed += (_, e) => error = e;
        engine.ScheduleError(TimeSpan.FromSeconds(0.5), SpeechRecognitionErrorKind.EngineFailed, "boom");
        engine.Start();

        engine.Process(BuildChunk(TimeSpan.FromSeconds(0.5), 1));

        Assert.NotNull(error);
        Assert.Equal(SpeechRecognitionErrorKind.EngineFailed, error!.Kind);
        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public void Start_Failure_Throws_AndRecognitionDoesNotBegin()
    {
        using var engine = new FakeSpeechToTextEngine();
        engine.ThrowOnStart(new InvalidOperationException("model missing"));

        Assert.Throws<InvalidOperationException>(() => engine.Start());
        Assert.False(engine.IsRecognizing);
    }

    [Fact]
    public void Start_ModelError_RaisesRecognitionFailed()
    {
        using var engine = new FakeSpeechToTextEngine();
        SpeechRecognitionError? error = null;
        engine.RecognitionFailed += (_, e) => error = e;
        engine.RaiseErrorOnStart(SpeechRecognitionErrorKind.ModelNotFound, "model not found");

        engine.Start();

        Assert.NotNull(error);
        Assert.Equal(SpeechRecognitionErrorKind.ModelNotFound, error!.Kind);
        Assert.False(engine.IsRecognizing);
    }

    [Fact]
    public void ContinuousChunks_AccumulateDuration_AndTriggerTimedFinal()
    {
        using var engine = new FakeSpeechToTextEngine();
        var finals = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);
        engine.ScheduleFinal(TimeSpan.FromSeconds(1.0), "complete sentence");
        engine.Start();

        for (int i = 1; i <= 10; i++)
        {
            engine.Process(BuildChunk(TimeSpan.FromMilliseconds(100), i));
        }

        Assert.Equal(10, engine.ProcessedChunks);
        Assert.InRange(engine.ProcessedDuration.TotalSeconds, 0.95, 1.05);
        Assert.Equal(["complete sentence"], finals);
    }

    [Fact]
    public void Transcript_CarriesSourceTimestamp_ForLatencyMeasurement()
    {
        using var engine = new FakeSpeechToTextEngine();
        PartialTranscript? transcript = null;
        engine.PartialTranscriptAvailable += (_, t) => transcript = t;
        var capturedAt = DateTime.UtcNow.AddSeconds(-1);
        engine.SchedulePartial(TimeSpan.Zero, "now");
        engine.Start();

        engine.Process(new AudioChunk(new float[SampleRate], new AudioFormat(SampleRate, 1, 32), capturedAt, 1));

        Assert.NotNull(transcript);
        Assert.Equal(capturedAt, transcript!.CapturedAtUtc);
        Assert.True(transcript.Latency >= TimeSpan.Zero);
        Assert.True(transcript.EmittedAtUtc >= transcript.CapturedAtUtc);
    }

    [Fact]
    public void Dispose_IsForwarded()
    {
        var engine = new FakeSpeechToTextEngine();

        engine.Dispose();

        Assert.True(engine.Disposed);
    }

    private static AudioChunk BuildChunk(TimeSpan duration, long sequence)
    {
        int frameCount = (int)(duration.TotalSeconds * SampleRate);
        var format = new AudioFormat(SampleRate, 1, 32);
        return new AudioChunk(new float[frameCount], format, DateTime.UtcNow, sequence);
    }
}
