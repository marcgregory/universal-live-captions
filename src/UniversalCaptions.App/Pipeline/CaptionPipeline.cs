using System.Diagnostics;
using UniversalCaptions.App.Settings;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Capture;
using UniversalCaptions.Core.Processing;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.App.Pipeline;

/// <summary>
/// Wires the audio pipeline — capture → processor → Gemini Live (transcription + translation) →
/// caption service — and owns a single capture session at a time. All dependencies are the Core
/// contracts (or factories that produce them), so the wiring can be verified deterministically
/// against fakes. Status and latency are surfaced as events; the UI marshals them to the dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0011: Gemini Live is the pipeline's ONLY speech engine. The same live session produces both
/// surfaces — source-language input transcription (fed to the caption service as source captions)
/// and target-language translation (relayed as translation-origin lines). There is no local STT and
/// no offline translation path; when the engine cannot be created (e.g. no stored API key) the
/// session fails fast with an actionable status instead of degrading to source-only captions.
/// </para>
/// <para>
/// The user's translation toggle does NOT stop the Gemini session: toggling off only suppresses
/// translation-origin lines at the caption service (source captions keep flowing), because the
/// session is also the transcription source. Only the target language recycles the engine (it is
/// part of the session setup).
/// </para>
/// <para>
/// <see cref="Stop"/>/<see cref="Dispose"/> detach the session and return immediately: the capture
/// source and the live engine are stopped and disposed on a background task, so the calling thread —
/// the WPF UI thread in the app — never blocks on the audio pipeline (ARCHITECTURE state-management
/// rule). <see cref="StopAsync"/> returns the in-flight teardown and <see cref="Dispose"/> waits for
/// it, so shutdown is deterministic without stalling the UI.
/// </para>
/// <para>
/// TD-002 auto-recovery: when a <see cref="IDeviceChangeMonitor"/> is supplied and the live session
/// captures the system default render device, a <see cref="DefaultDeviceAutoRecovery"/> coordinator
/// restarts the capture-only half of the session when the default device changes or the endpoint is
/// removed/unplugged. The Gemini engine is preserved (the session is never changed) and the recovery
/// re-queries the default device, so captions resume after a hotplug without user action.
/// Explicitly chosen (non-default) devices never auto-recover — the user's choice is preserved.
/// </para>
/// </remarks>
public sealed class CaptionPipeline : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<string?, IAudioCapture> _captureFactory;
    private readonly IAudioProcessor _processor;
    private readonly ICaptionService _captions;
    private readonly IDeviceChangeMonitor? _monitor;
    private readonly DefaultDeviceAutoRecovery? _recovery;
    private readonly Func<(string? SourceLanguage, string? TargetLanguage), ILiveAudioTranslationEngine?> _liveTranslationFactory;
    private readonly Func<(string? SourceLanguage, string? TargetLanguage), ILiveAudioTranslationEngine?>? _sourceOnlyFactory;
    private readonly string? _sourceLanguage;
    private readonly string? _targetLanguage;

    private IAudioCapture? _capture;
    private ILiveAudioTranslationEngine? _liveTranslation;
    // Per-session configuration, supplied by the caller of Start (the control window passes the
    // user's language selections). Distinct from the readonly constructor defaults so each session
    // can react to UI changes without rebuilding the pipeline.
    private string? _liveSourceLanguage;
    private string? _liveTargetLanguage;
    // Mirrors the caption service's common translation state: when false, translation-origin events
    // from the Gemini session are dropped before they reach the caption service (the service would
    // reject them anyway; gating here avoids the state-changed churn).
    private bool _translationEnabled;
    private bool _sourceOnlyMode;
    private bool _sourceOnlyFallbackStarted;
    // Event delegates captured at subscription time so the failure path can detach the exact same
    // handlers without depending on a public remove API (events only expose -= from inside the
    // declaring type — these references are the standard solution when subscribing from outside).
    private EventHandler<PartialTranscript>? _onPartialTranscription;
    private EventHandler<FinalTranscript>? _onFinalTranscription;
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
    /// Creates a pipeline that produces a fresh capture source and Gemini Live session per start.
    /// </summary>
    /// <param name="captureFactory">Creates the capture source for a device (null = system default).</param>
    /// <param name="processor">Converts captured audio to the engine's format.</param>
    /// <param name="captions">The caption service the pipeline feeds transcripts into.</param>
    /// <param name="liveTranslationFactory">
    /// Factory that produces the session's single speech engine (Gemini Live) for the configured
    /// source/target language pair. Returning null (no API key configured) or throwing fails the
    /// session start with an actionable status — there is no offline fallback path (ADR-0011).
    /// </param>
    /// <param name="monitor">
    /// Optional device-change source for TD-002 auto-recovery. When null (or when the session captures
    /// an explicitly chosen device), no notification restarts the session.
    /// </param>
    /// <param name="sourceLanguage">The fallback source language passed to the factory when the session does not supply one.</param>
    /// <param name="targetLanguage">The fallback target language passed to the factory when the session does not supply one.</param>
    public CaptionPipeline(
        Func<string?, IAudioCapture> captureFactory,
        IAudioProcessor processor,
        ICaptionService captions,
        Func<(string? SourceLanguage, string? TargetLanguage), ILiveAudioTranslationEngine?> liveTranslationFactory,
        IDeviceChangeMonitor? monitor = null,
        string? sourceLanguage = null,
        string? targetLanguage = null,
        Func<(string? SourceLanguage, string? TargetLanguage), ILiveAudioTranslationEngine?>? sourceOnlyFactory = null)
    {
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _captions = captions ?? throw new ArgumentNullException(nameof(captions));
        _liveTranslationFactory = liveTranslationFactory ?? throw new ArgumentNullException(nameof(liveTranslationFactory));
        _sourceOnlyFactory = sourceOnlyFactory;
        _monitor = monitor;
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
    /// <see cref="LatencyUpdated"/>, which measures final-transcript production only.
    /// </summary>
    public event EventHandler<EndToEndLatencySample>? EndToEndLatencyUpdated;

    /// <summary>
    /// Raised when the Gemini session reports a classified failure — invalid/missing API key, quota,
    /// network, or server error. Carries the categorized <see cref="LiveTranslationError"/> so the UI
    /// can surface an actionable message. Distinct from <see cref="StatusChanged"/>, which also fires
    /// but only carries a display string.
    /// </summary>
    public event EventHandler<LiveTranslationError>? LiveTranslationErrorUpdated;

    /// <summary>True while a capture session is running.</summary>
    public bool IsRunning => _capture?.IsCapturing == true;
    /// <summary>True when a Gemini Live session is attached to the running capture.</summary>
    public bool HasLiveTranslationSession
    {
        get
        {
            lock (_gate)
            {
                return _capture?.IsCapturing == true && _liveTranslation is not null;
            }
        }
    }

    /// <summary>Reconnects Gemini without stopping WASAPI capture after a recoverable server session end.</summary>
    public async Task RestartLiveTranslationAsync()
    {
        IAudioCapture? capture;
        string? sourceLanguage;
        string? targetLanguage;

        lock (_gate)
        {
            if (_disposed || _faulted || _restarting || _capture?.IsCapturing != true || _liveTranslation is not null)
            {
                return;
            }

            _restarting = true;
            capture = _capture;
            sourceLanguage = _liveSourceLanguage;
            targetLanguage = _liveTargetLanguage;
        }

        RaiseStatus(new PipelineStatus(PipelineStatusKind.Error, "Gemini session ended. Reconnecting…"));

        ILiveAudioTranslationEngine? candidate = null;
        try
        {
            candidate = await Task.Run(() => CreateAndStartLiveTranslation(sourceLanguage, targetLanguage)).ConfigureAwait(false);
            if (candidate is null)
            {
                return;
            }

            bool attach;
            lock (_gate)
            {
                attach = !_disposed
                    && !_faulted
                    && ReferenceEquals(_capture, capture)
                    && capture?.IsCapturing == true
                    && _liveTranslation is null;

                if (attach)
                {
                    _liveTranslation = candidate;
                }
            }

            if (!attach)
            {
                StopLiveTranslationEngine(candidate);
                candidate = null;
                return;
            }

            SubscribeLiveEvents(candidate);
            SyncLiveTranslationSession();
            RaiseStatus(new PipelineStatus(PipelineStatusKind.Capturing, "Capturing system audio…"));
            candidate = null;
        }
        finally
        {
            lock (_gate)
            {
                _restarting = false;
            }

            if (candidate is not null)
            {
                StopLiveTranslationEngine(candidate);
            }
        }
    }

    /// <summary>
    /// True while the live session is capturing the system default render device — the only state in
    /// which TD-002 auto-recovery may restart the session. False when stopped, faulted, disposed, or
    /// capturing an explicitly chosen device.
    /// </summary>
    public bool IsOnDefaultDevice =>
        !_disposed && !_faulted && _deviceId is null && _capture?.IsCapturing == true;

    /// <summary>
    /// Starts a caption session on the given device. The Gemini Live session is created first — it is
    /// the only speech engine, so a failure to construct or start it (missing API key, rejected key,
    /// no network) aborts the start with an actionable error status and no capture begins.
    /// </summary>
    /// <param name="deviceId">The Windows endpoint ID of the render device, or null for the system default.</param>
    /// <param name="sourceLanguage">The source language hint for transcription/translation (null = auto-detect).</param>
    /// <param name="targetLanguage">The translation target language.</param>
    /// <param name="translationEnabled">
    /// Whether translated captions are displayed. When false the Gemini session still runs (it is
    /// also the transcription source) but translation-origin lines are suppressed.
    /// </param>
    public void Start(
        string? deviceId,
        string? sourceLanguage,
        string? targetLanguage,
        bool translationEnabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning || _restarting)
        {
            return;
        }

        _faulted = false;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;

        // Capture the per-session configuration. The UI-provided values win; the readonly constructor
        // defaults are only a fallback when a session does not supply them.
        _liveSourceLanguage = NormalizeLanguage(sourceLanguage) ?? _sourceLanguage;
        _liveTargetLanguage = NormalizeLanguage(targetLanguage) ?? _targetLanguage;
        _translationEnabled = translationEnabled;
        _sourceOnlyMode = false;
        _sourceOnlyFallbackStarted = false;

        TempaudioLatencyProbe.RecordCaptureStarted();

        ILiveAudioTranslationEngine? engine = CreateAndStartLiveTranslation(_liveSourceLanguage, _liveTargetLanguage);
        if (engine is null)
        {
            // CreateAndStartLiveTranslation already raised the actionable error status + classified
            // LiveTranslationErrorUpdated. Without Gemini there are no captions at all (ADR-0011).
            return;
        }

        IAudioCapture? capture = CreateCapture(_deviceId);
        if (capture is null)
        {
            StopLiveTranslationEngine(engine);
            return;
        }

        lock (_gate)
        {
            _liveTranslation = engine;
            _capture = capture;
        }

        capture.AudioAvailable += OnAudioAvailable;
        capture.CaptureFailed += OnCaptureFailed;
        SubscribeLiveEvents(engine);

        _captions.Start();
        _captions.SetTranslationEnabled(translationEnabled, _liveTargetLanguage);
        SyncLiveTranslationSession();

        try
        {
            _starting = true;
            capture.Start();
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

        if (capture.IsCapturing)
        {
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
    /// capture chain on it. The Gemini engine is preserved unchanged — only the WASAPI capture source
    /// is recreated — so transcripts continue across a device hotplug without restarting the session.
    /// No-op when the session is stopped, faulted, disposed, on an explicit device, or already
    /// restarting (coalesced by the recovery coordinator). Completion means the new capture is live
    /// or the session was stopped in a controlled error state.
    /// </summary>
    public async Task RestartCaptureAsync()
    {
        IAudioCapture? oldCapture;
        ILiveAudioTranslationEngine? liveTranslation;
        lock (_gate)
        {
            if (_disposed || _faulted || _restarting || _deviceId is not null)
            {
                return;
            }

            oldCapture = _capture;
            liveTranslation = _liveTranslation;
            if (oldCapture is null || liveTranslation is null || !oldCapture.IsCapturing)
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
            // A Stop/Dispose that landed while the restart was in flight is detected by the Gemini
            // engine being detached; the freshly created capture must not resurrect the session.
            if (_disposed || _faulted || !ReferenceEquals(_liveTranslation, liveTranslation))
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
    /// Stops the running session and disposes its capture source and Gemini engine without blocking
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
    /// Toggles translated-caption display without touching the running Gemini session (the session
    /// is also the transcription source, so it must keep running). Enabling applies the current
    /// target language; disabling scrubs translation-origin content at the caption service.
    /// No-op when the session is not capturing.
    /// </summary>
    public void SetTranslationEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_capture?.IsCapturing != true)
            {
                return;
            }

            _translationEnabled = enabled;
        }

        _captions.SetTranslationEnabled(enabled, _liveTargetLanguage);
    }

    /// <summary>
    /// Swaps the translation target language of a running session. The target is part of the Gemini
    /// session setup, so the engine is stopped and a new one connected with the new target; source
    /// captions pause only for the swap's duration. No-op when the session is not capturing or the
    /// desired target already matches. A failed swap raises an error status and stops the session
    /// (without Gemini there is no caption source to fall back to).
    /// </summary>
    public void SetTargetLanguage(string? targetLanguage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? normTarget = NormalizeLanguage(targetLanguage);

        ILiveAudioTranslationEngine? oldEngine;
        lock (_gate)
        {
            if (_disposed || _capture?.IsCapturing != true)
            {
                return;
            }

            if (string.Equals(_liveTargetLanguage, normTarget, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Detach the current engine under the lock so the capture callback can no longer reach it.
            oldEngine = _liveTranslation;
            _liveTranslation = null;
            UnsubscribeLiveEvents(oldEngine, out _, out _, out _, out _, out _);
            _liveTargetLanguage = normTarget;
        }

        if (oldEngine is not null)
        {
            // Graceful stop off the capture callback (we are on the UI thread, not inside the
            // engine's receive loop), so the tail-flush finals are dropped by the detached events.
            if (oldEngine is not null)
        {
            StopLiveTranslationEngine(oldEngine);
        }
        }

        ILiveAudioTranslationEngine? engine = CreateAndStartLiveTranslation(_liveSourceLanguage, normTarget);
        if (engine is null)
        {
            _faulted = true;
            Stop();
            return;
        }

        lock (_gate)
        {
            _liveTranslation = engine;
        }

        SubscribeLiveEvents(engine);
        _captions.SetTranslationEnabled(_translationEnabled, normTarget);
        SyncLiveTranslationSession();
    }

    /// <summary>
    /// Detaches the current session and launches its teardown on a background task. Returns true
    /// when a session was stopped (and a teardown started); false when there was nothing to stop.
    /// Callers must hold <see cref="_gate"/>.
    /// </summary>
    private bool StopSessionLocked()
    {
        IAudioCapture? capture = _capture;
        ILiveAudioTranslationEngine? liveTranslation = _liveTranslation;
        if (capture is null && liveTranslation is null && !_captions.IsRunning)
        {
            return false;
        }

        _capture = null;
        _liveTranslation = null;
        _deviceId = null;
        _monitor?.Stop();

        if (capture is not null)
        {
            capture.AudioAvailable -= OnAudioAvailable;
            capture.CaptureFailed -= OnCaptureFailed;
        }

        UnsubscribeLiveEvents(liveTranslation, out _, out _, out _, out _, out _);

        _captions.Stop();

        _teardownTask = Task.Run(() => TeardownComponents(capture, liveTranslation));
        return true;
    }

    /// <summary>
    /// Stops and disposes the session components. Runs on a background task so a blocking stop/dispose
    /// never stalls the caller (typically the WPF UI thread). The live engine is stopped
    /// asynchronously; an await on its own receive loop would deadlock, so the failure path
    /// (DetachAndDisposeLiveTranslation) does NOT call StopAsync at all — only Dispose, after
    /// detaching the events so a Dispose from this teardown path is idempotent.
    /// </summary>
    private static void TeardownComponents(IAudioCapture? capture, ILiveAudioTranslationEngine? liveTranslation)
    {
        try
        {
            capture?.Stop();
            capture?.Dispose();
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

    /// <summary>
    /// Creates the Gemini session via the factory, starts it, and raises the actionable failure
    /// surface on error. A factory that returns null (no API key stored) raises the classified
    /// SessionRejected error so the UI can point the user at the key panel. Callers must not hold
    /// <see cref="_gate"/>.
    /// </summary>
    private ILiveAudioTranslationEngine? CreateAndStartLiveTranslation(string? sourceLanguage, string? targetLanguage)
    {
        ILiveAudioTranslationEngine? engine;
        try
        {
            engine = _liveTranslationFactory((sourceLanguage, targetLanguage));
        }
        catch (Exception ex)
        {
            RaiseStatus(new PipelineStatus(PipelineStatusKind.Error, $"Speech engine unavailable: {ex.Message}"));
            return null;
        }

        if (engine is null)
        {
            LiveTranslationError missingKey = new(
                LiveTranslationErrorKind.SessionRejected,
                "Gemini API key is missing. Add or update the key in the Control Window.",
                null);
            RaiseStatus(new PipelineStatus(PipelineStatusKind.Error, missingKey.Message));
            LiveTranslationErrorUpdated?.Invoke(this, missingKey);
            return null;
        }

        try
        {
            // StartAsync is awaited from the calling (UI) thread; a slow cloud handshake is bounded
            // by the user's perceived latency.
            engine.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Treat the synchronous StartAsync failure identically to a TranslationFailed event:
            // dispose the engine, raise status + classified error. The classification is coarse (a
            // WebSocket handshake rejection is connection-level at this seam) but the message
            // carries the actionable detail for the UI.
            var startupError = new LiveTranslationError(
                LiveTranslationErrorKind.ConnectionFailed,
                ex.Message,
                ex);
            _ = Task.Run(() =>
            {
                try
                {
                    engine.Dispose();
                }
                catch
                {
                    // Dispose is best-effort.
                }
            });
            RaiseStatus(new PipelineStatus(
                PipelineStatusKind.Error,
                $"Speech engine unavailable: {ex.Message}"));
            LiveTranslationErrorUpdated?.Invoke(this, startupError);
            return null;
        }

        return engine;
    }

    private void SubscribeLiveEvents(ILiveAudioTranslationEngine engine)
    {
        _onPartialTranscription = OnPartialTranscription;
        _onFinalTranscription = OnFinalTranscription;
        _onPartialTranslation = OnPartialTranslation;
        _onFinalTranslation = OnFinalTranslation;
        _onLiveTranslationFailed = OnLiveTranslationFailed;
        engine.PartialTranscriptionAvailable += _onPartialTranscription;
        engine.FinalTranscriptionAvailable += _onFinalTranscription;
        engine.PartialTranslationAvailable += _onPartialTranslation;
        engine.FinalTranslationAvailable += _onFinalTranslation;
        engine.TranslationFailed += _onLiveTranslationFailed;
    }

    /// <summary>
    /// Unsubscribes the captured live-event delegates from <paramref name="engine"/> and clears the
    /// fields. Safe to call with a null engine (clears the fields only).
    /// </summary>
    private void UnsubscribeLiveEvents(
        ILiveAudioTranslationEngine? engine,
        out EventHandler<PartialTranscript>? partialTranscription,
        out EventHandler<FinalTranscript>? finalTranscription,
        out EventHandler<PartialTranslation>? partialTranslation,
        out EventHandler<FinalTranslation>? finalTranslation,
        out EventHandler<LiveTranslationError>? failed)
    {
        partialTranscription = _onPartialTranscription;
        finalTranscription = _onFinalTranscription;
        partialTranslation = _onPartialTranslation;
        finalTranslation = _onFinalTranslation;
        failed = _onLiveTranslationFailed;
        _onPartialTranscription = null;
        _onFinalTranscription = null;
        _onPartialTranslation = null;
        _onFinalTranslation = null;
        _onLiveTranslationFailed = null;

        if (engine is null)
        {
            return;
        }

        if (partialTranscription is not null)
        {
            engine.PartialTranscriptionAvailable -= partialTranscription;
        }

        if (finalTranscription is not null)
        {
            engine.FinalTranscriptionAvailable -= finalTranscription;
        }

        if (partialTranslation is not null)
        {
            engine.PartialTranslationAvailable -= partialTranslation;
        }

        if (finalTranslation is not null)
        {
            engine.FinalTranslationAvailable -= finalTranslation;
        }

        if (failed is not null)
        {
            engine.TranslationFailed -= failed;
        }
    }

    /// <summary>
    /// Reflects the pipeline's live-engine state onto the caption service's live-translation-session
    /// flag, so the overlay's display mode (target-language-only vs. source+translation) is driven by
    /// the actual engine presence — never inferred from history content. Reads
    /// <see cref="_liveTranslation"/> under <see cref="_gate"/>.
    /// </summary>
    private void SyncLiveTranslationSession()
    {
        bool live;
        lock (_gate)
        {
            live = _liveTranslation is not null && !_sourceOnlyMode;
        }

        _captions.SetLiveTranslationSession(live);
    }

    /// <summary>
    /// Stops and disposes a live translation engine synchronously. Best-effort: a failing stop never
    /// prevents dispose, and a failing dispose never affects the rest of the pipeline. Safe to call from
    /// the UI thread (not from inside the engine's own receive-loop callback — that path uses
    /// <see cref="DetachAndDisposeLiveTranslation"/>).
    /// </summary>
    private static void StopLiveTranslationEngine(ILiveAudioTranslationEngine engine)
    {
        try
        {
            engine.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Stop failures are best-effort: dispose below still runs.
        }

        try
        {
            engine.Dispose();
        }
        catch
        {
            // Dispose failures are best-effort.
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
                UniversalCaptions.Core.Diagnostics.DiagnosticTracer.Record(2, "First audio chunk dispatched to the Gemini session");

                // The Gemini session is the pipeline's only speech consumer (ADR-0011): PushAudio is
                // synchronous and MUST NOT do network I/O / throw — a failure surfaces via
                // TranslationFailed, not exceptions caught here.
                _liveTranslation?.PushAudio(processed);
            }
        }
        catch (Exception ex)
        {
            // This handler runs on the capture callback thread; an exception must not escape into
            // the audio stack. Surface it through the same failure path as capture errors.
            _faulted = true;
            RaiseStatus(new PipelineStatus(PipelineStatusKind.Error, $"Audio processing failed: {ex.Message}"));
            if (!_starting)
            {
                Stop();
            }
        }
    }

    private void OnPartialTranscription(object? sender, PartialTranscript transcript)
    {
        TempaudioLatencyProbe.RecordPartial();
        UniversalCaptions.Core.Diagnostics.DiagnosticTracer.Record(3, "First Gemini partial transcription");
        _captions.ProcessPartial(transcript);
    }

    private bool ShouldUseHindiSourceOnly(string text)
    {
        if (_sourceOnlyFactory is null || _sourceOnlyFallbackStarted || _liveSourceLanguage is not null)
        {
            return false;
        }

        return string.Equals(_liveTargetLanguage, "hi", StringComparison.OrdinalIgnoreCase)
            && text.Any(ch => ch is >= '\u0900' and <= '\u097F');
    }

    private async Task SwitchToSourceOnlyAsync()
    {
        ILiveAudioTranslationEngine? oldEngine;
        lock (_gate)
        {
            if (_capture?.IsCapturing != true || _sourceOnlyMode)
            {
                return;
            }
            oldEngine = _liveTranslation;
            _liveTranslation = null;
            UnsubscribeLiveEvents(oldEngine, out _, out _, out _, out _, out _);
        }

        if (oldEngine is not null)
        {
            StopLiveTranslationEngine(oldEngine);
        }
        ILiveAudioTranslationEngine? fallback = null;
        try
        {
            fallback = await Task.Run(() => _sourceOnlyFactory!((_liveSourceLanguage, "en"))).ConfigureAwait(false);
            if (fallback is null)
            {
                return;
            }
            lock (_gate)
            {
                if (_disposed || _capture?.IsCapturing != true || _liveTranslation is not null)
                {
                    StopLiveTranslationEngine(fallback);
                    return;
                }
                _liveTranslation = fallback;
                _sourceOnlyMode = true;
                _translationEnabled = false;
            }
            SubscribeLiveEvents(fallback);
            _captions.SetTranslationEnabled(false, null);
            SyncLiveTranslationSession();
            RaiseStatus(new PipelineStatus(PipelineStatusKind.Capturing, "Captions active — translation skipped because the audio is already Hindi."));
        }
        catch (Exception ex)
        {
            if (fallback is not null)
            {
                StopLiveTranslationEngine(fallback);
            }
            RaiseStatus(new PipelineStatus(PipelineStatusKind.Error, $"Hindi source-only fallback failed: {ex.Message}"));
        }
    }

    private void OnFinalTranscription(object? sender, FinalTranscript transcript)
    {
        TempaudioLatencyProbe.RecordFinal();
        UniversalCaptions.Core.Diagnostics.DiagnosticTracer.Record(4, "First Gemini final transcription");
        _captions.ProcessFinal(transcript);
        LatencyUpdated?.Invoke(this, transcript.Latency);
    }

    private void OnPartialTranslation(object? sender, PartialTranslation translation)
    {
        if (!_translationEnabled)
        {
            return;
        }

        _captions.ProcessPartialTranslation(translation);
    }

    private void OnFinalTranslation(object? sender, FinalTranslation translation)
    {
        if (!_translationEnabled)
        {
            return;
        }

        _captions.ProcessFinalTranslation(translation);
    }

    /// <summary>
    /// Gemini session failure — the whole pipeline depends on it, but the handler stays non-blocking
    /// on purpose: a failing receive loop must not be awaited from inside its own callback. We detach
    /// the events, clear the active translation line, raise a status for the UI, and fire-and-forget
    /// the dispose on a background task. Capture keeps running until the user stops or restarts; the
    /// pipeline is NOT marked faulted by this path alone (a transient session end should not wedge
    /// the Stop button).
    /// </summary>
    /// <remarks>
    /// Clears the caption service's translation active line so the overlay stops painting a stale
    /// in-progress translation. The clear happens before detach so the cleared state is published
    /// alongside the failure status.
    /// </remarks>
    private void OnLiveTranslationFailed(object? sender, LiveTranslationError error)
    {
        _captions.ClearLiveTranslationActiveLine();
        DetachAndDisposeLiveTranslation();
        RaiseStatus(new PipelineStatus(
            PipelineStatusKind.Error,
            $"Speech engine unavailable: {error.Message}"));
        LiveTranslationErrorUpdated?.Invoke(this, error);

        // A graceful Gemini goAway/session cap is recoverable: keep WASAPI capture alive and
        // replace only the dead Live session. Manual Start uses the same path if this fails.
        if (error.Kind == LiveTranslationErrorKind.SessionEnded)
        {
            _ = RestartLiveTranslationAsync();
        }
        else if (_sourceOnlyFactory is not null && _liveSourceLanguage is null
            && string.Equals(_liveTargetLanguage, "hi", StringComparison.OrdinalIgnoreCase)
            && !_sourceOnlyMode)
        {
            _sourceOnlyFallbackStarted = true;
            _ = SwitchToSourceOnlyAsync();
        }
    }

    /// <summary>
    /// Detaches the live events, clears the field, and disposes the engine on a background task.
    /// Must be idempotent. This is the single-side "unsubscribe + null + async dispose" used by the
    /// failure paths; it does NOT touch capture state.
    /// </summary>
    private void DetachAndDisposeLiveTranslation()
    {
        ILiveAudioTranslationEngine? engine;

        lock (_gate)
        {
            engine = _liveTranslation;
            if (engine is null)
            {
                return;
            }

            _liveTranslation = null;
            UnsubscribeLiveEvents(engine, out _, out _, out _, out _, out _);
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
                // Dispose is best-effort: a failing engine must not affect the rest of the pipeline.
            }
        });

        // The live engine is gone (failure path): reflect that on the caption service so the
        // overlay leaves target-only display mode.
        SyncLiveTranslationSession();
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
