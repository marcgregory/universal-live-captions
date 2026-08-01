using UniversalCaptions.App.Pipeline;
using UniversalCaptions.Audio.Processing;
using UniversalCaptions.Captions;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Capture;
using UniversalCaptions.Core.Processing;
using UniversalCaptions.Core.Speech;

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

        public void EmitPartial(string text, long sequence = 1)
            => PartialTranscriptAvailable?.Invoke(
                this, new PartialTranscript(text, DateTime.UtcNow, DateTime.UtcNow, sequence));

        public void EmitFinal(string text, long sequence = 1, TimeSpan? latency = null)
        {
            DateTime captured = DateTime.UtcNow;
            DateTime emitted = captured + (latency ?? TimeSpan.FromMilliseconds(400));
            FinalTranscriptAvailable?.Invoke(this, new FinalTranscript(text, captured, emitted, sequence));
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
}
