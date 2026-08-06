using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Processing;
using UniversalCaptions.Core.Speech;

namespace UniversalCaptions.Speech.Tests;

/// <summary>
/// Verifies <see cref="FasterWhisperNativeStreamingEngine"/> against scripted VAD decisions and a
/// scripted worker process (no Python/model). One completed speech segment must produce exactly one
/// FINAL; no live partials are emitted; Stop must flush the in-progress segment and drain queued
/// segments so nothing is dropped.
/// </summary>
public sealed class FasterWhisperNativeStreamingEngineTests
{
    private const int Rate = 16_000;
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static FasterWhisperEngineOptions TestOptions() => new()
    {
        PythonExecutablePath = "python",
        SampleRate = Rate,
        Language = "tl",
    };

    private static FasterWhisperEngineOptions PartialOptions(TimeSpan? interval = null, TimeSpan? window = null) => new()
    {
        PythonExecutablePath = "python",
        SampleRate = Rate,
        Language = "tl",
        PartialDecodeInterval = interval ?? TimeSpan.FromSeconds(0.5),
        PartialDecodeWindow = window ?? TimeSpan.FromSeconds(2),
    };

    private static SpeechSegmentDetectorOptions DetectorOptions() => new() { SampleRate = Rate };

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

    [Fact]
    public void EmitsOneFinalPerCompletedSegment_NoLivePartials()
    {
        var vad = new ScriptedVad([true, false, false, false, true, false, false, false]);
        var process = new ScriptedFasterWhisperProcess(["first sentence", "second sentence"]);
        using var engine = new FasterWhisperNativeStreamingEngine(TestOptions(), process, vad, DetectorOptions());
        var finals = new List<FinalTranscript>();
        var partials = new List<PartialTranscript>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t);
        engine.PartialTranscriptAvailable += (_, t) => partials.Add(t);

        engine.Start();
        for (int i = 1; i <= 8; i++)
        {
            engine.Process(Chunk(i));
        }

        WaitUntil(() => finals.Count == 2);

