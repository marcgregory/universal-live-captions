using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Speech.Tests.Support;

namespace UniversalCaptions.Speech.Tests;

/// <summary>
/// Verifies <see cref="WhisperSpeechToTextEngine"/> orchestration (buffering, decode loop,
/// partial/stable/final commit behavior, events, cancellation, error handling) using an
/// injected deterministic decoder instead of a model.
/// </summary>
public sealed class WhisperSpeechToTextEngineTests
{
    private const int Rate = 16_000;
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static WhisperEngineOptions TestOptions(TimeSpan? boundaryWaitBudget = null) => new()
    {
        SampleRate = Rate,
        WindowDuration = TimeSpan.FromSeconds(2.0),
        DecodeInterval = TimeSpan.FromSeconds(0.5),
        MinimumAudioBeforeFirstDecode = TimeSpan.FromSeconds(0.5),
        CommitOverlap = TimeSpan.FromSeconds(0.2),
        StabilityWindow = 2,
        BoundaryWaitBudget = boundaryWaitBudget ?? TimeSpan.FromSeconds(2),
        ModelPath = "unused.bin",
    };

    private static AudioChunk Chunk(DateTime capturedAt, long sequence)
    {
        int frames = Rate / 2;
        return new AudioChunk(new float[frames], new AudioFormat(Rate, 1, 32), capturedAt, sequence);
    }

