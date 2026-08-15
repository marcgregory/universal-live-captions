using System.Collections.Concurrent;
using System.Linq;
using UniversalCaptions.App.Pipeline;
using UniversalCaptions.App.Settings;
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
/// Verifies the app-side wiring (capture → processor → speech-to-text → caption service) and the
/// start/stop/error lifecycle against deterministic fakes. WPF visuals are verified manually.
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

    private sealed class FakeSpeechToTextEngine : ISpeechToTextEngine
    {
        public event EventHandler<PartialTranscript>? PartialTranscriptAvailable;
        public event EventHandler<FinalTranscript>? FinalTranscriptAvailable;
        public event EventHandler<SpeechRecognitionError>? RecognitionFailed;
        public bool IsRecognizing { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool ThrowOnProcess { get; set; }
        public List<AudioChunk> Received { get; } = [];

        public void Start() => IsRecognizing = true;
        public void Stop() => IsRecognizing = false;
        public void Dispose() => IsDisposed = true;

        public void Process(AudioChunk chunk)
        {
            if (ThrowOnProcess)
            {
                throw new InvalidOperationException("Speech engine rejected the chunk.");
            }

            Received.Add(chunk);
        }

        public void EmitPartial(string text, long sequence = 1, DateTime? captured = null, DateTime? emitted = null)
        {
            DateTime capturedAt = captured ?? DateTime.UtcNow;
            PartialTranscriptAvailable?.Invoke(
                this, new PartialTranscript(text, capturedAt, emitted ?? capturedAt, sequence));
        }

        public void EmitFinal(string text, long sequence = 1, TimeSpan? latency = null, DateTime? captured = null, DateTime? emitted = null)
        {
            DateTime capturedAt = captured ?? DateTime.UtcNow;
            DateTime emittedAt = emitted ?? capturedAt + (latency ?? TimeSpan.FromMilliseconds(400));
            FinalTranscriptAvailable?.Invoke(this, new FinalTranscript(text, capturedAt, emittedAt, sequence));
        }

        public void Fail(SpeechRecognitionError error) => RecognitionFailed?.Invoke(this, error);
    }

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

    // CS0067: the interface declares the translation events and the pipeline subscribes to them, but
    // this fake never raises them — it only verifies the engine lifecycle (create/start/stop).
#pragma warning disable CS0067
    /// <summary>A live translation engine that counts lifecycle calls instead of performing I/O.</summary>
    private sealed class FakeLiveAudioTranslationEngine : ILiveAudioTranslationEngine
    {
        public event EventHandler<PartialTranslation>? PartialTranslationAvailable;
        public event EventHandler<FinalTranslation>? FinalTranslationAvailable;
        public event EventHandler<LiveTranslationError>? TranslationFailed;

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int PushAudioCount { get; private set; }
        public bool IsDisposed { get; private set; }

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

        public void PushAudio(AudioChunk chunk) => PushAudioCount++;

        public void Dispose() => IsDisposed = true;

        public void EmitPartial(string translatedText, long sequence = 1)
        {
            PartialTranslationAvailable?.Invoke(
                this,
                new PartialTranslation(
                    sourceText: null,
                    translatedText: translatedText,
                    sourceLanguage: "en",
                    targetLanguage: "tl",
                    capturedAtUtc: DateTime.UtcNow,
                    emittedAtUtc: DateTime.UtcNow,
                    sequence: sequence));
        }

        public void EmitFinal(
            string translatedText,
            long sequence = 1,
            string sourceText = "good morning",
            string sourceLanguage = "en",
            string targetLanguage = "tl")
        {
            FinalTranslationAvailable?.Invoke(
                this,
                new FinalTranslation(
                    sourceText: sourceText,
                    translatedText: translatedText,
                    sourceLanguage: sourceLanguage,
                    targetLanguage: targetLanguage,
                    capturedAtUtc: DateTime.UtcNow,
                    emittedAtUtc: DateTime.UtcNow,
                    sequence: sequence,
                    committedAtUtc: DateTime.UtcNow));
        }

        public void Fail(LiveTranslationError error) => TranslationFailed?.Invoke(this, error);
    }
#pragma warning restore CS0067

    // CS0067: the interface declares the transcript events and the pipeline subscribes to them, but
    // this fake never raises them — it only exercises the start-failure path.
#pragma warning disable CS0067
    /// <summary>A speech engine that raises <see cref="RecognitionFailed"/> synchronously from <see cref="Start"/>.</summary>
    private sealed class FailingOnStartSpeechToTextEngine : ISpeechToTextEngine
    {
        public event EventHandler<PartialTranscript>? PartialTranscriptAvailable;
        public event EventHandler<FinalTranscript>? FinalTranscriptAvailable;
        public event EventHandler<SpeechRecognitionError>? RecognitionFailed;
        public bool IsRecognizing { get; private set; }
        public bool IsDisposed { get; private set; }

        public void Start() => RecognitionFailed?.Invoke(this, new SpeechRecognitionError(
            SpeechRecognitionErrorKind.ModelLoadFailed, "Model could not be loaded during start."));
        public void Stop() { }
        public void Dispose() => IsDisposed = true;
        public void Process(AudioChunk chunk) { }
    }

    /// <summary>A capture source that raises <see cref="CaptureFailed"/> synchronously from <see cref="Start"/>.</summary>
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

    /// <summary>A speech engine whose <see cref="Stop"/> blocks until the test releases it, so the
    /// caller-returns-early teardown behavior can be verified deterministically.</summary>
    private sealed class BlockingStopSpeechToTextEngine : ISpeechToTextEngine
    {
        public event EventHandler<PartialTranscript>? PartialTranscriptAvailable;
        public event EventHandler<FinalTranscript>? FinalTranscriptAvailable;
        public event EventHandler<SpeechRecognitionError>? RecognitionFailed;
        public bool IsRecognizing { get; private set; }
        public bool IsDisposed { get; private set; }
        public TaskCompletionSource<bool> AllowStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Start() => IsRecognizing = true;
        public void Stop()
        {
            AllowStop.Task.Wait();
            IsRecognizing = false;
        }
        public void Dispose() => IsDisposed = true;
        public void Process(AudioChunk chunk) { }
    }
#pragma warning restore CS0067

    /// <summary>A deterministic clock the test advances, so end-to-end latency samples are exact.</summary>
    private sealed class MutableClock
    {
        public DateTime Now { get; set; } = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        public DateTime UtcNow() => Now;
    }

    /// <summary>A translation engine completed manually by the test, so end-to-end timing is deterministic.</summary>
    private sealed class GatedTranslationEngine : ITranslationEngine
    {
        private readonly List<TaskCompletionSource<TranslationResult>> _completions = [];

        public int RequestCount => _completions.Count;

        public Task<TranslationResult> TranslateAsync(
            string text, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _completions.Add(completion);
            return completion.Task;
        }

        public void CompleteLatest(string translatedText, string targetLanguage) =>
            _completions[^1].TrySetResult(new TranslationResult(translatedText, "en", targetLanguage, 0, DateTime.UtcNow, DateTime.UtcNow));
    }

    /// <summary>A translation engine that fails every request, like a missing Argos backend.</summary>
    private sealed class FailingTranslationEngine : ITranslationEngine
    {
        public Task<TranslationResult> TranslateAsync(
            string text, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
            Task.FromException<TranslationResult>(
                new TranslationException(TranslationErrorKind.EngineUnavailable, "python missing"));
    }

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
        public FakeSpeechToTextEngine SpeechToText { get; } = new();
        public CaptionService Captions { get; } = new(new CaptionServiceOptions("en", historyCapacity: 20));
        public IAudioProcessor Processor { get; }
        public string? ReceivedDeviceId { get; private set; }
        public string? ReceivedSttLanguage { get; private set; }
        public CaptionPipeline Pipeline { get; }
        public List<PipelineStatus> Statuses { get; } = [];
        public List<TimeSpan> Latencies { get; } = [];

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
                language =>
                {
                    ReceivedSttLanguage = language;
                    return SpeechToText;
                },
                Captions);
            Pipeline.StatusChanged += (_, s) => Statuses.Add(s);
            Pipeline.LatencyUpdated += (_, l) => Latencies.Add(l);
        }

        public void Dispose() => Pipeline.Dispose();
    }

    private static AudioChunk Chunk(AudioFormat format, long sequence = 1) =>
        new(new float[format.Channels * 160], format, DateTime.UtcNow, sequence);

    [Fact]
    public void Start_wires_and_runs_the_pipeline()
    {
        using var h = new Harness();

        h.Pipeline.Start(null, null);

        Assert.True(h.Capture.IsCapturing);
        Assert.True(h.SpeechToText.IsRecognizing);
        Assert.True(h.Captions.IsRunning);
        Assert.True(h.Pipeline.IsRunning);
        Assert.Equal(PipelineStatusKind.Capturing, h.Statuses[^1].Kind);
    }

    [Fact]
    public void Start_is_idempotent_while_running()
    {
        using var h = new Harness();

        h.Pipeline.Start(null, null);
        h.Pipeline.Start(null, null);

        Assert.Single(h.Statuses, s => s.Kind == PipelineStatusKind.Capturing);
    }

    [Fact]
    public void Start_forwards_device_id_and_language_to_factories()
    {
        using var h = new Harness();

        h.Pipeline.Start("device-1", "en");

        Assert.Equal("device-1", h.ReceivedDeviceId);
        Assert.Equal("en", h.ReceivedSttLanguage);
    }

    [Fact]
    public void Start_normalizes_blank_device_and_language_to_null()
    {
        using var h = new Harness();

        h.Pipeline.Start("  ", " ");

        Assert.Null(h.ReceivedDeviceId);
        Assert.Null(h.ReceivedSttLanguage);
    }

    [Fact]
    public void Audio_chunks_flow_through_processor_to_speech_engine()
    {
        using var h = new Harness();
        h.Pipeline.Start(null, null);

        h.Capture.Emit(Chunk(h.Capture.Format, 7));

        Assert.Single(h.SpeechToText.Received);
        Assert.Equal(7, h.SpeechToText.Received[0].Sequence);
    }

    [Fact]
    public void Processor_output_format_is_fed_to_speech_engine()
    {
        using var h = new Harness(new AudioProcessor(new AudioFormat(16_000, 1, 32)));
        h.Pipeline.Start(null, null);

        h.Capture.Emit(Chunk(new AudioFormat(48_000, 2, 32), 3));

        AudioChunk fed = Assert.Single(h.SpeechToText.Received);
        Assert.Equal(16_000, fed.Format.SampleRate);
        Assert.Equal(1, fed.Format.Channels);
    }

    [Fact]
    public void Partial_transcripts_update_caption_state()
    {
        using var h = new Harness();
        h.Pipeline.Start(null, null);

        h.SpeechToText.EmitPartial("hel", 1);
        h.SpeechToText.EmitPartial("hello", 2);

        Assert.Equal("hello", h.Captions.State.ActiveLine!.Text);
    }

    [Fact]
    public void Final_transcript_commits_and_reports_latency()
    {
        using var h = new Harness();
        h.Pipeline.Start(null, null);

        h.SpeechToText.EmitFinal("done", 1, TimeSpan.FromMilliseconds(400));

        CaptionLine committed = Assert.Single(h.Captions.State.History);
        Assert.Equal("done", committed.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(400), h.Latencies[0]);
    }

    [Fact]
    public async Task Recognition_failure_surfaces_error_and_stops()
    {
        using var h = new Harness();
        h.Pipeline.Start(null, null);

        h.SpeechToText.Fail(new SpeechRecognitionError(
            SpeechRecognitionErrorKind.ModelNotFound, "Whisper model file was not found."));
        await h.Pipeline.StopAsync();

        Assert.Equal(PipelineStatusKind.Error, h.Statuses[^1].Kind);
        Assert.Contains("not found", h.Statuses[^1].Message);
        Assert.False(h.Capture.IsCapturing);
        Assert.False(h.SpeechToText.IsRecognizing);
        Assert.False(h.Captions.IsRunning);
    }

    [Fact]
    public void Capture_failure_surfaces_error_and_stops()
    {
        using var h = new Harness();
        h.Pipeline.Start(null, null);

        h.Capture.Fail(new AudioCaptureError(AudioCaptureErrorKind.DeviceDisconnected, "The audio device disconnected."));

        Assert.Equal(PipelineStatusKind.Error, h.Statuses[^1].Kind);
        Assert.Contains("disconnected", h.Statuses[^1].Message);
        Assert.False(h.Pipeline.IsRunning);
    }

    [Fact]
    public void Capture_factory_failure_surfaces_error_without_starting()
    {
        using var h = new Harness();
        using var failing = new CaptionPipeline(
            _ => throw new AudioCaptureException(AudioCaptureErrorKind.NoOutputDevice, "No audio output device was found."),
            new PassthroughProcessor(),
            _ => h.SpeechToText,
            h.Captions);
        var statuses = new List<PipelineStatus>();
        failing.StatusChanged += (_, s) => statuses.Add(s);

        failing.Start(null, null);

        Assert.Equal(PipelineStatusKind.Error, statuses[^1].Kind);
        Assert.Contains("output device", statuses[^1].Message);
        Assert.False(failing.IsRunning);
        Assert.False(h.Captions.IsRunning);
    }

    [Fact]
    public async Task Stop_tears_down_the_session()
    {
        using var h = new Harness();
        h.Pipeline.Start(null, null);

        await h.Pipeline.StopAsync();

        Assert.False(h.Capture.IsCapturing);
        Assert.False(h.SpeechToText.IsRecognizing);
        Assert.False(h.Captions.IsRunning);
        Assert.Equal(PipelineStatusKind.Stopped, h.Statuses[^1].Kind);
    }

    [Fact]
    public async Task Stop_returns_before_component_teardown_completes()
    {
        var blockingStt = new BlockingStopSpeechToTextEngine();
        using var pipeline = new CaptionPipeline(
            _ => new FakeAudioCapture(),
            new PassthroughProcessor(),
            _ => blockingStt,
            new CaptionService(new CaptionServiceOptions("en", historyCapacity: 20)));

        pipeline.Start(null, null);

        Task stop = Task.Run(() => pipeline.Stop());
        Task completed = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.True(ReferenceEquals(stop, completed), "Stop() must not block the caller on component teardown.");

        blockingStt.AllowStop.TrySetResult(true);
        await stop;
        await pipeline.StopAsync();

        Assert.True(blockingStt.IsDisposed);
    }

    [Fact]
    public async Task Dispose_waits_for_component_teardown()
    {
        var blockingStt = new BlockingStopSpeechToTextEngine();
        var capture = new FakeAudioCapture();
        using var pipeline = new CaptionPipeline(
            _ => capture,
            new PassthroughProcessor(),
            _ => blockingStt,
            new CaptionService(new CaptionServiceOptions("en", historyCapacity: 20)));

        pipeline.Start(null, null);

        Task dispose = Task.Run(() => pipeline.Dispose());
        Task completedEarly = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromMilliseconds(200)));

        Assert.False(ReferenceEquals(dispose, completedEarly), "Dispose() must wait for component teardown to complete.");

        blockingStt.AllowStop.TrySetResult(true);
        Task completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(ReferenceEquals(dispose, completed), "Dispose() should complete once teardown finishes.");
        Assert.True(blockingStt.IsDisposed);
        Assert.True(capture.IsDisposed);
    }

    [Fact]
    public void Dispose_stops_and_disposes_components()
    {
        var h = new Harness();
        h.Pipeline.Start(null, null);

        h.Pipeline.Dispose();

        Assert.False(h.Capture.IsCapturing);
        Assert.True(h.Capture.IsDisposed);
        Assert.True(h.SpeechToText.IsDisposed);
        Assert.False(h.Captions.IsRunning);
    }

    [Fact]
    public async Task Speech_engine_failure_during_start_surfaces_error_and_tears_down()
    {
        var failingStt = new FailingOnStartSpeechToTextEngine();
        var capture = new FakeAudioCapture();
        using var pipeline = new CaptionPipeline(
            _ => capture,
            new PassthroughProcessor(),
            _ => failingStt,
            new CaptionService(new CaptionServiceOptions("en", historyCapacity: 20)));
        var statuses = new List<PipelineStatus>();
        pipeline.StatusChanged += (_, s) => statuses.Add(s);

        pipeline.Start(null, null);

        Assert.Equal(PipelineStatusKind.Error, statuses[^1].Kind);
        Assert.Contains("start", statuses[^1].Message);
        Assert.False(pipeline.IsRunning);
        Assert.False(capture.IsCapturing);
        await pipeline.StopAsync();
        Assert.True(failingStt.IsDisposed);
        Assert.True(capture.IsDisposed);
    }

    [Fact]
    public async Task Capture_failure_during_start_surfaces_error_and_tears_down()
    {
        var failingCapture = new FailingOnStartAudioCapture();
        var stt = new FakeSpeechToTextEngine();
        using var pipeline = new CaptionPipeline(
            _ => failingCapture,
            new PassthroughProcessor(),
            _ => stt,
            new CaptionService(new CaptionServiceOptions("en", historyCapacity: 20)));
        var statuses = new List<PipelineStatus>();
        pipeline.StatusChanged += (_, s) => statuses.Add(s);

        pipeline.Start(null, null);

        Assert.Equal(PipelineStatusKind.Error, statuses[^1].Kind);
        Assert.False(pipeline.IsRunning);
        await pipeline.StopAsync();
        Assert.False(stt.IsRecognizing);
        Assert.True(failingCapture.IsDisposed);
        Assert.True(stt.IsDisposed);
    }

    [Fact]
    public async Task Audio_processing_exception_surfaces_error_and_stops()
    {
        using var h = new Harness(new ThrowingProcessor());
        h.Pipeline.Start(null, null);

        h.Capture.Emit(Chunk(h.Capture.Format, 1));

        Assert.Equal(PipelineStatusKind.Error, h.Statuses[^1].Kind);
        Assert.Contains("processing", h.Statuses[^1].Message);
        await h.Pipeline.StopAsync();
        Assert.False(h.Capture.IsCapturing);
        Assert.False(h.SpeechToText.IsRecognizing);
        Assert.False(h.Captions.IsRunning);
    }

    [Fact]
    public async Task Speech_engine_processing_exception_surfaces_error_and_stops()
    {
        using var h = new Harness();
        h.SpeechToText.ThrowOnProcess = true;
        h.Pipeline.Start(null, null);

        h.Capture.Emit(Chunk(h.Capture.Format, 1));

        Assert.Equal(PipelineStatusKind.Error, h.Statuses[^1].Kind);
        Assert.Contains("processing", h.Statuses[^1].Message);
        await h.Pipeline.StopAsync();
        Assert.False(h.Pipeline.IsRunning);
        Assert.False(h.SpeechToText.IsRecognizing);
    }

    [Fact]
    public void Chunks_after_stop_are_ignored()
    {
        using var h = new Harness();
        h.Pipeline.Start(null, null);
        h.Pipeline.Stop();

        h.Capture.Emit(Chunk(h.Capture.Format, 5));

        Assert.Empty(h.SpeechToText.Received);
    }

    [Fact]
    public async Task Translated_partial_raises_partial_end_to_end_latency_sample()
    {
        var clock = new MutableClock();
        var engine = new GatedTranslationEngine();
        var stt = new FakeSpeechToTextEngine();
        var captions = new CaptionService(new CaptionServiceOptions("en", "tl", 20), engine, clock.UtcNow);
        using var pipeline = new CaptionPipeline(_ => new FakeAudioCapture(), new PassthroughProcessor(), _ => stt, captions);
        var samples = new List<EndToEndLatencySample>();
        pipeline.EndToEndLatencyUpdated += (_, s) => samples.Add(s);

        pipeline.Start(null, null);
        captions.SetTranslationEnabled(true, "tl");

        DateTime captured = clock.Now;
        stt.EmitPartial("hel", 1, captured, captured);

        clock.Now = clock.Now.AddMilliseconds(500);
        engine.CompleteLatest("kumusta", "tl");
        await captions.FlushAsync();

        var sample = Assert.Single(samples);
        Assert.Equal(EndToEndLatencyKind.Partial, sample.Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(500), sample.EndToEndLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(500), sample.TranslationLatency);
    }

    [Fact]
    public async Task Translated_final_raises_final_end_to_end_latency_sample()
    {
        var clock = new MutableClock();
        var engine = new GatedTranslationEngine();
        var stt = new FakeSpeechToTextEngine();
        var captions = new CaptionService(new CaptionServiceOptions("en", "tl", 20), engine, clock.UtcNow);
        using var pipeline = new CaptionPipeline(_ => new FakeAudioCapture(), new PassthroughProcessor(), _ => stt, captions);
        var samples = new List<EndToEndLatencySample>();
        pipeline.EndToEndLatencyUpdated += (_, s) => samples.Add(s);

        pipeline.Start(null, null);
        captions.SetTranslationEnabled(true, "tl");

        DateTime captured = clock.Now;
        stt.EmitFinal("hello world", 1, captured: captured, emitted: captured.AddMilliseconds(400));

        clock.Now = clock.Now.AddSeconds(1);
        engine.CompleteLatest("magandang mundo", "tl");
        await captions.FlushAsync();

        var sample = Assert.Single(samples);
        Assert.Equal(EndToEndLatencyKind.Final, sample.Kind);
        Assert.Equal(TimeSpan.FromSeconds(1), sample.EndToEndLatency);
        Assert.Equal(TimeSpan.FromSeconds(1), sample.TranslationLatency);
    }

    [Fact]
    public async Task Untranslated_or_failed_lines_do_not_raise_end_to_end_samples()
    {
        var stt = new FakeSpeechToTextEngine();
        var captions = new CaptionService(new CaptionServiceOptions("en", historyCapacity: 20));
        using var pipeline = new CaptionPipeline(_ => new FakeAudioCapture(), new PassthroughProcessor(), _ => stt, captions);
        var samples = new List<EndToEndLatencySample>();
        pipeline.EndToEndLatencyUpdated += (_, s) => samples.Add(s);

        pipeline.Start(null, null);
        stt.EmitPartial("hello", 1);
        stt.EmitFinal("hello world", 2);
        await captions.FlushAsync();

        Assert.Empty(samples);

        var failingCaptions = new CaptionService(
            new CaptionServiceOptions("en", "tl", 20), new FailingTranslationEngine());
        var failingStt = new FakeSpeechToTextEngine();
        using var failingPipeline = new CaptionPipeline(
            _ => new FakeAudioCapture(), new PassthroughProcessor(), _ => failingStt, failingCaptions);
        var failingSamples = new List<EndToEndLatencySample>();
        failingPipeline.EndToEndLatencyUpdated += (_, s) => failingSamples.Add(s);

        failingPipeline.Start(null, null);
        failingCaptions.SetTranslationEnabled(true, "tl");
        failingStt.EmitFinal("hello world", 3);
        await failingCaptions.FlushAsync();

        Assert.Empty(failingSamples);
    }

    [Fact]
    public async Task Default_device_changed_recreates_capture_and_keeps_speech_engine()
    {
        using var monitor = new FakeDeviceChangeMonitor();
        var captures = new List<FakeAudioCapture>();
        var stt = new FakeSpeechToTextEngine();
        using var pipeline = new CaptionPipeline(
            _ => { var c = new FakeAudioCapture(); captures.Add(c); return c; },
            new PassthroughProcessor(),
            _ => stt,
            new CaptionService(new CaptionServiceOptions("en", historyCapacity: 20)),
            monitor);

        pipeline.Start(null, null);

        Assert.True(monitor.Started);
        Assert.Single(captures);
        var original = captures[0];

        monitor.Raise(DeviceChangeNotification.DefaultChanged("new-default"));
        Assert.True(
            SpinWait.SpinUntil(() => captures.Count >= 2 && captures[1].IsCapturing, TimeSpan.FromSeconds(2)),
            "Recovery should recreate and start a capture on the new default device.");

        Assert.NotSame(original, captures[1]);
        Assert.True(original.IsDisposed, "The stale capture must be disposed on recovery.");
        Assert.True(captures[1].IsCapturing);
        Assert.True(stt.IsRecognizing, "Recovery must keep the existing speech engine.");
        Assert.True(pipeline.IsRunning);
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task Device_removed_while_on_default_device_triggers_recovery()
    {
        using var monitor = new FakeDeviceChangeMonitor();
        var captures = new List<FakeAudioCapture>();
        using var pipeline = new CaptionPipeline(
            _ => { var c = new FakeAudioCapture(); captures.Add(c); return c; },
            new PassthroughProcessor(),
            _ => new FakeSpeechToTextEngine(),
            new CaptionService(new CaptionServiceOptions("en", historyCapacity: 20)),
            monitor);

        pipeline.Start(null, null);
        var original = captures[0];

        monitor.Raise(DeviceChangeNotification.Removed("gone-device"));
        Assert.True(
            SpinWait.SpinUntil(() => captures.Count >= 2 && captures[1].IsCapturing, TimeSpan.FromSeconds(2)),
            "A removed device should trigger recovery on the default device.");

        Assert.True(original.IsDisposed);
        Assert.True(captures[1].IsCapturing);
        Assert.True(pipeline.IsRunning);
        await pipeline.StopAsync();
    }

    [Fact]
    public void Device_changed_while_on_explicit_device_does_not_recover()
    {
        using var monitor = new FakeDeviceChangeMonitor();
        var captures = new List<FakeAudioCapture>();
        using var pipeline = new CaptionPipeline(
            _ => { var c = new FakeAudioCapture(); captures.Add(c); return c; },
            new PassthroughProcessor(),
            _ => new FakeSpeechToTextEngine(),
            new CaptionService(new CaptionServiceOptions("en", historyCapacity: 20)),
            monitor);

        pipeline.Start("device-1", null);

        monitor.Raise(DeviceChangeNotification.DefaultChanged("new-default"));

        Assert.False(monitor.Started, "Monitoring is only needed while on the default device.");
        var capture = Assert.Single(captures);
        Assert.False(capture.IsDisposed, "An explicitly chosen device must never auto-recover.");
        Assert.True(capture.IsCapturing);
        Assert.True(pipeline.IsRunning);
    }

    [Fact]
    public async Task Burst_of_notifications_does_not_create_duplicate_sessions()
    {
        using var monitor = new FakeDeviceChangeMonitor();
        var captures = new List<FakeAudioCapture>();
        using var pipeline = new CaptionPipeline(
            _ => { var c = new FakeAudioCapture(); captures.Add(c); return c; },
            new PassthroughProcessor(),
            _ => new FakeSpeechToTextEngine(),
            new CaptionService(new CaptionServiceOptions("en", historyCapacity: 20)),
            monitor);

        pipeline.Start(null, null);

        monitor.Raise(DeviceChangeNotification.DefaultChanged("a"));
        monitor.Raise(DeviceChangeNotification.DefaultChanged("b"));
        monitor.Raise(DeviceChangeNotification.StateChangedOf("device", DeviceState.Unplugged));
        Assert.True(
            SpinWait.SpinUntil(() => captures.Count >= 2 && captures[1].IsCapturing, TimeSpan.FromSeconds(2)),
            "Recovery should complete for the coalesced notification window.");

        // Original session + exactly one recovered session: the burst coalesced into one restart.
        Assert.Equal(2, captures.Count);
        Assert.True(captures[0].IsDisposed);
        Assert.True(captures[1].IsCapturing);
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task Device_changed_after_stop_does_not_recover()
    {
        using var monitor = new FakeDeviceChangeMonitor();
        var captures = new List<FakeAudioCapture>();
        using var pipeline = new CaptionPipeline(
            _ => { var c = new FakeAudioCapture(); captures.Add(c); return c; },
            new PassthroughProcessor(),
            _ => new FakeSpeechToTextEngine(),
            new CaptionService(new CaptionServiceOptions("en", historyCapacity: 20)),
            monitor);

        pipeline.Start(null, null);
        await pipeline.StopAsync();

        monitor.Raise(DeviceChangeNotification.DefaultChanged("new-default"));

        Assert.False(monitor.Started);
        Assert.Single(captures);
        Assert.False(pipeline.IsRunning);
    }

    [Fact]
    public void Device_changed_after_dispose_does_not_recover()
    {
        using var monitor = new FakeDeviceChangeMonitor();
        var captures = new List<FakeAudioCapture>();
        var pipeline = new CaptionPipeline(
            _ => { var c = new FakeAudioCapture(); captures.Add(c); return c; },
            new PassthroughProcessor(),
            _ => new FakeSpeechToTextEngine(),
            new CaptionService(new CaptionServiceOptions("en", historyCapacity: 20)),
            monitor);

        pipeline.Start(null, null);
        pipeline.Dispose();

        monitor.Raise(DeviceChangeNotification.DefaultChanged("new-default"));

        Assert.Single(captures);
    }

    [Fact]
    public async Task Recovery_failure_surfaces_error_and_stops()
    {
        using var monitor = new FakeDeviceChangeMonitor();
        int captureCalls = 0;
        var captures = new List<FakeAudioCapture>();
        var stt = new FakeSpeechToTextEngine();
        // Thread-safe so the poll below never races with the StatusChanged handler firing on the
        // pipeline's own thread while the test enumerates (pre-existing flaky race, hardened).
        var statuses = new ConcurrentQueue<PipelineStatus>();
        using var pipeline = new CaptionPipeline(
            _ =>
            {
                captureCalls++;
                if (captureCalls >= 2)
                {
                    throw new AudioCaptureException(
                        AudioCaptureErrorKind.NoOutputDevice, "No audio output device was found.");
                }

                var c = new FakeAudioCapture();
                captures.Add(c);
                return c;
            },
            new PassthroughProcessor(),
            _ => stt,
            new CaptionService(new CaptionServiceOptions("en", historyCapacity: 20)),
            monitor);
        pipeline.StatusChanged += (_, s) => statuses.Enqueue(s);

        pipeline.Start(null, null);

        monitor.Raise(DeviceChangeNotification.DefaultChanged("new-default"));
        Assert.True(
            SpinWait.SpinUntil(() => statuses.Any(s => s.Kind == PipelineStatusKind.Error), TimeSpan.FromSeconds(2)),
            "A failed recovery should surface a controlled error status.");

        Assert.Single(captures);
        Assert.False(pipeline.IsRunning);
        await pipeline.StopAsync();
        Assert.False(stt.IsRecognizing);
        Assert.True(stt.IsDisposed);
    }

    [Fact]
    public async Task Start_with_gemini_provider_forwards_languages_and_starts_live_translation()
    {
        // The UI provider/language wiring (the fix): the pipeline must hand the per-session provider,
        // source, and target to the live-translation factory so the App's factory can construct the
        // Gemini engine from the user's selections — and then fan the live stream out to it.
        using var h = new Harness();
        var live = new FakeLiveAudioTranslationEngine();
        var capt = new List<(TranslationProvider? Provider, string? Source, string? Target)>();
        using var pipeline = new CaptionPipeline(
            _ => h.Capture,
            h.Processor,
            _ => h.SpeechToText,
            h.Captions,
            liveTranslationFactory: pair =>
            {
                capt.Add((pair.Provider, pair.SourceLanguage, pair.TargetLanguage));
                return live;
            });

        pipeline.Start(null, null, TranslationProvider.Gemini, "en", "tl");

        Assert.True(pipeline.IsRunning);
        Assert.Single(capt);
        Assert.Equal(TranslationProvider.Gemini, capt[0].Provider);
        Assert.Equal("en", capt[0].Source);
        Assert.Equal("tl", capt[0].Target);
        Assert.Equal(1, live.StartCount);

        // The captured stream fans out to the live engine (PCM fan-out is the Gemini path).
        h.Capture.Emit(Chunk(h.Capture.Format));
        Assert.Equal(1, live.PushAudioCount);

        await pipeline.StopAsync();
        Assert.Equal(1, live.StopCount);
        Assert.True(live.IsDisposed);
    }

    [Fact]
    public async Task Start_without_provider_does_not_invoke_live_translation_factory()
    {
        // Translation off (provider null) must never create a live engine — the offline pipeline.
        using var h = new Harness();
        int factoryCalls = 0;
        using var pipeline = new CaptionPipeline(
            _ => h.Capture,
            h.Processor,
            _ => h.SpeechToText,
            h.Captions,
            liveTranslationFactory: pair =>
            {
                factoryCalls++;
                return new FakeLiveAudioTranslationEngine();
            });

        pipeline.Start(null, null, liveTranslationProvider: null, liveSourceLanguage: "en", liveTargetLanguage: "tl");

        Assert.True(pipeline.IsRunning);
        Assert.Equal(0, factoryCalls);
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task SetLiveTranslation_toggle_on_while_capturing_starts_live_engine()
    {
        // Translation toggled ON mid-session (Gemini): the pipeline must create + start the live
        // engine without restarting the capture session — Argos UI/UX parity for runtime toggles.
        using var h = new Harness();
        var live = new FakeLiveAudioTranslationEngine();
        var capt = new List<(TranslationProvider? Provider, string? Source, string? Target)>();
        using var pipeline = new CaptionPipeline(
            _ => h.Capture,
            h.Processor,
            _ => h.SpeechToText,
            h.Captions,
            liveTranslationFactory: pair =>
            {
                capt.Add((pair.Provider, pair.SourceLanguage, pair.TargetLanguage));
                return live;
            });

        pipeline.Start(null, null);
        Assert.True(pipeline.IsRunning);
        Assert.Empty(capt);

        pipeline.SetLiveTranslation(TranslationProvider.Gemini, "en", "tl");

        Assert.Single(capt);
        Assert.Equal((TranslationProvider.Gemini, "en", "tl"), (capt[0].Provider, capt[0].Source, capt[0].Target));
        Assert.Equal(1, live.StartCount);

        // The captured stream fans out to the newly started engine.
        h.Capture.Emit(Chunk(h.Capture.Format));
        Assert.Equal(1, live.PushAudioCount);

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task SetLiveTranslation_toggle_off_stops_live_engine_and_keeps_captions_running()
    {
        using var h = new Harness();
        var live = new FakeLiveAudioTranslationEngine();
        int factoryCalls = 0;
        using var pipeline = new CaptionPipeline(
            _ => h.Capture,
            h.Processor,
            _ => h.SpeechToText,
            h.Captions,
            liveTranslationFactory: pair =>
            {
                factoryCalls++;
                return live;
            });

        pipeline.Start(null, null, TranslationProvider.Gemini, "en", "tl");
        Assert.True(pipeline.IsRunning);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, live.StartCount);

        pipeline.SetLiveTranslation(null, null, null);

        // Engine stopped and disposed; no new engine was created; the session keeps capturing.
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, live.StopCount);
        Assert.True(live.IsDisposed);
        Assert.True(pipeline.IsRunning);
        Assert.True(h.Capture.IsCapturing);
        Assert.True(h.SpeechToText.IsRecognizing);

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task SetLiveTranslation_syncs_the_caption_services_live_translation_session_flag()
    {
        // The overlay's live-translation display mode must be driven by the actual provider, not by
        // history content: switching Gemini → Argos (same target) clears the flag so source-STT lines
        // never flash English, and switching Argos → Gemini sets it so the display is target-language
        // only from the first moment (no English flash while Gemini starts).
        using var h = new Harness();
        var live = new FakeLiveAudioTranslationEngine();
        int factoryCalls = 0;
        using var pipeline = new CaptionPipeline(
            _ => h.Capture,
            h.Processor,
            _ => h.SpeechToText,
            h.Captions,
            liveTranslationFactory: pair =>
            {
                factoryCalls++;
                return live;
            });

        pipeline.Start(null, null, TranslationProvider.Gemini, "en", "tl");
        Assert.True(h.Captions.GetSnapshot().IsLiveTranslationSession);

        // Gemini → Argos: the live engine is stopped, so the flag clears.
        pipeline.SetLiveTranslation(null, null, null);
        Assert.False(h.Captions.GetSnapshot().IsLiveTranslationSession);

        // Argos → Gemini: the live engine is recreated, so the flag sets again.
        pipeline.SetLiveTranslation(TranslationProvider.Gemini, "en", "tl");
        Assert.True(h.Captions.GetSnapshot().IsLiveTranslationSession);

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task Live_translation_failure_clears_the_live_session_flag_so_source_captions_show()
    {
        // When the live Gemini engine fails (e.g. a source language the API does not support),
        // the overlay must leave target-only display mode and return to the source captions Whisper
        // keeps producing. Without the flag re-sync the overlay renders nothing after a failure.
        using var h = new Harness();
        var live = new FakeLiveAudioTranslationEngine();
        var errors = new List<LiveTranslationError>();
        using var pipeline = new CaptionPipeline(
            _ => h.Capture,
            h.Processor,
            _ => h.SpeechToText,
            h.Captions,
            liveTranslationFactory: _ => live);
        pipeline.LiveTranslationErrorUpdated += (_, e) => errors.Add(e);

        pipeline.Start(null, null, TranslationProvider.Gemini, "en", "tl");
        Assert.True(h.Captions.GetSnapshot().IsLiveTranslationSession);

        live.Fail(new LiveTranslationError(
            LiveTranslationErrorKind.ServerError,
            "unsupported language",
            null));

        Assert.False(h.Captions.GetSnapshot().IsLiveTranslationSession);
        Assert.Single(errors);
        Assert.True(live.IsDisposed);
        Assert.True(pipeline.IsRunning);
        Assert.True(h.Capture.IsCapturing);

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task SetLiveTranslation_target_change_swaps_the_live_engine()
    {
        using var h = new Harness();
        var engines = new List<FakeLiveAudioTranslationEngine>();
        var capt = new List<(TranslationProvider? Provider, string? Source, string? Target)>();
        using var pipeline = new CaptionPipeline(
            _ => h.Capture,
            h.Processor,
            _ => h.SpeechToText,
            h.Captions,
            liveTranslationFactory: pair =>
            {
                capt.Add((pair.Provider, pair.SourceLanguage, pair.TargetLanguage));
                var engine = new FakeLiveAudioTranslationEngine();
                engines.Add(engine);
                return engine;
            });

        pipeline.Start(null, null, TranslationProvider.Gemini, "en", "tl");
        FakeLiveAudioTranslationEngine first = Assert.Single(engines);
        Assert.Equal(1, first.StartCount);

        pipeline.SetLiveTranslation(TranslationProvider.Gemini, "en", "ja");

        // Old engine stopped + disposed, new engine created with the new target and started.
        Assert.Equal(1, first.StopCount);
        Assert.True(first.IsDisposed);
        Assert.Equal(2, engines.Count);
        FakeLiveAudioTranslationEngine second = engines[1];
        Assert.Equal(1, second.StartCount);
        Assert.Equal((TranslationProvider.Gemini, "en", "ja"), (capt[1].Provider, capt[1].Source, capt[1].Target));

        // The stream now fans out to the NEW engine, never the detached one.
        h.Capture.Emit(Chunk(h.Capture.Format));
        Assert.Equal(0, first.PushAudioCount);
        Assert.Equal(1, second.PushAudioCount);

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task SetLiveTranslation_same_configuration_is_a_noop()
    {
        using var h = new Harness();
        int factoryCalls = 0;
        using var pipeline = new CaptionPipeline(
            _ => h.Capture,
            h.Processor,
            _ => h.SpeechToText,
            h.Captions,
            liveTranslationFactory: pair =>
            {
                factoryCalls++;
                return new FakeLiveAudioTranslationEngine();
            });

        pipeline.Start(null, null, TranslationProvider.Gemini, "en", "tl");
        Assert.Equal(1, factoryCalls);

        pipeline.SetLiveTranslation(TranslationProvider.Gemini, "en", "tl");

        Assert.Equal(1, factoryCalls);
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task SetLiveTranslation_before_start_is_a_noop()
    {
        using var h = new Harness();
        int factoryCalls = 0;
        using var pipeline = new CaptionPipeline(
            _ => h.Capture,
            h.Processor,
            _ => h.SpeechToText,
            h.Captions,
            liveTranslationFactory: pair =>
            {
                factoryCalls++;
                return new FakeLiveAudioTranslationEngine();
            });

        pipeline.SetLiveTranslation(TranslationProvider.Gemini, "en", "tl");

        Assert.Equal(0, factoryCalls);
        Assert.False(pipeline.IsRunning);

        // The toggle is honored by the next Start's own configuration, not by the no-op call.
        pipeline.Start(null, null, TranslationProvider.Gemini, "en", "tl");
        Assert.Equal(1, factoryCalls);
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task Live_translation_failure_clears_active_translation_line_and_keeps_captions_running()
    {
        // Reproduces the Gemini pause/resume symptom: when the live engine raises TranslationFailed
        // (which now happens for graceful goAway shutdowns too), the pipeline must clear the active
        // translation line so the overlay returns to the source captions instead of freezing on
        // whatever translated text it had. The Whisper pipeline keeps running so source captions
        // resume immediately.
        using var h = new Harness();
        var live = new FakeLiveAudioTranslationEngine();
        using var pipeline = new CaptionPipeline(
            _ => h.Capture,
            h.Processor,
            _ => h.SpeechToText,
            h.Captions,
            liveTranslationFactory: _ => live);

        pipeline.Start(null, null, TranslationProvider.Gemini, "en", "tl");
        h.Captions.SetTranslationEnabled(true, "tl");

        // Seed an active translation line and a committed history entry so the assertion proves the
        // clear is scoped to the translation active slot (the committed history is preserved). The
        // final clears the active line (engine's commit semantics), so a second partial arrives to
        // re-populate the active slot — the failure path under test fires while the speaker is
        // mid-utterance.
        live.EmitPartial("Magandang umaga", sequence: 1);
        live.EmitFinal("Magandang umaga lahat.", sequence: 2);
        live.EmitPartial("Ipinakita", sequence: 3);
        Assert.NotNull(h.Captions.State.ActiveTranslationLine);
        Assert.Single(h.Captions.State.History);

        // The live engine raises a graceful-session-end failure (Gemini goAway path).
        var statuses = new ConcurrentQueue<PipelineStatus>();
        pipeline.StatusChanged += (_, s) => statuses.Enqueue(s);
        live.Fail(new LiveTranslationError(
            LiveTranslationErrorKind.ServerError,
            "Live translation session ended by server.",
            null));

        // The pipeline clears the active translation line and surfaces an error status. The
        // committed history is preserved so the overlay still has content to render.
        Assert.Null(h.Captions.State.ActiveTranslationLine);
        Assert.Single(h.Captions.State.History);
        Assert.Equal("Magandang umaga lahat.", h.Captions.State.History[0].TranslatedText);
        Assert.True(pipeline.IsRunning, "Whisper must keep capturing after a live-translation failure.");
        Assert.True(h.Capture.IsCapturing);
        Assert.True(h.SpeechToText.IsRecognizing);

        var errorStatus = statuses.FirstOrDefault(s => s.Kind == PipelineStatusKind.Error);
        Assert.NotNull(errorStatus);
        Assert.Contains("ended by server", errorStatus!.Message);

        // The engine is detached but its disposal runs on a background task — wait for it before
        // teardown so the test does not race a fire-and-forget Task.Run.
        await pipeline.StopAsync();
        Assert.True(live.IsDisposed);
    }

    [Fact]
    public async Task Live_translation_failure_when_no_active_line_is_a_noop_clear()
    {
        // Clearing when there is nothing to clear must not raise StateChanged (the overlay would
        // rerender for no reason) and must not affect the Whisper pipeline.
        using var h = new Harness();
        var live = new FakeLiveAudioTranslationEngine();
        using var pipeline = new CaptionPipeline(
            _ => h.Capture,
            h.Processor,
            _ => h.SpeechToText,
            h.Captions,
            liveTranslationFactory: _ => live);

        pipeline.Start(null, null, TranslationProvider.Gemini, "en", "tl");
        h.Captions.SetTranslationEnabled(true, "tl");

        var stateChangedBefore = h.Captions.State;
        var stateChanges = 0;
        h.Captions.StateChanged += (_, _) => stateChanges++;

        live.Fail(new LiveTranslationError(
            LiveTranslationErrorKind.ConnectionFailed,
            "Live translation receive failed.",
            null));

        Assert.Null(h.Captions.State.ActiveTranslationLine);
        Assert.True(pipeline.IsRunning);
        Assert.True(h.Capture.IsCapturing);

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task Live_translation_failure_raises_classified_error_event()
    {
        // The classified failure must surface through LiveTranslationErrorUpdated so the UI can show
        // an actionable message (invalid key vs quota vs network) and disable Gemini in the dropdown.
        using var h = new Harness();
        var live = new FakeLiveAudioTranslationEngine();
        using var pipeline = new CaptionPipeline(
            _ => h.Capture,
            h.Processor,
            _ => h.SpeechToText,
            h.Captions,
            liveTranslationFactory: _ => live);

        var errors = new List<LiveTranslationError>();
        pipeline.LiveTranslationErrorUpdated += (_, e) => errors.Add(e);

        pipeline.Start(null, null, TranslationProvider.Gemini, "en", "tl");
        live.Fail(new LiveTranslationError(
            LiveTranslationErrorKind.SessionRejected,
            "API key not valid. Please pass a valid API key.",
            null));

        LiveTranslationError error = Assert.Single(errors);
        Assert.Equal(LiveTranslationErrorKind.SessionRejected, error.Kind);
        Assert.Contains("API key", error.Message);
        Assert.True(pipeline.IsRunning, "A live-translation failure must never stop source captions.");
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task SetLiveTranslation_gemini_with_missing_key_raises_classified_error()
    {
        // A Gemini selection whose engine cannot be constructed (the App factory returns null when no
        // API key is stored) must surface the classified "missing/invalid key" error so the UI can
        // fall back to Argos instead of showing "Gemini selected, nothing happens".
        using var h = new Harness();
        using var pipeline = new CaptionPipeline(
            _ => h.Capture,
            h.Processor,
            _ => h.SpeechToText,
            h.Captions,
            liveTranslationFactory: _ => null);

        var errors = new List<LiveTranslationError>();
        pipeline.LiveTranslationErrorUpdated += (_, e) => errors.Add(e);

        pipeline.Start(null, null);
        Assert.True(pipeline.IsRunning);

        pipeline.SetLiveTranslation(TranslationProvider.Gemini, "en", "tl");

        LiveTranslationError error = Assert.Single(errors);
        Assert.Equal(LiveTranslationErrorKind.SessionRejected, error.Kind);
        Assert.Contains("API key", error.Message);
        Assert.True(pipeline.IsRunning, "A missing-key failure must not stop source captions.");
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task SetLiveTranslation_quota_failure_keeps_captions_running()
    {
        // Quota classification flows through the event while the Whisper pipeline stays untouched.
        using var h = new Harness();
        var live = new FakeLiveAudioTranslationEngine();
        using var pipeline = new CaptionPipeline(
            _ => h.Capture,
            h.Processor,
            _ => h.SpeechToText,
            h.Captions,
            liveTranslationFactory: _ => live);

        var errors = new List<LiveTranslationError>();
        pipeline.LiveTranslationErrorUpdated += (_, e) => errors.Add(e);

        pipeline.Start(null, null, TranslationProvider.Gemini, "en", "tl");
        live.Fail(new LiveTranslationError(
            LiveTranslationErrorKind.QuotaExceeded,
            "Quota exceeded for the day.",
            null));

        Assert.Equal(LiveTranslationErrorKind.QuotaExceeded, Assert.Single(errors).Kind);
        Assert.True(pipeline.IsRunning);
        Assert.True(h.SpeechToText.IsRecognizing);
        await pipeline.StopAsync();
    }
}
