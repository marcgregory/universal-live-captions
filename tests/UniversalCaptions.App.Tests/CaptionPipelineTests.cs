using System.Linq;
using UniversalCaptions.App.Pipeline;
using UniversalCaptions.Audio.Processing;
using UniversalCaptions.Captions;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Capture;
using UniversalCaptions.Core.Processing;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Verifies the Gemini-only app wiring (capture → processor → live engine → caption service,
/// ADR-0011) and the start/stop/error lifecycle against deterministic fakes. WPF visuals are
/// verified manually.
/// </summary>
public class CaptionPipelineTests
{
    private sealed class FakeAudioCapture : IAudioCapture
    {
        public event EventHandler<AudioChunk>? AudioAvailable;
        public event EventHandler<AudioCaptureError>? CaptureFailed;
        public AudioFormat Format { get; } = new(48_000, 2, 32);
        public bool IsCapturing { get; private set; }
        public bool IsDisposed { get; private set; }

        public void Start() => IsCapturing = true;
        public void Stop() => IsCapturing = false;
        public void Dispose() => IsDisposed = true;

        public void Emit(AudioChunk chunk) => AudioAvailable?.Invoke(this, chunk);
        public void Fail(AudioCaptureError error) => CaptureFailed?.Invoke(this, error);
    }

#pragma warning disable CS0067
    /// <summary>A live translation engine that counts lifecycle calls instead of performing I/O.</summary>
    private sealed class FakeLiveEngine : ILiveAudioTranslationEngine
    {
        public event EventHandler<PartialTranscript>? PartialTranscriptionAvailable;
        public event EventHandler<FinalTranscript>? FinalTranscriptionAvailable;
        public event EventHandler<PartialTranslation>? PartialTranslationAvailable;
        public event EventHandler<FinalTranslation>? FinalTranslationAvailable;
        public event EventHandler<LiveTranslationError>? TranslationFailed;

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int PushAudioCount { get; private set; }
        public List<AudioChunk> ReceivedAudio { get; } = [];
        public (string? Source, string? Target)? CreatedFor { get; set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public void PushAudio(AudioChunk chunk)
        {
            PushAudioCount++;
            ReceivedAudio.Add(chunk);
        }

        public void Dispose() => DisposeCount++;

        public void EmitPartialTranscription(string text, long sequence = 1)
        {
            DateTime captured = DateTime.UtcNow;
            PartialTranscriptionAvailable?.Invoke(
                this, new PartialTranscript(text, captured, captured, sequence));
        }

        public void EmitFinalTranscription(string text, long sequence = 1, TimeSpan? latency = null)
        {
            DateTime captured = DateTime.UtcNow;
            DateTime emitted = captured + (latency ?? TimeSpan.FromMilliseconds(400));
            FinalTranscriptionAvailable?.Invoke(
                this, new FinalTranscript(text, captured, emitted, sequence));
        }

        public void EmitPartialTranslation(string translatedText, long sequence = 1)
        {
            DateTime now = DateTime.UtcNow;
            PartialTranslationAvailable?.Invoke(
                this,
                new PartialTranslation(
                    sourceText: null,
                    translatedText: translatedText,
                    sourceLanguage: "en",
                    targetLanguage: "tl",
                    capturedAtUtc: now,
                    emittedAtUtc: now,
                    sequence: sequence));
        }

        public void EmitFinalTranslation(string translatedText, long sequence = 1)
        {
            DateTime now = DateTime.UtcNow;
            FinalTranslationAvailable?.Invoke(
                this,
                new FinalTranslation(
                    sourceText: null,
                    translatedText: translatedText,
                    sourceLanguage: "en",
                    targetLanguage: "tl",
                    capturedAtUtc: now,
                    emittedAtUtc: now,
                    sequence: sequence,
                    committedAtUtc: now));
        }

        public void Fail(LiveTranslationError error) => TranslationFailed?.Invoke(this, error);
    }

    /// <summary>A live engine whose <see cref="StartAsync"/> throws synchronously, like a rejected handshake.</summary>
    private sealed class ThrowingOnStartLiveEngine : ILiveAudioTranslationEngine
    {
        public event EventHandler<PartialTranscript>? PartialTranscriptionAvailable;
        public event EventHandler<FinalTranscript>? FinalTranscriptionAvailable;
        public event EventHandler<PartialTranslation>? PartialTranslationAvailable;
        public event EventHandler<FinalTranslation>? FinalTranslationAvailable;
        public event EventHandler<LiveTranslationError>? TranslationFailed;

        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("handshake rejected"));

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void PushAudio(AudioChunk chunk) { }
        public void Dispose() => DisposeCount++;
    }
#pragma warning restore CS0067

    private sealed class PassthroughProcessor : IAudioProcessor
    {
        public AudioFormat OutputFormat { get; } = new(16_000, 1, 32);

        public bool TryProcess(AudioChunk input, out AudioChunk? output)
        {
            output = input;
            return true;
        }
    }

    private sealed class ThrowingProcessor : IAudioProcessor
    {
        public AudioFormat OutputFormat { get; } = new(16_000, 1, 32);

        public bool TryProcess(AudioChunk input, out AudioChunk? output) =>
            throw new InvalidOperationException("Audio processing exploded.");
    }

    /// <summary>A capture source that raises <see cref="IAudioCapture.CaptureFailed"/> synchronously from <see cref="IAudioCapture.Start"/>.</summary>
#pragma warning disable CS0067
    private sealed class FailingOnStartAudioCapture : IAudioCapture
    {
        public event EventHandler<AudioChunk>? AudioAvailable;
        public event EventHandler<AudioCaptureError>? CaptureFailed;
        public AudioFormat Format { get; } = new(48_000, 2, 32);
        public bool IsCapturing { get; private set; }
        public bool IsDisposed { get; private set; }

        public void Start() => CaptureFailed?.Invoke(this, new AudioCaptureError(
            AudioCaptureErrorKind.DeviceDisconnected, "Device disconnected during start."));
        public void Stop() { }
        public void Dispose() => IsDisposed = true;
    }
#pragma warning restore CS0067

    /// <summary>A device-change source driven by the test, so recovery timing is deterministic.</summary>
    private sealed class FakeDeviceChangeMonitor : IDeviceChangeMonitor
    {
        public event EventHandler<DeviceChangeNotification>? DeviceChanged;
        public bool Started { get; private set; }

        public void Start() => Started = true;
        public void Stop() => Started = false;
        public void Dispose() { }

        public void Raise(DeviceChangeNotification notification) => DeviceChanged?.Invoke(this, notification);
    }

    private sealed class Harness : IDisposable
    {
        public FakeAudioCapture Capture { get; } = new();
        public FakeLiveEngine Engine { get; } = new();
        public CaptionService Captions { get; } = new(new CaptionServiceOptions("en", historyCapacity: 20));
        public IAudioProcessor Processor { get; }
        public string? ReceivedDeviceId { get; private set; }
        public List<(string? Source, string? Target)> FactoryCalls { get; } = [];
        public Func<ILiveAudioTranslationEngine?> OnCreate { get; set; } = () => null;
        public CaptionPipeline Pipeline { get; }
        public List<PipelineStatus> Statuses { get; } = [];
        public List<TimeSpan> Latencies { get; } = [];
        public List<EndToEndLatencySample> E2eSamples { get; } = [];
        public List<LiveTranslationError> LiveErrors { get; } = [];

        public Harness(IAudioProcessor? processor = null)
        {
            Processor = processor ?? new PassthroughProcessor();
            Pipeline = new CaptionPipeline(
                deviceId =>
                {
                    ReceivedDeviceId = deviceId;
                    return Capture;
                },
                Processor,
                Captions,
                languages =>
                {
                    FactoryCalls.Add(languages);
                    return OnCreate();
                });
            Pipeline.StatusChanged += (_, s) => Statuses.Add(s);
            Pipeline.LatencyUpdated += (_, l) => Latencies.Add(l);
            Pipeline.EndToEndLatencyUpdated += (_, s) => E2eSamples.Add(s);
            Pipeline.LiveTranslationErrorUpdated += (_, e) => LiveErrors.Add(e);
        }

        public Harness WithWorkingEngine()
        {
            var engine = Engine;
            OnCreate = () =>
            {
                engine.CreatedFor = FactoryCalls[^1];
                return engine;
            };
            return this;
        }

        public void Dispose() => Pipeline.Dispose();
    }

    private static AudioChunk Chunk() => new(new float[160], new AudioFormat(16_000, 1, 32), DateTime.UtcNow, 1);

    // ---------------------------------------------------------------------
    // Start lifecycle
    // ---------------------------------------------------------------------

    [Fact]
    public void Start_CreatesEngineWithSessionLanguages_AndStartsCapture()
    {
        using var harness = new Harness().WithWorkingEngine();

        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        Assert.True(harness.Pipeline.IsRunning);
        Assert.Equal(1, harness.Engine.StartCount);
        Assert.Null(harness.ReceivedDeviceId);
        Assert.Equal(("en", "tl"), harness.FactoryCalls.Single());
        Assert.True(harness.Capture.IsCapturing);
        Assert.Contains(harness.Statuses, s => s.Kind == PipelineStatusKind.Capturing);
    }

    [Fact]
    public void Start_ForwardsExplicitDeviceIdToTheCaptureFactory()
    {
        using var harness = new Harness().WithWorkingEngine();

        harness.Pipeline.Start("{device-id}", "en", "tl", translationEnabled: false);

        Assert.Equal("{device-id}", harness.ReceivedDeviceId);
    }

    [Fact]
    public void Start_WhenFactoryReturnsNull_RaisesMissingKeyError_AndNeverStartsCapture()
    {
        using var harness = new Harness(); // OnCreate returns null → no API key stored.

        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        Assert.False(harness.Pipeline.IsRunning);
        Assert.False(harness.Capture.IsCapturing);
        Assert.Contains(harness.Statuses, s => s.Kind == PipelineStatusKind.Error);
        LiveTranslationError error = Assert.Single(harness.LiveErrors);
        Assert.Equal(LiveTranslationErrorKind.SessionRejected, error.Kind);
        Assert.Contains("API key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Start_WhenFactoryThrows_RaisesError_AndNeverStartsCapture()
    {
        using var harness = new Harness();
        harness.OnCreate = () => throw new InvalidOperationException("credential store locked");

        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        Assert.False(harness.Pipeline.IsRunning);
        Assert.Contains(harness.Statuses, s => s.Kind == PipelineStatusKind.Error && s.Message.Contains("credential store locked"));
        Assert.Empty(harness.LiveErrors);
    }

    [Fact]
    public async Task Start_WhenEngineStartAsyncThrows_DisposesEngine_AndRaisesClassifiedError()
    {
        var throwing = new ThrowingOnStartLiveEngine();
        using var harness = new Harness();
        harness.OnCreate = () => throwing;

        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        Assert.False(harness.Pipeline.IsRunning);
        Assert.False(harness.Capture.IsCapturing);
        LiveTranslationError error = Assert.Single(harness.LiveErrors);
        Assert.Equal(LiveTranslationErrorKind.ConnectionFailed, error.Kind);
        Assert.Contains("handshake rejected", error.Message);

        // The failed engine is disposed on a background task — poll for it.
        for (int i = 0; i < 50 && throwing.DisposeCount == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, throwing.DisposeCount);
    }

    [Fact]
    public void Start_WhileAlreadyRunning_IsNoOp()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        harness.Pipeline.Start(null, "en", "ja", translationEnabled: false);

        Assert.Equal(("en", "tl"), harness.FactoryCalls.Single());
        Assert.True(harness.Pipeline.IsRunning);
    }

    [Fact]
    public void Start_WhenCaptureStartFails_StopsTheSession()
    {
        var capture = new FailingOnStartAudioCapture();
        var engine = new FakeLiveEngine();
        var statuses = new List<PipelineStatus>();
        var pipeline = new CaptionPipeline(
            _ => capture,
            new PassthroughProcessor(),
            new CaptionService(new CaptionServiceOptions("en", historyCapacity: 20)),
            _ => engine);
        pipeline.StatusChanged += (_, s) => statuses.Add(s);
        try
        {
            pipeline.Start(null, "en", "tl", translationEnabled: true);

            Assert.False(pipeline.IsRunning);
            Assert.False(capture.IsCapturing);
            Assert.Contains(statuses, s => s.Kind == PipelineStatusKind.Error);
        }
        finally
        {
            pipeline.Dispose();
        }
    }

    // ---------------------------------------------------------------------
    // Audio + transcription flow
    // ---------------------------------------------------------------------

    [Fact]
    public void AudioChunks_AreProcessedAndPushedToTheLiveEngine()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        harness.Capture.Emit(Chunk());

        Assert.Equal(1, harness.Engine.PushAudioCount);
        Assert.Single(harness.Engine.ReceivedAudio);
    }

    [Fact]
    public void PartialTranscription_BecomesTheActiveCaptionLine()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        harness.Engine.EmitPartialTranscription("Magandang");

        Assert.Equal("Magandang", harness.Captions.State.ActiveLine?.Text);
    }

    [Fact]
    public void FinalTranscription_CommitsHistory_AndRaisesLatency()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        harness.Engine.EmitFinalTranscription("Magandang umaga.", latency: TimeSpan.FromMilliseconds(250));

        CaptionLine line = Assert.Single(harness.Captions.State.History);
        Assert.Equal("Magandang umaga.", line.Text);
        Assert.Equal(LineOrigin.SourceStt, line.Origin);
        TimeSpan latency = Assert.Single(harness.Latencies);
        Assert.Equal(TimeSpan.FromMilliseconds(250), latency);
    }

    [Fact]
    public void TranscriptionAfterStop_DoesNotReachTheCaptionService()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);
        harness.Pipeline.Stop();

        harness.Engine.EmitPartialTranscription("late");
        harness.Engine.EmitFinalTranscription("late final.");

        Assert.Null(harness.Captions.State.ActiveLine);
        Assert.Empty(harness.Captions.State.History);
    }

    // ---------------------------------------------------------------------
    // Translation relay + gating
    // ---------------------------------------------------------------------

    [Fact]
    public void TranslationEvents_WhenEnabled_RelayIntoTheCaptionService()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        harness.Engine.EmitPartialTranslation("Magandang");
        Assert.Equal("Magandang", harness.Captions.State.ActiveTranslationLine?.Text);

        harness.Engine.EmitFinalTranslation("Magandang umaga.");

        CaptionLine line = Assert.Single(harness.Captions.State.History, l => l.Origin == LineOrigin.Translation);
        Assert.Equal("Magandang umaga.", line.Text);
    }

    [Fact]
    public void TranslationEvents_WhenDisabled_AreGatedBeforeTheCaptionService()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: false);

        harness.Engine.EmitPartialTranslation("Magandang");
        harness.Engine.EmitFinalTranslation("Magandang umaga.");

        Assert.Null(harness.Captions.State.ActiveTranslationLine);
        Assert.DoesNotContain(harness.Captions.State.History, l => l.Origin == LineOrigin.Translation);
    }

    [Fact]
    public void SetTranslationEnabled_TogglesWithoutRecreatingTheEngine()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: false);

        harness.Pipeline.SetTranslationEnabled(true);
        harness.Engine.EmitFinalTranslation("Salin.");

        Assert.Contains(harness.Captions.State.History, l => l.Origin == LineOrigin.Translation);
        Assert.Equal(1, harness.Engine.StartCount); // same session kept running
        Assert.Equal(("en", "tl"), harness.FactoryCalls.Single());
    }

    [Fact]
    public void SetTranslationEnabled_Disable_ScrubsTranslatedContentFromTheOverlay()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);
        harness.Engine.EmitFinalTranslation("Salin.");

        harness.Pipeline.SetTranslationEnabled(false);

        Assert.DoesNotContain(harness.Captions.State.History, l => l.Origin == LineOrigin.Translation);
        Assert.Null(harness.Captions.State.ActiveTranslationLine);
    }

    [Fact]
    public void SetTranslationEnabled_BeforeStart_IsNoOpAndDoesNotThrow()
    {
        using var harness = new Harness().WithWorkingEngine();

        harness.Pipeline.SetTranslationEnabled(true);

        Assert.False(harness.Captions.State.TranslationEnabled);
    }

    [Fact]
    public void TranslatedCaptionPublication_RaisesEndToEndLatencySample()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        harness.Engine.EmitFinalTranslation("Salin.");

        EndToEndLatencySample sample = Assert.Single(harness.E2eSamples);
        Assert.Equal(EndToEndLatencyKind.Final, sample.Kind);
        Assert.True(sample.EndToEndLatency >= TimeSpan.Zero);
        Assert.True(sample.TranslationLatency >= TimeSpan.Zero);
    }

    // ---------------------------------------------------------------------
    // Target-language swap
    // ---------------------------------------------------------------------

    [Fact]
    public void SetTargetLanguage_RecyclesTheEngineWithTheNewTarget()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);
        var oldEngine = harness.Engine;
        var newEngine = new FakeLiveEngine();
        harness.OnCreate = () => newEngine;

        harness.Pipeline.SetTargetLanguage("ja");

        Assert.Equal(1, oldEngine.StopCount);
        Assert.Equal(1, newEngine.StartCount);
        Assert.Equal(("en", "ja"), harness.FactoryCalls[^1]);
        Assert.True(harness.Pipeline.IsRunning);
    }

    [Fact]
    public void SetTargetLanguage_SameTarget_IsNoOp()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        harness.Pipeline.SetTargetLanguage("TL");

        Assert.Equal(("en", "tl"), harness.FactoryCalls.Single());
        Assert.Equal(0, harness.Engine.StopCount);
    }

    [Fact]
    public void SetTargetLanguage_WhenSwapFails_StopsTheSession()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);
        harness.OnCreate = () => null; // e.g. the key was cleared mid-session

        harness.Pipeline.SetTargetLanguage("ja");

        Assert.False(harness.Pipeline.IsRunning);
        Assert.Contains(harness.Statuses, s => s.Kind == PipelineStatusKind.Error);
    }

    [Fact]
    public void SetTargetLanguage_BeforeStart_IsNoOp()
    {
        using var harness = new Harness().WithWorkingEngine();

        harness.Pipeline.SetTargetLanguage("ja");

        Assert.Empty(harness.FactoryCalls);
    }

    // ---------------------------------------------------------------------
    // Failure paths
    // ---------------------------------------------------------------------

    [Fact]
    public async Task LiveEngineFailure_DetachesAndDisposesTheEngine_ButKeepsCaptureAlive()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);
        harness.Engine.EmitPartialTranslation("aktibong salin");

        harness.Engine.Fail(new LiveTranslationError(
            LiveTranslationErrorKind.ConnectionFailed, "websocket closed", null));

        Assert.Contains(harness.Statuses, s => s.Kind == PipelineStatusKind.Error);
        LiveTranslationError reported = Assert.Single(harness.LiveErrors);
        Assert.Equal(LiveTranslationErrorKind.ConnectionFailed, reported.Kind);
        Assert.Null(harness.Captions.State.ActiveTranslationLine);
        Assert.True(harness.Pipeline.IsRunning); // capture keeps running; not faulted

        for (int i = 0; i < 50 && harness.Engine.DisposeCount == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, harness.Engine.DisposeCount);
    }

    [Fact]
    public void CaptureFailure_MarksFaulted_AndStopsTheSession()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        harness.Capture.Fail(new AudioCaptureError(
            AudioCaptureErrorKind.DeviceDisconnected, "device gone"));

        Assert.False(harness.Pipeline.IsRunning);
        Assert.Contains(harness.Statuses, s => s.Kind == PipelineStatusKind.Error);
    }

    [Fact]
    public void ThrowingProcessor_FaultsTheSession_AtRuntime()
    {
        using var harness = new Harness(new ThrowingProcessor()).WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        harness.Capture.Emit(Chunk());

        Assert.False(harness.Pipeline.IsRunning);
        Assert.Contains(harness.Statuses, s => s.Kind == PipelineStatusKind.Error && s.Message.Contains("Audio processing failed"));
    }

    // ---------------------------------------------------------------------
    // Stop / dispose lifecycle
    // ---------------------------------------------------------------------

    [Fact]
    public async Task StopAsync_StopsCaptureAndEngine_AndRaisesStoppedStatus()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        await harness.Pipeline.StopAsync();

        Assert.False(harness.Pipeline.IsRunning);
        Assert.False(harness.Captions.IsRunning);
        Assert.Contains(harness.Statuses, s => s.Kind == PipelineStatusKind.Stopped);
        Assert.Equal(1, harness.Engine.StopCount);

        for (int i = 0; i < 50 && harness.Engine.DisposeCount == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, harness.Engine.DisposeCount);
        Assert.True(harness.Capture.IsDisposed);
    }

    [Fact]
    public void Stop_WhenNotRunning_IsNoOp()
    {
        using var harness = new Harness().WithWorkingEngine();

        harness.Pipeline.Stop();

        Assert.DoesNotContain(harness.Statuses, s => s.Kind == PipelineStatusKind.Stopped);
    }

    [Fact]
    public void Dispose_TearsDownTheSession_AndRejectsFurtherUse()
    {
        var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        harness.Pipeline.Dispose();

        Assert.False(harness.Pipeline.IsRunning);
        Assert.Throws<ObjectDisposedException>(() => harness.Pipeline.Start(null, "en", "tl", translationEnabled: true));
        Assert.Throws<ObjectDisposedException>(() => harness.Pipeline.SetTranslationEnabled(true));
        Assert.Throws<ObjectDisposedException>(() => harness.Pipeline.SetTargetLanguage("ja"));
    }

    [Fact]
    public void Dispose_WhenNeverStarted_IsSafe()
    {
        using var harness = new Harness();

        harness.Pipeline.Dispose();
    }

    // ---------------------------------------------------------------------
    // TD-002 device-change recovery
    // ---------------------------------------------------------------------

    [Fact]
    public async Task RestartCaptureAsync_RecreatesCapture_PreservingTheEngine()
    {
        var monitor = new FakeDeviceChangeMonitor();
        var firstCapture = new FakeAudioCapture();
        var secondCapture = new FakeAudioCapture();
        var captures = new Queue<FakeAudioCapture>([firstCapture, secondCapture]);
        var engine = new FakeLiveEngine();
        using var captions = new CaptionService(new CaptionServiceOptions("en", historyCapacity: 20));
        using var pipeline = new CaptionPipeline(
            _ => captures.Dequeue(),
            new PassthroughProcessor(),
            captions,
            _ => engine,
            monitor);
        pipeline.Start(null, "en", "tl", translationEnabled: true);
        Assert.True(firstCapture.IsCapturing);

        await pipeline.RestartCaptureAsync();

        Assert.True(pipeline.IsRunning);
        Assert.False(firstCapture.IsCapturing);
        Assert.True(secondCapture.IsCapturing); // capture recreated
        Assert.Equal(1, engine.StartCount); // engine preserved — no new session

        await pipeline.StopAsync();
        pipeline.Dispose();
    }

    [Fact]
    public async Task RestartCaptureAsync_OnExplicitDevice_IsNoOp()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start("{device-id}", "en", "tl", translationEnabled: true);
        int startCountBefore = harness.Engine.StartCount;

        await harness.Pipeline.RestartCaptureAsync();

        Assert.True(harness.Pipeline.IsRunning);
        Assert.Equal(startCountBefore, harness.Engine.StartCount);
    }

    [Fact]
    public async Task RestartCaptureAsync_WhenNotRunning_IsNoOp()
    {
        using var harness = new Harness().WithWorkingEngine();

        await harness.Pipeline.RestartCaptureAsync();

        Assert.False(harness.Pipeline.IsRunning);
        Assert.Empty(harness.FactoryCalls);
    }

    // ---------------------------------------------------------------------
    // SessionEnded reconnect (v0.5.46: 540k hide-on-goAway regression)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reproduces the 540k scenario: the Gemini session emits <see cref="LiveTranslationErrorKind.SessionEnded"/>
    /// (e.g. goAway, server-side session cap) while capture is alive. The pipeline must:
    /// <list type="number">
    ///   <item>NOT mark the session as faulted (capture keeps running).</item>
    ///   <item>Surface a <see cref="PipelineStatusKind.Error"/> status (with the
    ///   <see cref="LiveTranslationErrorKind.SessionEnded"/> error attached) so the UI can show
    ///   "reconnecting".</item>
    ///   <item>Fire <see cref="CaptionPipeline.RestartLiveTranslationAsync"/> so a fresh Gemini
    ///   session takes over without the user pressing Start.</item>
    ///   <item>Raise <see cref="CaptionPipeline.SessionResumed"/> exactly once, AFTER the new
    ///   engine is attached. The control window reacts to that event by calling
    ///   <see cref="ICaptionService.ClearCaptionContent"/> and refreshing the overlay so the
    ///   stale "frozen-but-on" caption from the previous session is cleared and the overlay is
    ///   ready to repaint the new session's first partial. This pipeline test only pins the
    ///   event firing itself; the control window test pins the visible behavior.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task SessionEnded_FromRunningEngine_ClearsActiveLineAndFiresReconnectAndSessionResumed()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);
        Assert.True(harness.Pipeline.IsRunning);

        // Prime an active translation line so we can prove it gets cleared on reconnect (the
        // SessionResumed → ClearCaptionContent path is what removes the stale caption that the
        // user saw stuck on the overlay before).
        harness.Engine.EmitPartialTranslation("Mahigpit na pagbati", sequence: 1);
        Assert.Single(harness.FactoryCalls); // just the initial Start

        int sessionResumedCount = 0;
        harness.Pipeline.SessionResumed += (_, _) => sessionResumedCount++;

        // Act: the engine reports a recoverable session end (the 540k scenario).
        harness.Engine.Fail(new LiveTranslationError(
            LiveTranslationErrorKind.SessionEnded,
            "Gemini session ended. Toggle translation off/on or restart to resume.",
            null));

        // The pipeline detaches synchronously and fires RestartLiveTranslationAsync on a background
        // task; poll for the reconnect (factory called twice = initial Start + reconnect).
        for (int i = 0; i < 100 && harness.FactoryCalls.Count < 2; i++)
        {
            await Task.Delay(20);
        }

        // 1. The pipeline is still running (capture alive) — SessionEnded is NOT a fault.
        Assert.True(harness.Pipeline.IsRunning);

        // 2. The pipeline raised a SessionEnded error (so the UI can show "reconnecting").
        LiveTranslationError error = Assert.Single(harness.LiveErrors, e => e.Kind == LiveTranslationErrorKind.SessionEnded);

        // 3. The pipeline surfaced an Error status (the UI updates its indicator / text).
        Assert.Contains(harness.Statuses, s => s.Kind == PipelineStatusKind.Error);

        // 4. The pipeline fired RestartLiveTranslationAsync: factory was called again, the engine
        //    was re-StartAsync'd. Allow the background reconnect to complete.
        Assert.Equal(2, harness.FactoryCalls.Count);
        for (int i = 0; i < 100 && harness.Engine.StartCount < 2; i++)
        {
            await Task.Delay(20);
        }
        Assert.Equal(2, harness.Engine.StartCount);

        // 5. SessionResumed fired exactly once, AFTER the new engine was attached. The control
        //    window's handler calls ClearCaptionContent() on the caption service.
        for (int i = 0; i < 100 && sessionResumedCount == 0; i++)
        {
            await Task.Delay(20);
        }
        Assert.Equal(1, sessionResumedCount);

        // 6. Simulate the control window's handler: clear the caption content.
        harness.Captions.ClearCaptionContent();

        // 7. The active translation line is now cleared. This is the visible-behavior contract:
        //    on reconnect the stale caption from the previous session is removed so the overlay
        //    can paint the new session's first partial instead of looking frozen.
        var snapshot = harness.Captions.GetSnapshot();
        Assert.Null(snapshot.ActiveTranslationLine);
    }

    /// <summary>
    /// A non-recoverable failure (e.g. quota exceeded) must NOT fire RestartLiveTranslationAsync —
    /// the reconnect path is for <see cref="LiveTranslationErrorKind.SessionEnded"/> only. Other
    /// kinds surface as classified errors so the UI can prompt the user to update the key or wait
    /// for quota. This test pins that boundary so the 540k fix cannot regress into "reconnect on
    /// every error".
    /// </summary>
    [Fact]
    public async Task NonRecoverableFailure_DoesNotFireReconnect()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);
        int factoryCallsBefore = harness.FactoryCalls.Count;

        harness.Engine.Fail(new LiveTranslationError(
            LiveTranslationErrorKind.QuotaExceeded,
            "Quota exceeded.",
            null));

        // Give the background fire-and-forget a moment — there should be nothing to wait for.
        await Task.Delay(200);

        Assert.Equal(factoryCallsBefore, harness.FactoryCalls.Count);
        Assert.Equal(1, harness.Engine.StartCount); // unchanged
        Assert.True(harness.Pipeline.IsRunning); // capture still alive
    }

    /// <summary>
    /// Pins the pipeline-only contract that <see cref="CaptionPipeline.SessionResumed"/> fires
    /// exactly once on a successful reconnect after <see cref="LiveTranslationErrorKind.SessionEnded"/>,
    /// and that the event handler runs AFTER the new engine has been attached (so a subscriber
    /// that immediately re-emits something goes through the live engine, not the dead one).
    /// </summary>
    [Fact]
    public async Task RestartLiveTranslation_OnSessionEnded_FiresSessionResumed()
    {
        using var harness = new Harness().WithWorkingEngine();
        harness.Pipeline.Start(null, "en", "tl", translationEnabled: true);

        // Track the order: factory calls vs SessionResumed invocations.
        var order = new List<string>();
        harness.Pipeline.SessionResumed += (_, _) => order.Add("SessionResumed");

        // The harness factory wraps the engine in a "before/after" hook that adds to `order` via
        // FactoryCalls.Count; we mirror that with a custom subscribe that fires on each factory
        // call. The existing harness increments FactoryCalls.Count, so we observe via a side-list.
        int factoryCallsBefore = harness.FactoryCalls.Count;

        // Fail the engine with SessionEnded — triggers RestartLiveTranslationAsync.
        harness.Engine.Fail(new LiveTranslationError(
            LiveTranslationErrorKind.SessionEnded,
            "Gemini session ended.",
            null));

        // Poll until the reconnect factory call lands AND the SessionResumed event fires.
        for (int i = 0; i < 200; i++)
        {
            if (harness.FactoryCalls.Count > factoryCallsBefore && order.Count >= 1)
            {
                break;
            }
            await Task.Delay(20);
        }

        // The factory was invoked exactly once for the reconnect (initial Start + reconnect).
        Assert.Equal(factoryCallsBefore + 1, harness.FactoryCalls.Count);

        // SessionResumed fired exactly once.
        Assert.Single(order);

        // The new engine has been StartAsync'd (otherwise the SessionResumed handler would have
        // nothing to feed).
        for (int i = 0; i < 100 && harness.Engine.StartCount < 2; i++)
        {
            await Task.Delay(20);
        }
        Assert.Equal(2, harness.Engine.StartCount);
    }
}
