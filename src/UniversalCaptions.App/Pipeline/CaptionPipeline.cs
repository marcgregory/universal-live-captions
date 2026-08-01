using System.Diagnostics;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Capture;
using UniversalCaptions.Core.Processing;
using UniversalCaptions.Core.Speech;

namespace UniversalCaptions.App.Pipeline;

/// <summary>
/// Wires the audio pipeline — capture → processor → speech-to-text → caption service — and owns a
/// single capture session at a time. All dependencies are the Core contracts (or factories that
/// produce them), so the wiring can be verified deterministically against fakes. Status and latency
/// are surfaced as events; the UI marshals them to the dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Stop"/>/<see cref="Dispose"/> detach the session and return immediately: the capture
/// source and speech engine are stopped and disposed on a background task (a Whisper engine's
/// <c>Stop</c> waits up to ten seconds for its loop and its dispose can block on the native model),
/// so the calling thread — the WPF UI thread in the app — never blocks on the audio pipeline
/// (ARCHITECTURE state-management rule). <see cref="StopAsync"/> returns the in-flight teardown and
/// <see cref="Dispose"/> waits for it, so shutdown is deterministic without stalling the UI.
/// </para>
/// </remarks>
public sealed class CaptionPipeline : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<string?, IAudioCapture> _captureFactory;
    private readonly IAudioProcessor _processor;
    private readonly Func<string?, ISpeechToTextEngine> _speechToTextFactory;
    private readonly ICaptionService _captions;

    private IAudioCapture? _capture;
    private ISpeechToTextEngine? _speechToText;
    private Task? _teardownTask;
    private bool _faulted;
    private bool _starting;
    private bool _disposed;

    /// <summary>
    /// Creates a pipeline that produces a fresh capture source and speech engine per session.
    /// </summary>
    /// <param name="captureFactory">Creates the capture source for a device (null = system default).</param>
    /// <param name="processor">Converts captured audio to the speech engine's format.</param>
    /// <param name="speechToTextFactory">Creates the speech engine for a language hint (null = auto-detect).</param>
    /// <param name="captions">The caption service the pipeline feeds transcripts into.</param>
    public CaptionPipeline(
        Func<string?, IAudioCapture> captureFactory,
        IAudioProcessor processor,
        Func<string?, ISpeechToTextEngine> speechToTextFactory,
        ICaptionService captions)
    {
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _speechToTextFactory = speechToTextFactory ?? throw new ArgumentNullException(nameof(speechToTextFactory));
        _captions = captions ?? throw new ArgumentNullException(nameof(captions));
        _captions.CaptionLineUpdated += OnCaptionLineUpdated;
    }

    /// <summary>Raised when the pipeline state changes (capturing, stopped, or error).</summary>
    public event EventHandler<PipelineStatus>? StatusChanged;

    /// <summary>Raised for each committed final transcript with its capture-to-emit latency.</summary>
    public event EventHandler<TimeSpan>? LatencyUpdated;

    /// <summary>
    /// Raised when a translated caption (active line or committed line) is published to subscribers —
    /// the moment the translated text is available for the overlay. Carries the end-to-end latency
    /// from the originating audio capture time and the translation latency separately. Distinct from
    /// <see cref="LatencyUpdated"/>, which measures STT-final production only.
    /// </summary>
    public event EventHandler<EndToEndLatencySample>? EndToEndLatencyUpdated;

    /// <summary>True while a capture session is running.</summary>
    public bool IsRunning => _capture?.IsCapturing == true;

    /// <summary>
    /// Starts a caption session on the given device with the given speech-language hint.
    /// </summary>
    /// <param name="deviceId">The Windows endpoint ID of the render device, or null for the system default.</param>
    /// <param name="sttLanguage">An optional speech-language hint (null = auto-detect).</param>
    public void Start(string? deviceId, string? sttLanguage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            return;
        }

        _faulted = false;

        _capture = CreateCapture(deviceId);
        if (_capture is null)
        {
            return;
        }

        _speechToText = CreateSpeechToText(sttLanguage);
        if (_speechToText is null)
        {
            _capture.Dispose();
            _capture = null;
            return;
        }

        _capture.AudioAvailable += OnAudioAvailable;
        _capture.CaptureFailed += OnCaptureFailed;
        _speechToText.PartialTranscriptAvailable += OnPartialTranscript;
        _speechToText.FinalTranscriptAvailable += OnFinalTranscript;
        _speechToText.RecognitionFailed += OnRecognitionFailed;

        _captions.Start();
        try
        {
            _starting = true;
            _speechToText.Start();
            if (!_faulted)
            {
                _capture.Start();
            }
        }
        finally
        {
            _starting = false;
        }

        if (_faulted)
        {
            Stop();
            return;
        }

        if (_capture.IsCapturing)
        {
            UniversalCaptions.Core.Diagnostics.DiagnosticTracer.StartSession();
            RaiseStatus(new PipelineStatus(PipelineStatusKind.Capturing, "Capturing system audio…"));
        }
    }

    /// <summary>
    /// Stops the running session and disposes its capture source and speech engine without blocking
    /// the caller: the session is detached and the caption service is stopped synchronously, and the
    /// component teardown runs on a background task. Idempotent.
    /// </summary>
    public void Stop()
    {
        bool stopped;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            stopped = StopSessionLocked();
        }

        if (stopped && !_faulted)
        {
            RaiseStatus(new PipelineStatus(PipelineStatusKind.Stopped, "Captions stopped."));
        }
    }

    /// <summary>
    /// Stops the session like <see cref="Stop"/> and returns the component teardown so the caller
    /// can wait for it to complete (used by tests and shutdown).
    /// </summary>
    public async Task StopAsync()
    {
        Task? teardown;
        bool stopped;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            stopped = StopSessionLocked();
            teardown = _teardownTask;
        }

        if (stopped && !_faulted)
        {
            RaiseStatus(new PipelineStatus(PipelineStatusKind.Stopped, "Captions stopped."));
        }

        if (teardown is not null)
        {
            await teardown.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Task? teardown;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopSessionLocked();
            teardown = _teardownTask;
        }

        _captions.CaptionLineUpdated -= OnCaptionLineUpdated;
        teardown?.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Detaches the current session and launches its teardown on a background task. Returns true
    /// when a session was stopped (and a teardown started); false when there was nothing to stop.
    /// Callers must hold <see cref="_gate"/>.
    /// </summary>
    private bool StopSessionLocked()
    {
        IAudioCapture? capture = _capture;
        ISpeechToTextEngine? speechToText = _speechToText;
        if (capture is null && speechToText is null && !_captions.IsRunning)
        {
            return false;
        }

        _capture = null;
        _speechToText = null;

        if (capture is not null)
        {
            capture.AudioAvailable -= OnAudioAvailable;
            capture.CaptureFailed -= OnCaptureFailed;
        }

        if (speechToText is not null)
        {
            speechToText.PartialTranscriptAvailable -= OnPartialTranscript;
            speechToText.FinalTranscriptAvailable -= OnFinalTranscript;
            speechToText.RecognitionFailed -= OnRecognitionFailed;
        }

        _captions.Stop();

        _teardownTask = Task.Run(() => TeardownComponents(capture, speechToText));
        return true;
    }

    /// <summary>
    /// Stops and disposes the session components. Runs on a background task so a blocking Whisper
    /// stop/dispose never stalls the caller (typically the WPF UI thread).
    /// </summary>
    private static void TeardownComponents(IAudioCapture? capture, ISpeechToTextEngine? speechToText)
    {
        try
        {
            speechToText?.Stop();
            capture?.Stop();
            capture?.Dispose();
            speechToText?.Dispose();
        }
        catch
        {
            // Teardown is best-effort: a failing component must not prevent the rest from stopping.
        }
    }

    private IAudioCapture? CreateCapture(string? deviceId)
    {
        try
        {
            return _captureFactory(string.IsNullOrWhiteSpace(deviceId) ? null : deviceId);
        }
        catch (AudioCaptureException ex)
        {
            RaiseStatus(new PipelineStatus(PipelineStatusKind.Error, ex.Message));
            return null;
        }
    }

    private ISpeechToTextEngine? CreateSpeechToText(string? sttLanguage)
    {
        try
        {
            return _speechToTextFactory(string.IsNullOrWhiteSpace(sttLanguage) ? null : sttLanguage);
        }
        catch (ArgumentException ex)
        {
            RaiseStatus(new PipelineStatus(PipelineStatusKind.Error, ex.Message));
            return null;
        }
    }

    private void OnAudioAvailable(object? sender, AudioChunk chunk)
    {
        try
        {
            UniversalCaptions.Core.Diagnostics.DiagnosticTracer.Record(1, "First non-silent audio chunk reaches capture pipeline");

            if (_processor.TryProcess(chunk, out AudioChunk? processed) && processed is not null)
            {
                UniversalCaptions.Core.Diagnostics.DiagnosticTracer.Record(2, "First audio chunk dispatched to Whisper");
                _speechToText?.Process(processed);
            }
        }
        catch (Exception ex)
        {
            // This handler runs on the capture callback thread; an exception must not escape into
            // the audio stack. Surface it through the same failure path as capture/recognition errors.
            _faulted = true;
            RaiseStatus(new PipelineStatus(PipelineStatusKind.Error, $"Audio processing failed: {ex.Message}"));
            if (!_starting)
            {
                Stop();
            }
        }
    }

    private void OnPartialTranscript(object? sender, PartialTranscript transcript)
    {
        UniversalCaptions.Core.Diagnostics.DiagnosticTracer.Record(3, "First Whisper Partial result");
        _captions.ProcessPartial(transcript);
    }

    private void OnFinalTranscript(object? sender, FinalTranscript transcript)
    {
        UniversalCaptions.Core.Diagnostics.DiagnosticTracer.Record(4, "First Whisper Final result");
        _captions.ProcessFinal(transcript);
        LatencyUpdated?.Invoke(this, transcript.Latency);
    }

    /// <summary>
    /// Computes end-to-end latency when a translated caption is published. Only lines whose translation
    /// completed successfully produce a sample (failed and untranslated lines have no translated caption
    /// to show); stale/cancelled results never reach this handler because the caption service discards
    /// them before raising <c>CaptionLineUpdated</c>.
    /// </summary>
    private void OnCaptionLineUpdated(object? sender, CaptionLine line)
    {
        if (line.TranslatedText is null || line.TranslationCompletedAtUtc is not DateTime completed)
        {
            return;
        }

        UniversalCaptions.Core.Diagnostics.DiagnosticTracer.Record(6, "First translation result");

        DateTime started = line.TranslationStartedAtUtc ?? completed;
        var kind = line.State == CaptionLineState.Active ? EndToEndLatencyKind.Partial : EndToEndLatencyKind.Final;
        EndToEndLatencyUpdated?.Invoke(this, new EndToEndLatencySample(
            kind,
            completed - line.CapturedAtUtc,
            completed - started));
    }

    private void OnRecognitionFailed(object? sender, SpeechRecognitionError error)
    {
        _faulted = true;
        RaiseStatus(new PipelineStatus(PipelineStatusKind.Error, error.Message));
        if (!_starting)
        {
            Stop();
        }
    }

    private void OnCaptureFailed(object? sender, AudioCaptureError error)
    {
        _faulted = true;
        RaiseStatus(new PipelineStatus(PipelineStatusKind.Error, error.Message));
        if (!_starting)
        {
            Stop();
        }
    }

    private void RaiseStatus(PipelineStatus status) => StatusChanged?.Invoke(this, status);
}
