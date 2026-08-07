using System.Diagnostics;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Capture;
using UniversalCaptions.Core.Processing;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Core.Translation;

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
/// <para>
/// TD-002 auto-recovery: when a <see cref="IDeviceChangeMonitor"/> is supplied and the live session
/// captures the system default render device, a <see cref="DefaultDeviceAutoRecovery"/> coordinator
/// restarts the capture-only half of the session when the default device changes or the endpoint is
/// removed/unplugged. The speech engine is preserved (engine and model are never changed) and the
/// recovery re-queries the default device, so captions resume after a hotplug without user action.
/// Explicitly chosen (non-default) devices never auto-recover — the user's choice is preserved.
/// </para>
/// </remarks>
public sealed class CaptionPipeline : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<string?, IAudioCapture> _captureFactory;
    private readonly IAudioProcessor _processor;
    private readonly Func<string?, ISpeechToTextEngine> _speechToTextFactory;
    private readonly ICaptionService _captions;
    private readonly IDeviceChangeMonitor? _monitor;
    private readonly DefaultDeviceAutoRecovery? _recovery;
    private readonly Func<(string? SourceLanguage, string? TargetLanguage), ILiveAudioTranslationEngine?>? _liveTranslationFactory;
    private readonly string? _sourceLanguage;
    private readonly string? _targetLanguage;

    private IAudioCapture? _capture;
    private ISpeechToTextEngine? _speechToText;
    private ILiveAudioTranslationEngine? _liveTranslation;
    // Event delegates captured at subscription time so the failure path can detach the exact same
    // handlers without depending on a public remove API (events only expose -= from inside the
    // declaring type — these references are the standard solution when subscribing from outside).
    private EventHandler<PartialTranslation>? _onPartialTranslation;
    private EventHandler<FinalTranslation>? _onFinalTranslation;
    private EventHandler<LiveTranslationError>? _onLiveTranslationFailed;
    private string? _deviceId;
    private Task? _teardownTask;
    private bool _faulted;
    private bool _starting;
    private bool _restarting;
    private bool _disposed;

    /// <summary>
    /// Creates a pipeline that produces a fresh capture source and speech engine per session.
    /// </summary>
    /// <param name="captureFactory">Creates the capture source for a device (null = system default).</param>
    /// <param name="processor">Converts captured audio to the speech engine's format.</param>
    /// <param name="speechToTextFactory">Creates the speech engine for a language hint (null = auto-detect).</param>
    /// <param name="captions">The caption service the pipeline feeds transcripts into.</param>
    /// <param name="monitor">
    /// Optional device-change source for TD-002 auto-recovery. When null (or when the session captures
    /// an explicitly chosen device), no notification restarts the session.
    /// </param>
    /// <param name="liveTranslationFactory">
    /// Optional factory that produces an <see cref="ILiveAudioTranslationEngine"/> for the configured
    /// source/target language pair. When null, or when the factory itself returns null (no provider
    /// configured), the pipeline runs the offline-only path: PCM is not fanned out to a live
    /// translator and no <see cref="ILiveAudioTranslationEngine.PartialTranslationAvailable"/> /
    /// <see cref="ILiveAudioTranslationEngine.FinalTranslationAvailable"/> events are produced. A live
    /// translation engine failure (raised through <see cref="ILiveAudioTranslationEngine.TranslationFailed"/>)
    /// never faults the caption pipeline or stops Whisper — only the live translation engine itself is
    /// stopped and disposed, and a status is raised so the UI can surface the failure.
    /// </param>
    /// <param name="sourceLanguage">The source language passed to the live translation factory, when known.</param>
    /// <param name="targetLanguage">The target language passed to the live translation factory, when known.</param>
    public CaptionPipeline(
        Func<string?, IAudioCapture> captureFactory,
        IAudioProcessor processor,
        Func<string?, ISpeechToTextEngine> speechToTextFactory,
        ICaptionService captions,
        IDeviceChangeMonitor? monitor = null,
        Func<(string? SourceLanguage, string? TargetLanguage), ILiveAudioTranslationEngine?>? liveTranslationFactory = null,
        string? sourceLanguage = null,
        string? targetLanguage = null)
    {
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _speechToTextFactory = speechToTextFactory ?? throw new ArgumentNullException(nameof(speechToTextFactory));
        _captions = captions ?? throw new ArgumentNullException(nameof(captions));
        _monitor = monitor;
        _liveTranslationFactory = liveTranslationFactory;
        _sourceLanguage = NormalizeLanguage(sourceLanguage);
        _targetLanguage = NormalizeLanguage(targetLanguage);
        if (monitor is not null)
        {
            _recovery = new DefaultDeviceAutoRecovery(monitor, () => IsOnDefaultDevice, _ => RestartCaptureAsync());
        }

        _captions.CaptionLineUpdated += OnCaptionLineUpdated;
    }

    private static string? NormalizeLanguage(string? language)
    {
        return string.IsNullOrWhiteSpace(language) ? null : language.Trim().ToLowerInvariant();
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
    /// True while the live session is capturing the system default render device — the only state in
    /// which TD-002 auto-recovery may restart the session. False when stopped, faulted, disposed, or
    /// capturing an explicitly chosen device.
    /// </summary>
    public bool IsOnDefaultDevice =>
        !_disposed && !_faulted && _deviceId is null && _capture?.IsCapturing == true;

    /// <summary>
    /// Starts a caption session on the given device with the given speech-language hint.
    /// </summary>
    /// <param name="deviceId">The Windows endpoint ID of the render device, or null for the system default.</param>
    /// <param name="sttLanguage">An optional speech-language hint (null = auto-detect).</param>
    public void Start(string? deviceId, string? sttLanguage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning || _restarting)
        {
            return;
        }

        _faulted = false;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;

        TempaudioLatencyProbe.RecordCaptureStarted();

        _capture = CreateCapture(_deviceId);
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
            // Live-translation engine is created AFTER speech has started and capture is live, so a
            // failure to create it (e.g. provider is unset / factory returns null) cannot fault the
            // offline path. The engine is the sole property of the pipeline — we own StartAsync /
            // StopAsync / Dispose, and TranslationFailed only tears the engine down without setting
            // _faulted or stopping Whisper.
            if (_liveTranslationFactory is not null && _sourceLanguage is not null && _targetLanguage is not null)
            {
                _liveTranslation = CreateLiveTranslation(_sourceLanguage, _targetLanguage);
                if (_liveTranslation is not null)
                {
                    // Capture delegate fields first so StopSessionLocked and the failure handler
                    // can unsubscribe the exact same handler instances.
                    _onPartialTranslation = OnPartialTranslation;
                    _onFinalTranslation = OnFinalTranslation;
                    _onLiveTranslationFailed = OnLiveTranslationFailed;
                    _liveTranslation.PartialTranslationAvailable += _onPartialTranslation;
                    _liveTranslation.FinalTranslationAvailable += _onFinalTranslation;
                    _liveTranslation.TranslationFailed += _onLiveTranslationFailed;

                    try
                    {
                        // StartAsync is awaited from the UI thread; a slow cloud handshake is bounded
                        // by the user's perceived Start latency. The factory has already produced the
                        // engine so this is the only failure mode that can fault the live path.
                        _liveTranslation.StartAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        // Treat the synchronous StartAsync failure identically to a TranslationFailed
                        // event: stop the engine, dispose it, raise status, leave Whisper running.
                        DetachAndDisposeLiveTranslation();
                        RaiseStatus(new PipelineStatus(
                            PipelineStatusKind.Error,
                            $"Live translation unavailable: {ex.Message}"));
                    }
                }
            }

            TempaudioLatencyProbe.RecordDeviceStarted();
            UniversalCaptions.Core.Diagnostics.DiagnosticTracer.StartSession();
            if (_monitor is not null && _deviceId is null)
            {
                _monitor.Start();
            }

            RaiseStatus(new PipelineStatus(PipelineStatusKind.Capturing, "Capturing system audio…"));
        }
    }

    /// <summary>
    /// Restarts the capture-only half of a live default-device session (TD-002): detaches and disposes
    /// the current capture source, re-queries the system default device, and recreates and starts a
    /// capture chain on it. The speech engine is preserved unchanged — only the WASAPI capture source
    /// is recreated — so transcripts continue across a device hotplug without restarting the model.
    /// No-op when the session is stopped, faulted, disposed, on an explicit device, or already
    /// restarting (coalesced by the recovery coordinator). Completion means the new capture is live
    /// or the session was stopped in a controlled error state.
    /// </summary>
    public async Task RestartCaptureAsync()
    {
        IAudioCapture? oldCapture;
        ISpeechToTextEngine? speechToText;
        lock (_gate)
        {
            if (_disposed || _faulted || _restarting || _deviceId is not null)
            {
                return;
            }

            oldCapture = _capture;
            speechToText = _speechToText;
            if (oldCapture is null || speechToText is null || !oldCapture.IsCapturing)
            {
                return;
            }

            _restarting = true;
            _capture = null;
            oldCapture.AudioAvailable -= OnAudioAvailable;
            oldCapture.CaptureFailed -= OnCaptureFailed;
        }

        // Yield so the notification callback thread is not tied up while the stale capture is torn
        // down and recreated; the coordinator is fire-and-forget and coalesces further notifications.
        await Task.Yield();

        try
        {
            oldCapture.Stop();
            oldCapture.Dispose();
        }
        catch
        {
            // Best-effort teardown of the stale capture; recovery continues with the new device.
        }

        IAudioCapture? newCapture = CreateCapture(_deviceId);
        if (newCapture is null)
        {
            // No default device to recover to: surface the error (CreateCapture already raised the
            // status) and stop the session in a controlled error state.
            lock (_gate)
            {
                _restarting = false;
            }

            _faulted = true;
            Stop();
            return;
        }

        bool discard;
        lock (_gate)
        {
            // A Stop/Dispose that landed while the restart was in flight is detected by the speech
            // engine being detached; the freshly created capture must not resurrect the session.
            if (_disposed || _faulted || !ReferenceEquals(_speechToText, speechToText))
            {
                discard = true;
            }
            else
            {
                _capture = newCapture;
                discard = false;
            }
        }

        if (discard)
        {
            newCapture.Dispose();
            lock (_gate)
            {
                _restarting = false;
            }

            return;
        }

        newCapture.AudioAvailable += OnAudioAvailable;
        newCapture.CaptureFailed += OnCaptureFailed;

        try
        {
            _starting = true;
            newCapture.Start();
        }
        finally
        {
            _starting = false;
            lock (_gate)
            {
                _restarting = false;
            }
        }

        if (_faulted)
        {
            Stop();
            return;
        }

        if (newCapture.IsCapturing)
        {
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
        _recovery?.Dispose();
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
        ILiveAudioTranslationEngine? liveTranslation = _liveTranslation;
        if (capture is null && speechToText is null && liveTranslation is null && !_captions.IsRunning)
        {
            return false;
        }

        _capture = null;
        _speechToText = null;
        _liveTranslation = null;
        _deviceId = null;
        _monitor?.Stop();

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

        if (liveTranslation is not null)
        {
            if (_onPartialTranslation is not null)
            {
                liveTranslation.PartialTranslationAvailable -= _onPartialTranslation;
            }

            if (_onFinalTranslation is not null)
            {
                liveTranslation.FinalTranslationAvailable -= _onFinalTranslation;
            }

            if (_onLiveTranslationFailed is not null)
            {
                liveTranslation.TranslationFailed -= _onLiveTranslationFailed;
            }
        }

        _onPartialTranslation = null;
        _onFinalTranslation = null;
        _onLiveTranslationFailed = null;

        _captions.Stop();

        _teardownTask = Task.Run(() => TeardownComponents(capture, speechToText, liveTranslation));
        return true;
    }

    /// <summary>
    /// Stops and disposes the session components. Runs on a background task so a blocking Whisper
    /// stop/dispose never stalls the caller (typically the WPF UI thread). The live translation engine
    /// is stopped asynchronously; an await on its own receive loop would deadlock, so the failure
    /// path (DetachAndDisposeLiveTranslation) does NOT call StopAsync at all — only Dispose, after
    /// detaching the events so a Dispose from this teardown path is idempotent.
    /// </summary>
    private static void TeardownComponents(IAudioCapture? capture, ISpeechToTextEngine? speechToText, ILiveAudioTranslationEngine? liveTranslation)
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

        if (liveTranslation is not null)
        {
            try
            {
                liveTranslation.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Stop failures are best-effort: dispose below still runs.
            }

            try
            {
                liveTranslation.Dispose();
            }
            catch
            {
                // Dispose failures are best-effort.
            }
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

    private ILiveAudioTranslationEngine? CreateLiveTranslation(string sourceLanguage, string targetLanguage)
    {
        try
        {
            return _liveTranslationFactory!((sourceLanguage, targetLanguage));
        }
        catch (Exception ex)
        {
            RaiseStatus(new PipelineStatus(PipelineStatusKind.Error, $"Live translation unavailable: {ex.Message}"));
            return null;
        }
    }

    private void OnAudioAvailable(object? sender, AudioChunk chunk)
    {
        try
        {
            TempaudioLatencyProbe.RecordChunk(chunk);
            UniversalCaptions.Core.Diagnostics.DiagnosticTracer.Record(1, "First non-silent audio chunk reaches capture pipeline");

            if (_processor.TryProcess(chunk, out AudioChunk? processed) && processed is not null)
            {
                TempaudioLatencyProbe.RecordDispatch();
                UniversalCaptions.Core.Diagnostics.DiagnosticTracer.Record(2, "First audio chunk dispatched to Whisper");
                _speechToText?.Process(processed);

                // Parallel PCM fan-out: the same processed chunk is offered to the live translation
                // engine. The engine owns its own bounded queue and drop policy (deferred to A6), so
                // PushAudio is synchronous and MUST NOT do network I/O / throw — a failure surfaces
                // via TranslationFailed, not exceptions caught here.
                _liveTranslation?.PushAudio(processed);
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

    private void OnPartialTranslation(object? sender, PartialTranslation translation)
    {
        _captions.ProcessPartialTranslation(translation);
    }

    private void OnFinalTranslation(object? sender, FinalTranslation translation)
    {
        _captions.ProcessFinalTranslation(translation);
    }

    /// <summary>
    /// Live translation failure — isolated from the Whisper pipeline. The handler is non-blocking on
    /// purpose: a failing receive loop must not be awaited from inside its own callback. We detach
    /// the events, raise a status for the UI, null the field, and fire-and-forget the dispose on a
    /// background task. Whisper continues running; the pipeline is NOT marked faulted.
    /// </summary>
    private void OnLiveTranslationFailed(object? sender, LiveTranslationError error)
    {
        DetachAndDisposeLiveTranslation();
        RaiseStatus(new PipelineStatus(
            PipelineStatusKind.Error,
            $"Live translation unavailable: {error.Message}"));
    }

    /// <summary>
    /// Detaches the live translation events, clears the field, and disposes the engine on a
    /// background task. Must be idempotent: called from the failure handler AND from the synchronous
    /// StartAsync failure path AND from <see cref="StopSessionLocked"/> is the normal path for the
    /// latter (StopSessionLocked passes the local capture). This overload is the single-side
    /// "unsubscribe + null + async dispose" used by the failure paths; it does NOT touch the Whisper
    /// pipeline state.
    /// </summary>
    private void DetachAndDisposeLiveTranslation()
    {
        ILiveAudioTranslationEngine? engine;
        EventHandler<PartialTranslation>? partial;
        EventHandler<FinalTranslation>? final;
        EventHandler<LiveTranslationError>? failed;

        lock (_gate)
        {
            engine = _liveTranslation;
            if (engine is null)
            {
                return;
            }

            _liveTranslation = null;
            partial = _onPartialTranslation;
            final = _onFinalTranslation;
            failed = _onLiveTranslationFailed;
            _onPartialTranslation = null;
            _onFinalTranslation = null;
            _onLiveTranslationFailed = null;

            if (partial is not null)
            {
                engine.PartialTranslationAvailable -= partial;
            }

            if (final is not null)
            {
                engine.FinalTranslationAvailable -= final;
            }

            if (failed is not null)
            {
                engine.TranslationFailed -= failed;
            }
        }

        // Dispose on a background task: the engine may be on its own receive loop and we are
        // running inside its callback. We deliberately do NOT await StopAsync here — the failure
        // event came from inside the engine, so waiting on its own loop would deadlock.
        _ = Task.Run(() =>
        {
            try
            {
                engine.Dispose();
            }
            catch
            {
                // Dispose is best-effort: a failing engine must not affect the offline pipeline.
            }
        });
    }

    private void OnPartialTranscript(object? sender, PartialTranscript transcript)
    {
        TempaudioLatencyProbe.RecordPartial();
        UniversalCaptions.Core.Diagnostics.DiagnosticTracer.Record(3, "First Whisper Partial result");
        _captions.ProcessPartial(transcript);
    }

    private void OnFinalTranscript(object? sender, FinalTranscript transcript)
    {
        TempaudioLatencyProbe.RecordFinal();
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