        Assert.Equal(["first sentence", "second sentence"], finals.Select(f => f.Text).ToArray());
        Assert.Equal(Base, finals[0].CapturedAtUtc);
        Assert.Equal(Base + TimeSpan.FromSeconds(2), finals[1].CapturedAtUtc);
        Assert.Empty(partials);
        Assert.Equal(2, process.ReceivedPcm.Count);
        Assert.Equal(4 * Rate / 2, process.ReceivedPcm[0].Length);
        Assert.Equal(4 * Rate / 2, process.ReceivedPcm[1].Length);
        Assert.All(process.ReceivedLanguages, lang => Assert.Equal("tl", lang));
    }

    [Fact]
    public void PcmConversion_ClampsToInt16Range()
    {
        var vad = new ScriptedVad([true, false, false, false]);
        var process = new ScriptedFasterWhisperProcess(["okay"]);
        using var engine = new FasterWhisperNativeStreamingEngine(TestOptions(), process, vad, DetectorOptions());
        var hotChunk = new float[Rate / 2];
        hotChunk[0] = 2.0f;
        hotChunk[1] = -2.0f;
        hotChunk[2] = 0.5f;

        engine.Start();
        engine.Process(new AudioChunk(hotChunk, new AudioFormat(Rate, 1, 32), Base, 1));
        for (int i = 2; i <= 4; i++)
        {
            engine.Process(Chunk(i));
        }

        WaitUntil(() => process.ReceivedPcm.Count == 1);

        Assert.Equal(short.MaxValue, process.ReceivedPcm[0][0]);
        Assert.Equal(short.MinValue, process.ReceivedPcm[0][1]);
        Assert.Equal((short)(0.5f * short.MaxValue), process.ReceivedPcm[0][2]);
    }

    [Fact]
    public void Stop_FlushesInProgressSegment_SoItIsNotDropped()
    {
        var vad = new ScriptedVad([true, true, false]);
        var process = new ScriptedFasterWhisperProcess(["final words"]);
        using var engine = new FasterWhisperNativeStreamingEngine(TestOptions(), process, vad, DetectorOptions());
        var finals = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);

        engine.Start();
        engine.Process(Chunk(1));
        engine.Process(Chunk(2));
        engine.Process(Chunk(3));
        engine.Stop();

        Assert.Equal(["final words"], finals);
        Assert.False(engine.IsRecognizing);
    }

    [Fact]
    public void Stop_DrainsSegmentsAlreadyQueued()
    {
        var vad = new ScriptedVad([true, false, false, false, true, false, false, false]);
        var process = new ScriptedFasterWhisperProcess(["one", "two"]);
        using var engine = new FasterWhisperNativeStreamingEngine(TestOptions(), process, vad, DetectorOptions());
        var finals = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);

        engine.Start();
        for (int i = 1; i <= 8; i++)
        {
            engine.Process(Chunk(i));
        }

        engine.Stop();

        Assert.Equal(["one", "two"], finals);
        Assert.Equal(2, process.ReceivedPcm.Count);
    }

    [Fact]
    public void DecodeFailure_RaisesEngineFailed_AndStopsRecognizing()
    {
        var vad = new ScriptedVad([true, false, false, false]);
        var process = new ThrowingProcess(new FasterWhisperProcessException(
            FasterWhisperErrorKind.EngineFailed, "worker decode crashed"));
        using var engine = new FasterWhisperNativeStreamingEngine(TestOptions(), process, vad, DetectorOptions());
        SpeechRecognitionError? error = null;
        engine.RecognitionFailed += (_, e) => error = e;

        engine.Start();
        engine.Process(Chunk(1));
        engine.Process(Chunk(2));
        engine.Process(Chunk(3));
        engine.Process(Chunk(4));

        WaitUntil(() => error is not null);
        Assert.Equal(SpeechRecognitionErrorKind.EngineFailed, error!.Kind);
        Assert.False(engine.IsRecognizing);
    }

    [Fact]
    public void StartFailure_RaisesModelLoadFailed()
    {
        var vad = new ScriptedVad([true]);
        var process = new FailingStartProcess(new FasterWhisperProcessException(
            FasterWhisperErrorKind.EngineUnavailable, "python or model missing"));
        using var engine = new FasterWhisperNativeStreamingEngine(TestOptions(), process, vad, DetectorOptions());
        SpeechRecognitionError? error = null;
        engine.RecognitionFailed += (_, e) => error = e;

        engine.Start();

        Assert.NotNull(error);
        Assert.Equal(SpeechRecognitionErrorKind.ModelLoadFailed, error!.Kind);
        Assert.False(engine.IsRecognizing);
    }

    [Fact]
    public void Restart_AfterStop_ResetsSessionState()
    {
        var vad = new ScriptedVad([true, false, false, false]);
        var process = new ScriptedFasterWhisperProcess(["one", "two"]);
        using var engine = new FasterWhisperNativeStreamingEngine(TestOptions(), process, vad, DetectorOptions());
        var finals = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);

        engine.Start();
        for (int i = 1; i <= 4; i++)
        {
            engine.Process(Chunk(i));
        }

        WaitUntil(() => finals.Count == 1);
        engine.Stop();

        engine.Start();
        for (int i = 5; i <= 8; i++)
        {
            engine.Process(Chunk(i));
        }

        WaitUntil(() => finals.Count == 2);
        engine.Stop();

        Assert.Equal(["one", "two"], finals);
        Assert.Equal(2, process.StartCalls);
    }

    [Fact]
    public void InvalidFormat_RaisesInvalidAudioFormatOnce()
    {
        var vad = new ScriptedVad([true, false]);
        var process = new ScriptedFasterWhisperProcess();
        using var engine = new FasterWhisperNativeStreamingEngine(TestOptions(), process, vad, DetectorOptions());
        var errors = new List<SpeechRecognitionError>();
        engine.RecognitionFailed += (_, e) => errors.Add(e);
        var stereo = new float[Rate];
        var chunk = new AudioChunk(stereo, new AudioFormat(Rate, 2, 32), Base, 1);

        engine.Start();
        engine.Process(chunk);
        engine.Process(chunk);

        WaitUntil(() => errors.Count == 1);
        Assert.Equal(SpeechRecognitionErrorKind.InvalidAudioFormat, errors[0].Kind);
        Assert.Empty(process.ReceivedPcm);
    }

    [Fact]
    public void EmptyDecodedText_DoesNotEmitFinal()
    {
        var vad = new ScriptedVad([true, false, false, false]);
        var process = new ScriptedFasterWhisperProcess();
        using var engine = new FasterWhisperNativeStreamingEngine(TestOptions(), process, vad, DetectorOptions());
        var finals = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);

        engine.Start();
        for (int i = 1; i <= 4; i++)
        {
            engine.Process(Chunk(i));
        }

        WaitUntil(() => process.ReceivedPcm.Count == 1);
        Thread.Sleep(200);

        Assert.Empty(finals);
    }

    [Fact]
    public void Process_WhenNotStartedOrAfterStop_IsIgnored()
    {
        var vad = new ScriptedVad([true, false, false, false]);
        var process = new ScriptedFasterWhisperProcess(["one"]);
        using var engine = new FasterWhisperNativeStreamingEngine(TestOptions(), process, vad, DetectorOptions());
        var finals = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);

        engine.Process(Chunk(1));
        Assert.Empty(process.ReceivedPcm);

        engine.Start();
        for (int i = 1; i <= 4; i++)
        {
            engine.Process(Chunk(i));
        }

        WaitUntil(() => finals.Count == 1);
        engine.Stop();

        int count = finals.Count;
        engine.Process(Chunk(9));
        engine.Process(Chunk(10));
        Thread.Sleep(200);

        Assert.Equal(count, finals.Count);
        Assert.Single(process.ReceivedPcm);
    }

    [Fact]
    public void Start_WhenAlreadyStarted_IsIdempotent()
    {
        var vad = new ScriptedVad([true, false, false, false]);
        var process = new ScriptedFasterWhisperProcess(["one"]);
        using var engine = new FasterWhisperNativeStreamingEngine(TestOptions(), process, vad, DetectorOptions());
        var finals = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);

        engine.Start();
        engine.Start();
        Assert.Equal(1, process.StartCalls);

        for (int i = 1; i <= 4; i++)
        {
            engine.Process(Chunk(i));
        }

        WaitUntil(() => finals.Count == 1);
        engine.Stop();
    }

    [Fact]
    public void ContinuousSpeech_EmitsFinalsAtMaxSegmentDurationCap()
    {
        // Continuous speech with no pauses must still produce FINALs at the max-segment cap, so
        // captions never go stale during a long monologue.
        var vad = new ScriptedVad([true, true, true, true, true, true, true, true]);
        var process = new ScriptedFasterWhisperProcess(["one", "two", "three"]);
        var detectorOptions = new SpeechSegmentDetectorOptions
        {
            SampleRate = Rate,
            MaxSegmentDuration = TimeSpan.FromSeconds(1.0),
        };
        using var engine = new FasterWhisperNativeStreamingEngine(TestOptions(), process, vad, detectorOptions);
        var finals = new List<string>();
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t.Text);

        engine.Start();
        for (int i = 1; i <= 6; i++)
        {
            engine.Process(Chunk(i));
        }

        WaitUntil(() => finals.Count == 3);

        Assert.Equal(["one", "two", "three"], finals);
        // 0.5 s chunks: a segment closes every 2 chunks at the 1.0 s cap, so each is 1.0 s = Rate samples.
        Assert.All(process.ReceivedPcm, pcm => Assert.Equal(Rate, pcm.Length));
    }

    [Fact]
    public void EmitsLivePartialsDuringSpeech_ThenOneFinalAtSegmentEnd()
    {
        // Chrome-style behavior: partials appear while the speaker is still talking, one per cadence
        // tick, and the completed segment still yields exactly one FINAL after the hangover.
        var vad = new ScriptedVad([true, true, true, false, false, false, false]);
        var process = new ScriptedFasterWhisperProcess(["mga", "mga anak", "mga anak ko", "mga anak ko kumakain"]);
        using var engine = new FasterWhisperNativeStreamingEngine(PartialOptions(), process, vad, DetectorOptions());
        var partials = new List<PartialTranscript>();
        var finals = new List<FinalTranscript>();
        engine.PartialTranscriptAvailable += (_, t) => partials.Add(t);
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t);

        engine.Start();
        engine.Process(Chunk(1));
        WaitUntil(() => partials.Count == 1);
        engine.Process(Chunk(2));
        WaitUntil(() => partials.Count == 2);
        engine.Process(Chunk(3));
        WaitUntil(() => partials.Count == 3);
        engine.Process(Chunk(4));
        engine.Process(Chunk(5));
        engine.Process(Chunk(6));
        WaitUntil(() => finals.Count == 1);

        Assert.Equal(["mga", "mga anak", "mga anak ko"], partials.Select(p => p.Text).ToArray());
        Assert.Equal(["mga anak ko kumakain"], finals.Select(f => f.Text).ToArray());
        // The 2 s partial window covers the whole buffer while speech is under 1.5 s, so every
        // partial's window still starts at the segment start (advancing window starts are covered by
        // the detector's TryGetPartial tests).
        Assert.Equal(Base, partials[0].CapturedAtUtc);
        Assert.Equal(Base, partials[1].CapturedAtUtc);
        Assert.Equal(Base, partials[2].CapturedAtUtc);
        Assert.Equal(Base, finals[0].CapturedAtUtc);

        // Partial decode windows grow: 1, 2, then 3 chunks (uncapped by the 2 s window), then the
        // full segment (1 speech chunk + 5 hangover/silence chunks) as the single FINAL.
        Assert.Equal([Rate / 2, Rate, 3 * Rate / 2, 3 * Rate], process.ReceivedPcm.Select(p => p.Length).ToArray());
    }

    [Fact]
    public void PartialDecode_BoundsWindowToConfiguredWindow()
    {
        // The partial decode window is capped so a live partial never grows unboundedly; the FINAL
        // decode is never windowed.
        var vad = new ScriptedVad([true, true, true, false, false, false, false]);
        var process = new ScriptedFasterWhisperProcess(["a", "ab", "abc", "final"]);
        using var engine = new FasterWhisperNativeStreamingEngine(
            PartialOptions(interval: TimeSpan.FromSeconds(0.5), window: TimeSpan.FromSeconds(1)), process, vad, DetectorOptions());
        var partials = new List<PartialTranscript>();
        var finals = new List<FinalTranscript>();
        engine.PartialTranscriptAvailable += (_, t) => partials.Add(t);
        engine.FinalTranscriptAvailable += (_, t) => finals.Add(t);

        engine.Start();
        engine.Process(Chunk(1));
        WaitUntil(() => partials.Count == 1);
        engine.Process(Chunk(2));
        WaitUntil(() => partials.Count == 2);
        engine.Process(Chunk(3));
        WaitUntil(() => partials.Count == 3);
        engine.Process(Chunk(4));
        engine.Process(Chunk(5));
        engine.Process(Chunk(6));
        WaitUntil(() => finals.Count == 1);

        // partial #1 = 1 chunk, #2 = 2 chunks, #3 would be 3 chunks but the 1 s window caps it at 2.
        Assert.Equal([Rate / 2, Rate, Rate], process.ReceivedPcm.Take(3).Select(p => p.Length).ToArray());
        // The FINAL is the full segment (speech + hangover/silence), never windowed.
        Assert.Equal(3 * Rate, process.ReceivedPcm[3].Length);
    }

    [Fact]
    public void NoPartialsWhenIdle_EvenWithCadenceConfigured()
    {
        var vad = new ScriptedVad([false, false, false, false]);
        var process = new ScriptedFasterWhisperProcess();
        using var engine = new FasterWhisperNativeStreamingEngine(PartialOptions(), process, vad, DetectorOptions());
        var partials = new List<PartialTranscript>();
        engine.PartialTranscriptAvailable += (_, t) => partials.Add(t);

        engine.Start();
        for (int i = 1; i <= 4; i++)
        {
            engine.Process(Chunk(i));
        }

        WaitUntil(() => process.ReceivedPcm.Count == 0);
        Thread.Sleep(100);

        Assert.Empty(partials);
        Assert.Empty(process.ReceivedPcm);
    }

    [Fact]
    public void PartialDecodes_AreBoundedToOneInFlightOrQueued()
    {
        // A slow worker must not accumulate a growing partial backlog: cadence ticks that land while
        // a partial decode is in flight are dropped, not queued (acceptance: no growing backlog).
        var vad = new ScriptedVad([true, true, true, true]);
        var process = new BlockingFasterWhisperProcess(["one", "two"]);
        using var engine = new FasterWhisperNativeStreamingEngine(PartialOptions(), process, vad, DetectorOptions());
        var partials = new List<PartialTranscript>();
        engine.PartialTranscriptAvailable += (_, t) => partials.Add(t);

        engine.Start();
        engine.Process(Chunk(1));
        WaitUntil(() => process.Started == 1);
        engine.Process(Chunk(2));
        engine.Process(Chunk(3));
        Thread.Sleep(100);

        // Only the first partial was dispatched; the later cadence ticks were dropped.
        Assert.Equal(1, process.Started);
        Assert.Empty(partials);

        process.Release();
        WaitUntil(() => partials.Count == 1);

        // After the in-flight partial clears, the accumulated cadence fires the next partial.
        engine.Process(Chunk(4));
        WaitUntil(() => partials.Count == 2);

        Assert.Equal(["one", "two"], partials.Select(p => p.Text).ToArray());
        Assert.Equal(2, process.Started);
    }

    private sealed class ScriptedVad : IVoiceActivityDetector
    {
        private readonly IReadOnlyList<bool> _script;
        private int _index;

        public ScriptedVad(IReadOnlyList<bool> script) => _script = script;

        public bool IsSpeech(AudioChunk chunk)
        {
            bool value = _index < _script.Count ? _script[_index] : false;
            _index++;
            return value;
        }

        public void Reset() => _index = 0;
    }

    private sealed class ScriptedFasterWhisperProcess : IFasterWhisperProcess
    {
        private readonly Queue<IReadOnlyList<TranscriptSegment>> _results;

        public ScriptedFasterWhisperProcess(params string[] texts)
        {
            _results = new Queue<IReadOnlyList<TranscriptSegment>>(
                texts.Select(t => (IReadOnlyList<TranscriptSegment>)new[]
                {
                    new TranscriptSegment(t, TimeSpan.Zero, TimeSpan.FromSeconds(0.5)),
                }));
        }

        public List<short[]> ReceivedPcm { get; } = new();

        public List<string?> ReceivedLanguages { get; } = new();

        public int StartCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            ReadOnlyMemory<short> pcmSamples,
            string? language,
            CancellationToken cancellationToken)
        {
            ReceivedPcm.Add(pcmSamples.ToArray());
            ReceivedLanguages.Add(language);
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : Array.Empty<TranscriptSegment>());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingFasterWhisperProcess : IFasterWhisperProcess
    {
        private readonly Queue<IReadOnlyList<TranscriptSegment>> _results;
        private readonly ManualResetEventSlim _release = new(false);
        private int _started;

        public BlockingFasterWhisperProcess(params string[] texts)
        {
            _results = new Queue<IReadOnlyList<TranscriptSegment>>(
                texts.Select(t => (IReadOnlyList<TranscriptSegment>)new[]
                {
                    new TranscriptSegment(t, TimeSpan.Zero, TimeSpan.FromSeconds(0.5)),
                }));
        }

        public int Started => _started;

        public void Release() => _release.Set();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            ReadOnlyMemory<short> pcmSamples,
            string? language,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _started) == 1)
            {
                _release.Wait();
            }

            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : Array.Empty<TranscriptSegment>());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingProcess : IFasterWhisperProcess
    {
        private readonly Exception _exception;

        public ThrowingProcess(Exception exception) => _exception = exception;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            ReadOnlyMemory<short> pcmSamples,
            string? language,
            CancellationToken cancellationToken)
            => throw _exception;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingStartProcess : IFasterWhisperProcess
    {
        private readonly Exception _exception;

        public FailingStartProcess(Exception exception) => _exception = exception;

        public Task StartAsync(CancellationToken cancellationToken) => throw _exception;

        public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            ReadOnlyMemory<short> pcmSamples,
            string? language,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TranscriptSegment>>(Array.Empty<TranscriptSegment>());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
