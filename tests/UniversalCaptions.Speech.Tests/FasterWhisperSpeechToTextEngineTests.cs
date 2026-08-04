using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Speech;

namespace UniversalCaptions.Speech.Tests;

/// <summary>
/// Verifies the <see cref="FasterWhisperSpeechToTextEngine"/> wire-up: it reuses the shared streaming
/// orchestration (windowing, trimming, commit) with a decoder injected in place of whisper.cpp, so
/// faster-whisper results flow through the same partial/stable/final pipeline as ggml-base.
/// </summary>
public sealed class FasterWhisperSpeechToTextEngineTests
{
    private const int Rate = 16_000;
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static FasterWhisperEngineOptions TestOptions(TimeSpan? boundaryWaitBudget = null) => new()
    {
        PythonExecutablePath = "python",
        SampleRate = Rate,
        WindowDuration = TimeSpan.FromSeconds(2.0),
        DecodeInterval = TimeSpan.FromSeconds(0.5),
        MinimumAudioBeforeFirstDecode = TimeSpan.FromSeconds(0.5),
        CommitOverlap = TimeSpan.FromSeconds(0.2),
        StabilityWindow = 2,
        BoundaryWaitBudget = boundaryWaitBudget ?? TimeSpan.FromSeconds(2),
        Language = "tl",
    };

    private static AudioChunk Chunk(long sequence) =>
        new(new float[Rate / 2], new AudioFormat(Rate, 1, 32), Base + TimeSpan.FromSeconds(0.5 * (sequence - 1)), sequence);

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
    private static ScriptedDecoder ScriptedDecoderFactory(params string[] windowTexts)
    {
        var script = new Queue<string>(windowTexts);
        return new ScriptedDecoder(() => script.Count > 0
            ? [new TranscriptSegment(script.Dequeue(), TimeSpan.Zero, TimeSpan.FromSeconds(0.5))]
            : []);
    }

    /// <summary>
    /// Builds a decoder whose windows carry explicit segment lists, so completed segment boundaries
    /// exist within each window (required by the Option B boundary-preserving fallback).
    /// </summary>
    private static ScriptedDecoder ScriptedSegmentDecoderFactory(params string[][] windowSegments)
    {
        var script = new Queue<string[]>(windowSegments);
        return new ScriptedDecoder(() => script.Count > 0
            ? script.Dequeue().Select(t => new TranscriptSegment(t, TimeSpan.Zero, TimeSpan.FromSeconds(0.5))).ToArray()
            : []);
    }

    [Fact]
    public void EmitsPartialsThenFinals_AsStableTextIsConfirmed()
    {
        var decoder = ScriptedSegmentDecoderFactory(
            ["Today we're going "],
            ["Today we're going ", "to discuss "],
            ["Today we're going ", "to discuss ", "the budget "],
            ["Today we're going ", "to discuss ", "the budget "]);
        using var engine = new FasterWhisperSpeechToTextEngine(TestOptions(TimeSpan.Zero), decoder);
        var finals = new List<string>();
        var partials = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);
        engine.PartialTranscriptAvailable += (_, t) => partials.Add(t.Text);
        Assert.False(engine.IsRecognizing);

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
        var decoder = ScriptedSegmentDecoderFactory(
            ["today we're going "],
            ["tonight we're going "],
            ["tonight we're going ", "to "],
            ["tonight we're going ", "to "]);
        using var engine = new FasterWhisperSpeechToTextEngine(TestOptions(TimeSpan.Zero), decoder);
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
    public void Start_ThenStop_IsIdempotent_AndNoFurtherTranscripts()
    {
        var decoder = ScriptedSegmentDecoderFactory(["hello ", "hello "], ["hello "]);
        using var engine = new FasterWhisperSpeechToTextEngine(TestOptions(), decoder);
        var finals = new List<string>();
        var partials = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);
        engine.PartialTranscriptAvailable += (_, t) => partials.Add(t.Text);
        Assert.False(engine.IsRecognizing);

        engine.Start();
        Assert.True(engine.IsRecognizing);
        engine.Process(Chunk(1));
        WaitUntil(() => partials.Count == 1);
        engine.Process(Chunk(2));
        WaitUntil(() => finals.Count == 1);
        engine.Stop();
        engine.Stop();
        Assert.False(engine.IsRecognizing);

        int count = finals.Count;
        engine.Process(Chunk(5));
        engine.Process(Chunk(6));
        Thread.Sleep(300);

        Assert.Equal(count, finals.Count);
    }

    [Fact]
    public void DecodeFailure_RaisesEngineFailed_AndStopsRecognizing()
    {
        var decoder = new ThrowingDecoder(new InvalidOperationException("worker decode crashed"));
        using var engine = new FasterWhisperSpeechToTextEngine(TestOptions(), decoder);
        SpeechRecognitionError? error = null;
        engine.RecognitionFailed += (_, e) => error = e;
        engine.Start();

        engine.Process(Chunk(1));

        WaitUntil(() => error is not null);
        Assert.Equal(SpeechRecognitionErrorKind.EngineFailed, error!.Kind);
        Assert.False(engine.IsRecognizing);
    }

    [Fact]
    public void Restart_ResetsCommitState()
    {
        var decoder = ScriptedSegmentDecoderFactory(["hello ", "hello "], ["hello "]);
        using var engine = new FasterWhisperSpeechToTextEngine(TestOptions(), decoder);
        var finals = new List<string>();
        var partials = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);
        engine.PartialTranscriptAvailable += (_, t) => partials.Add(t.Text);

        engine.Start();
        engine.Process(Chunk(1));
        WaitUntil(() => partials.Count == 1);
        engine.Process(Chunk(2));
        WaitUntil(() => finals.Count == 1);
        engine.Stop();

        var decoder2 = ScriptedSegmentDecoderFactory(["world ", "world "], ["world "]);
        using var engine2 = new FasterWhisperSpeechToTextEngine(TestOptions(), decoder2);
        var finals2 = new List<string>();
        var partials2 = new List<string>();
        engine2.FinalTranscriptAvailable += (_, t) => finals2.Add(t.Text);
        engine2.PartialTranscriptAvailable += (_, t) => partials2.Add(t.Text);
        engine2.Start();
        engine2.Process(Chunk(4));
        WaitUntil(() => partials2.Count == 1);
        engine2.Process(Chunk(5));
        WaitUntil(() => finals2.Count == 1);
        engine2.Stop();

        Assert.Equal(["hello "], finals);
        Assert.Equal(["world "], finals2);
    }

    private sealed class ScriptedDecoder : ISTTDecoder
    {
        private readonly Func<IReadOnlyList<TranscriptSegment>> _decode;

        public ScriptedDecoder(Func<IReadOnlyList<TranscriptSegment>> decode) => _decode = decode;

        public int DecodeCalls { get; private set; }

        public void EnsureReady()
        {
        }

        public IReadOnlyList<TranscriptSegment> Decode(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
        {
            DecodeCalls++;
            return _decode();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingDecoder : ISTTDecoder
    {
        private readonly Exception _exception;

        public ThrowingDecoder(Exception exception) => _exception = exception;

        public void EnsureReady()
        {
        }

        public IReadOnlyList<TranscriptSegment> Decode(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
            => throw _exception;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