    private static AudioChunk Chunk(long sequence) =>
        Chunk(Base + TimeSpan.FromSeconds(0.5 * (sequence - 1)), sequence);

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(20);
        }

        Assert.True(condition(), $"Condition not met within {timeoutMs} ms.");
    }

    /// <summary>Builds a decoder that replays a scripted list of window texts in order.</summary>
    private static SegmentDecoder ScriptedDecoder(params string[] windowTexts)
    {
        var script = new Queue<string>(windowTexts);
        return (_, _) => script.Count > 0
            ? [new TranscriptSegment(script.Dequeue(), TimeSpan.Zero, TimeSpan.FromSeconds(0.5))]
            : [];
    }

    /// <summary>
    /// Builds a decoder whose windows carry explicit segment lists, so completed segment boundaries
    /// exist within each window (required by the Option B boundary-preserving fallback).
    /// </summary>
    private static SegmentDecoder ScriptedSegmentDecoder(params string[][] windowSegments)
    {
        var script = new Queue<string[]>(windowSegments);
        return (_, _) => script.Count > 0
            ? script.Dequeue().Select(t => new TranscriptSegment(t, TimeSpan.Zero, TimeSpan.FromSeconds(0.5))).ToArray()
            : [];
    }

    [Fact]
    public void EmitsPartialsThenFinals_AsStableTextIsConfirmed()
    {
        // Windows carry explicit segment lists so completed boundaries exist: segment 0 closes at
        // "Today we're going ", segment 1 at "to discuss ", segment 2 at "the budget " (ADR-0007
        // Option B — an interior prefix inside a still-open segment must not be finalized).
        var decoder = ScriptedSegmentDecoder(
            ["Today we're going "],
            ["Today we're going ", "to discuss "],
            ["Today we're going ", "to discuss ", "the budget "],
            ["Today we're going ", "to discuss ", "the budget "]);
        using var engine = new WhisperSpeechToTextEngine(TestOptions(TimeSpan.Zero), decoder);
        var finals = new List<string>();
        var partials = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);
        engine.PartialTranscriptAvailable += (_, t) => partials.Add(t.Text);

        engine.Start();
        engine.Process(Chunk(1));
        WaitUntil(() => partials.Count == 1);
        engine.Process(Chunk(2));
        WaitUntil(() => finals.Count == 1);
        engine.Process(Chunk(3));
        WaitUntil(() => finals.Count == 2);
        engine.Process(Chunk(4));
        WaitUntil(() => finals.Count == 3);

        Assert.Equal(["Today we're going ", "to discuss ", "the budget "], finals);
        Assert.Equal(["Today we're going ", "to discuss ", "the budget "], partials);
        Assert.True(engine.IsRecognizing);
    }

    [Fact]
    public void ChangingPartials_DoNotPrematurelyCommit()
    {
        // "today"/"tonight" diverge (no commit), then a completed boundary at "tonight we're going "
        // allows exactly that segment to finalize; "to " finalizes when its own segment closes.
        var decoder = ScriptedSegmentDecoder(
            ["today we're going "],
            ["tonight we're going "],
            ["tonight we're going ", "to "],
            ["tonight we're going ", "to "]);
        using var engine = new WhisperSpeechToTextEngine(TestOptions(TimeSpan.Zero), decoder);
        var finals = new List<string>();
        var partials = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);
        engine.PartialTranscriptAvailable += (_, t) => partials.Add(t.Text);

        engine.Start();
        engine.Process(Chunk(1));
        WaitUntil(() => partials.Count == 1);
        engine.Process(Chunk(2));
        WaitUntil(() => partials.Count == 2);
        engine.Process(Chunk(3));
        WaitUntil(() => finals.Count == 1);
        engine.Process(Chunk(4));
        WaitUntil(() => finals.Count == 2);

        Assert.Equal(["tonight we're going ", "to "], finals);
        Assert.DoesNotContain(finals, f => f.Contains("today", StringComparison.Ordinal));
        Assert.Equal(3, partials.Count);
    }

    [Fact]
    public void FinalText_IsNotEmittedTwice()
    {
        var decoder = ScriptedDecoder("hello world ", "hello world ", "hello world ", "hello world ");
        using var engine = new WhisperSpeechToTextEngine(TestOptions(), decoder);
        var finals = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);

        engine.Start();
        for (int i = 1; i <= 4; i++)
        {
            engine.Process(Chunk(i));
            Thread.Sleep(50);
        }

        WaitUntil(() => finals.Count == 1);
        Thread.Sleep(100);

        Assert.Equal(["hello world "], finals);
    }

    [Fact]
    public void Stop_DoesNotCommitIncompleteAudio()
    {
        var decoder = ScriptedDecoder("a ", "b ", "c ");
        using var engine = new WhisperSpeechToTextEngine(TestOptions(), decoder);
        var finals = new List<string>();
        var partials = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);
        engine.PartialTranscriptAvailable += (_, t) => partials.Add(t.Text);

        engine.Start();
        engine.Process(Chunk(1));
        WaitUntil(() => partials.Count == 1);
        engine.Process(Chunk(2));
        WaitUntil(() => partials.Count == 2);
        engine.Process(Chunk(3));
        WaitUntil(() => partials.Count == 3);

        engine.Stop();
        Assert.False(engine.IsRecognizing);
        Assert.Empty(finals);

        engine.Process(Chunk(4));
        engine.Process(Chunk(5));
        Thread.Sleep(300);

        Assert.Equal(3, partials.Count);
        Assert.Empty(finals);
    }

    [Fact]
    public void Restart_ResetsCommitState()
    {
        var script = new Queue<string>();
        SegmentDecoder decoder = (_, _) => script.Count > 0
            ? [new TranscriptSegment(script.Dequeue(), TimeSpan.Zero, TimeSpan.FromSeconds(0.5))]
            : [];
        using var engine = new WhisperSpeechToTextEngine(TestOptions(), decoder);
        var finals = new List<string>();
        var partials = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);
        engine.PartialTranscriptAvailable += (_, t) => partials.Add(t.Text);

        script = new Queue<string>(["hello ", "hello ", "hello "]);
        engine.Start();
        engine.Process(Chunk(1));
        WaitUntil(() => partials.Count == 1);
        engine.Process(Chunk(2));
        WaitUntil(() => finals.Count == 1);
        engine.Stop();

        script = new Queue<string>(["world ", "world ", "world "]);
        engine.Start();
        engine.Process(Chunk(4));
        WaitUntil(() => partials.Count == 2);
        engine.Process(Chunk(5));
        WaitUntil(() => finals.Count == 2);
        engine.Stop();

        Assert.Equal(["hello ", "world "], finals);
    }

    [Fact]
    public void DecoderFailure_DoesNotLeaveStaleCommittedText()
    {
        int call = 0;
        SegmentDecoder decoder = (_, _) =>
        {
            call++;
            if (call == 3)
            {
                throw new InvalidOperationException("decode crashed");
            }

            return [new TranscriptSegment(call <= 2 ? "hello " : "goodbye ", TimeSpan.Zero, TimeSpan.FromSeconds(0.5))];
        };
        using var engine = new WhisperSpeechToTextEngine(TestOptions(), decoder);
        var finals = new List<string>();
        SpeechRecognitionError? error = null;
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);
        engine.RecognitionFailed += (_, e) => error = e;

        engine.Start();
        engine.Process(Chunk(1));
        WaitUntil(() => call >= 1);
        engine.Process(Chunk(2));
        WaitUntil(() => finals.Count == 1);
        engine.Process(Chunk(3));
        WaitUntil(() => error is not null);

        Assert.Equal(SpeechRecognitionErrorKind.EngineFailed, error!.Kind);
        Assert.False(engine.IsRecognizing);
        Assert.Equal(["hello "], finals);

        engine.Start();
        engine.Process(Chunk(4));
        WaitUntil(() => call >= 4);
        engine.Process(Chunk(5));
        WaitUntil(() => finals.Count == 2);

        Assert.Equal(["hello ", "goodbye "], finals);
    }

    [Fact]
    public async Task Stop_AndDisposeAsync_WhileDecodeInProgress_IsClean()
    {
        var gate = new ManualResetEventSlim(false);
        var decodeStarted = new ManualResetEventSlim(false);
        SegmentDecoder decoder = (_, ct) =>
        {
            decodeStarted.Set();
            while (!gate.Wait(10) && !ct.IsCancellationRequested)
            {
            }

            ct.ThrowIfCancellationRequested();
            return [new TranscriptSegment("done", TimeSpan.Zero, TimeSpan.FromSeconds(0.1))];
        };
        var engine = new WhisperSpeechToTextEngine(TestOptions(), decoder);

        engine.Start();
        engine.Process(Chunk(1));
        Assert.True(decodeStarted.Wait(5000), "decode did not start.");

        engine.Stop();
        await engine.DisposeAsync();
        gate.Set();

        Assert.False(engine.IsRecognizing);
    }

    [Fact]
    public void Process_BeforeStart_IsIgnored()
    {
        SegmentDecoder decoder = (_, _) => [new TranscriptSegment("x", TimeSpan.Zero, TimeSpan.FromSeconds(0.1))];
        using var engine = new WhisperSpeechToTextEngine(TestOptions(), decoder);
        var emissions = 0;
        engine.PartialTranscriptAvailable += (_, _) => emissions++;
        engine.FinalTranscriptAvailable += (_, _) => emissions++;

        engine.Process(Chunk(1));

        Thread.Sleep(200);
        Assert.Equal(0, emissions);
        Assert.False(engine.IsRecognizing);
    }

    [Fact]
    public void Stop_CancelsDecode_AndNoFurtherTranscripts()
    {
        SegmentDecoder decoder = (_, _) => [new TranscriptSegment("partial", TimeSpan.Zero, TimeSpan.FromSeconds(0.5))];
        using var engine = new WhisperSpeechToTextEngine(TestOptions(), decoder);
        var partials = 0;
        engine.PartialTranscriptAvailable += (_, _) => partials++;

        engine.Start();
        engine.Process(Chunk(1));
        WaitUntil(() => partials == 1);

        engine.Stop();
        Assert.False(engine.IsRecognizing);

        engine.Process(Chunk(2));
        engine.Process(Chunk(3));
        Thread.Sleep(300);

        Assert.Equal(1, partials);
    }

    [Fact]
    public void InvalidAudioFormat_RaisesRecognitionFailed()
    {
        SegmentDecoder decoder = (_, _) => Array.Empty<TranscriptSegment>();
        using var engine = new WhisperSpeechToTextEngine(TestOptions(), decoder);
        SpeechRecognitionError? error = null;
        engine.RecognitionFailed += (_, e) => error = e;
        engine.Start();

        var stereo = new AudioChunk(new float[Rate], new AudioFormat(Rate, 2, 32), Base, 1);
        engine.Process(stereo);

        WaitUntil(() => error is not null);
        Assert.Equal(SpeechRecognitionErrorKind.InvalidAudioFormat, error!.Kind);
    }

    [Fact]
    public void DecodeFailure_RaisesEngineFailed_AndStopsRecognizing()
    {
        SegmentDecoder decoder = (_, _) => throw new InvalidOperationException("decode crashed");
        using var engine = new WhisperSpeechToTextEngine(TestOptions(), decoder);
        SpeechRecognitionError? error = null;
        engine.RecognitionFailed += (_, e) => error = e;
        engine.Start();

        engine.Process(Chunk(1));

        WaitUntil(() => error is not null);
        Assert.Equal(SpeechRecognitionErrorKind.EngineFailed, error!.Kind);
        Assert.False(engine.IsRecognizing);
    }

    [Fact]
    public void MissingModel_RaisesModelNotFound_OnStart()
    {
        var options = new WhisperEngineOptions
        {
            ModelPath = Path.Combine(Path.GetTempPath(), "does-not-exist-ggml.bin"),
        };
        using var engine = new WhisperSpeechToTextEngine(options);
        SpeechRecognitionError? error = null;
        engine.RecognitionFailed += (_, e) => error = e;

        engine.Start();

        WaitUntil(() => error is not null);
        Assert.Equal(SpeechRecognitionErrorKind.ModelNotFound, error!.Kind);
        Assert.False(engine.IsRecognizing);
    }

    [Fact]
    public void StabilityWindowBelowTwo_IsRejectedAtConstruction()
    {
        var options = new WhisperEngineOptions
        {
            ModelPath = "unused.bin",
            StabilityWindow = 1,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new WhisperSpeechToTextEngine(options));
    }
}
