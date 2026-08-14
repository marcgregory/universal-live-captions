using System.Threading.Channels;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Processing;
using UniversalCaptions.Core.Speech;

namespace UniversalCaptions.Speech;

/// <summary>
/// A segment-based <see cref="ISpeechToTextEngine"/> for faster-whisper: a C#-side voice-activity
/// detector + segment state machine decides where speech segments end, and each completed segment is
/// decoded exactly once through a persistent faster-whisper worker. Unlike the windowed engines this
/// does not re-decode a sliding window — every segment yields one FINAL, so the stale 20–40 s commit
/// backlog of the windowed path is replaced by commits paced by natural speech pauses. While a segment
/// is still in progress the engine ALSO decodes a bounded trailing window of the live buffer on a
/// cadence and raises live partials, so captions appear while the speaker is still talking
/// (Chrome-Live-Caption-style incremental updates) without changing the worker wire protocol. The
/// engine composes the Core <see cref="IVoiceActivityDetector"/> contract; the concrete VAD is supplied
/// at the composition root (Speech does not reference Audio).
/// </summary>
public sealed class FasterWhisperNativeStreamingEngine : ISpeechToTextEngine, IAsyncDisposable
{
    private readonly FasterWhisperEngineOptions _options;
    private readonly IFasterWhisperProcess _process;
    private readonly bool _ownedProcess;
    private readonly IVoiceActivityDetector _voiceActivityDetector;
    private readonly SpeechSegmentDetector _segmentDetector;
    private readonly TimeSpan _stopWaitTimeout;
    private readonly int _partialIntervalFrames;
    private readonly int _partialWindowFrames;

    private readonly object _gate = new();
    private Channel<WorkItem?>? _channel;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _invalidFormatReported;
    private long _sequence;
    private long _sessionId;
    private bool _partialQueued;
    private long _samplesSincePartial;

    private sealed record WorkItem(short[] Pcm, DateTime CapturedAtUtc, long SessionId, bool IsPartial);

    // Partials are raised while a segment is still in progress, on the partial-decode cadence.
    /// <inheritdoc />
    public event EventHandler<PartialTranscript>? PartialTranscriptAvailable;

    /// <inheritdoc />
    public event EventHandler<FinalTranscript>? FinalTranscriptAvailable;

    /// <inheritdoc />
    public event EventHandler<SpeechRecognitionError>? RecognitionFailed;

    /// <inheritdoc />
    public bool IsRecognizing { get; private set; }

    /// <summary>
    /// Creates a native-streaming faster-whisper engine backed by a persistent Python worker.
    /// </summary>
    /// <param name="options">Process/model configuration (the windowing fields are unused by this engine).</param>
    /// <param name="voiceActivityDetector">The VAD whose per-chunk decisions drive segment boundaries.</param>
    /// <param name="segmentDetectorOptions">Segment-boundary tuning.</param>
    public FasterWhisperNativeStreamingEngine(
        FasterWhisperEngineOptions options,
        IVoiceActivityDetector voiceActivityDetector,
        SpeechSegmentDetectorOptions segmentDetectorOptions)
        : this(
            options,
            new LineProtocolFasterWhisperProcess(options),
            ownedProcess: true,
            voiceActivityDetector,
            segmentDetectorOptions,
            stopWaitTimeout: TimeSpan.FromSeconds(15))
    {
    }

    /// <summary>
    /// Test seam: builds the engine with a scripted worker process and VAD so segment/decode behavior
    /// can be verified deterministically without Python or a model.
    /// </summary>
    internal FasterWhisperNativeStreamingEngine(
        FasterWhisperEngineOptions options,
        IFasterWhisperProcess process,
        IVoiceActivityDetector voiceActivityDetector,
        SpeechSegmentDetectorOptions segmentDetectorOptions)
        : this(
            options,
            process,
            ownedProcess: false,
            voiceActivityDetector,
            segmentDetectorOptions,
            stopWaitTimeout: TimeSpan.FromSeconds(15))
    {
    }

    private FasterWhisperNativeStreamingEngine(
        FasterWhisperEngineOptions options,
        IFasterWhisperProcess process,
        bool ownedProcess,
        IVoiceActivityDetector voiceActivityDetector,
        SpeechSegmentDetectorOptions segmentDetectorOptions,
        TimeSpan stopWaitTimeout)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.PartialDecodeInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PartialDecodeInterval must not be negative.");
        }

        if (options.PartialDecodeInterval > TimeSpan.Zero && options.PartialDecodeWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PartialDecodeWindow must be positive when partial decodes are enabled.");
        }

        _process = process ?? throw new ArgumentNullException(nameof(process));
        _ownedProcess = ownedProcess;
        _voiceActivityDetector = voiceActivityDetector ?? throw new ArgumentNullException(nameof(voiceActivityDetector));
        _segmentDetector = new SpeechSegmentDetector(
            segmentDetectorOptions ?? throw new ArgumentNullException(nameof(segmentDetectorOptions)));
        _stopWaitTimeout = stopWaitTimeout;
        _partialIntervalFrames = (int)(options.PartialDecodeInterval.TotalSeconds * options.SampleRate);
        _partialWindowFrames = (int)(options.PartialDecodeWindow.TotalSeconds * options.SampleRate);
    }

    /// <summary>
    /// Test seam: exposes the options that constructed this engine so factory tests can assert knob
    /// propagation (for example the <c>UC_NATIVE_THREADS</c> decode-thread cap).
    /// </summary>
    internal FasterWhisperEngineOptions Options => _options;

    /// <inheritdoc />
    public void Start()
    {
        lock (_gate)
        {
            if (IsRecognizing)
            {
                return;
            }

            try
            {
                _process.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (FasterWhisperProcessException ex)
            {
                RecognitionFailed?.Invoke(this, new SpeechRecognitionError(
                    SpeechRecognitionErrorKind.ModelLoadFailed,
                    $"Faster-whisper model '{_options.Model}' could not be loaded: {ex.Message}",
                    ex));
                return;
            }
            catch (Exception ex)
            {
                RecognitionFailed?.Invoke(this, new SpeechRecognitionError(
                    SpeechRecognitionErrorKind.ModelLoadFailed,
                    $"Faster-whisper model '{_options.Model}' could not be started: {ex.Message}",
                    ex));
                return;
            }

            _invalidFormatReported = false;
            _voiceActivityDetector.Reset();
            _segmentDetector.Reset();
            _partialQueued = false;
            _samplesSincePartial = 0;
            _channel = Channel.CreateUnbounded<WorkItem?>(new UnboundedChannelOptions { SingleReader = true });
            _cts = new CancellationTokenSource();
            IsRecognizing = true;
            _sessionId++;
            var token = _cts.Token;
            _loopTask = Task.Run(() => RunLoop(token));
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        Task? loop;
        lock (_gate)
        {
            if (!IsRecognizing)
            {
                return;
            }

            IsRecognizing = false;

            // Flush the in-progress segment so a sentence cut short by Stop is still captioned.
            CompletedSegment? flushed = _segmentDetector.Flush();
            if (flushed is not null)
            {
                _channel?.Writer.TryWrite(ToWorkItem(flushed, _sessionId));
            }

            // The null marker tells the decode loop to exit once the queue is drained. The loop is
            // deliberately not cancelled here so an in-flight decode can finish and emit its FINAL.
            _channel?.Writer.TryWrite(null);
            loop = _loopTask;
            _loopTask = null;
        }

        if (loop is not null)
        {
            bool completed;
            try
            {
                completed = loop.Wait(_stopWaitTimeout);
            }
            catch (AggregateException)
            {
                completed = true;
            }

            if (!completed)
            {
                // Pathological case: a decode outlived the drain budget. Cancel so a stale FINAL
                // cannot bleed into a subsequent Start on the same instance.
                lock (_gate)
                {
                    _cts?.Cancel();
                }
            }
        }
    }

    /// <inheritdoc />
    public void Process(AudioChunk chunk)
    {
        bool reportInvalidFormat = false;
        lock (_gate)
        {
            if (!IsRecognizing)
            {
                return;
            }

            if (chunk.Format.SampleRate != _options.SampleRate || chunk.Format.Channels != 1)
            {
                reportInvalidFormat = !_invalidFormatReported;
                _invalidFormatReported = true;
            }
            else
            {
                bool isSpeech = _voiceActivityDetector.IsSpeech(chunk);
                CompletedSegment? completed = _segmentDetector.Process(chunk, isSpeech);
                if (completed is not null)
                {
                    _channel?.Writer.TryWrite(ToWorkItem(completed, _sessionId));
                    _samplesSincePartial = 0;
                }
                else if (isSpeech && _partialIntervalFrames > 0)
                {
                    _samplesSincePartial += chunk.Samples.Length;
                    if (_samplesSincePartial >= _partialIntervalFrames
                        && !_partialQueued
                        && _segmentDetector.TryGetPartial(_partialWindowFrames, out float[] partialSamples, out DateTime partialCapturedAt))
                    {
                        _partialQueued = true;
                        _samplesSincePartial = 0;
                        _channel?.Writer.TryWrite(new WorkItem(ToPcm(partialSamples), partialCapturedAt, _sessionId, IsPartial: true));
                    }
                }
            }
        }

        if (reportInvalidFormat)
        {
            RecognitionFailed?.Invoke(this, new SpeechRecognitionError(
                SpeechRecognitionErrorKind.InvalidAudioFormat,
                $"The engine expects mono {_options.SampleRate} Hz audio but received {chunk.Format}."));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _cts?.Cancel();
        }

        if (_ownedProcess)
        {
            _process.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Stops recognition and releases the worker process. Prefer over <see cref="Dispose"/> when a
    /// decode may be in flight.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Stop();
        lock (_gate)
        {
            _cts?.Cancel();
        }

        if (_ownedProcess)
        {
            await _process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void RunLoop(CancellationToken ct)
    {
        var reader = _channel!.Reader;
        try
        {
            while (true)
            {
                while (reader.TryRead(out WorkItem? item))
                {
                    if (item is null)
                    {
                        return;
                    }

                    DecodeAndEmit(item, ct);
                }

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            lock (_gate)
            {
                IsRecognizing = false;
                _cts?.Cancel();
            }

            RecognitionFailed?.Invoke(this, new SpeechRecognitionError(
                SpeechRecognitionErrorKind.EngineFailed,
                "Faster-whisper native streaming recognition failed.",
                ex));
        }
    }

    private void DecodeAndEmit(WorkItem item, CancellationToken ct)
    {
        IReadOnlyList<TranscriptSegment> decoded = _process.TranscribeAsync(
            item.Pcm,
            _options.Language,
            ct).GetAwaiter().GetResult();

        string text = JoinSegments(decoded);
        if (string.IsNullOrWhiteSpace(text) || IsHallucinatedPunctuation(text))
        {
            if (item.IsPartial)
            {
                lock (_gate)
                {
                    _partialQueued = false;
                }
            }

            return;
        }

        // The item's session must still be the active one. Without this guard, a decode that
        // outlived Stop would raise a stale transcript into a subsequently started session.
        bool currentSession;
        lock (_gate)
        {
            currentSession = item.SessionId == _sessionId;
            if (item.IsPartial)
            {
                _partialQueued = false;
            }
        }

        if (!currentSession)
        {
            return;
        }

        if (item.IsPartial)
        {
            PartialTranscriptAvailable?.Invoke(this, new PartialTranscript(
                text,
                item.CapturedAtUtc,
                DateTime.UtcNow,
                _sequence++));
        }
        else
        {
            FinalTranscriptAvailable?.Invoke(this, new FinalTranscript(
                text,
                item.CapturedAtUtc,
                DateTime.UtcNow,
                _sequence++));
        }
    }

    private static WorkItem ToWorkItem(CompletedSegment completed, long sessionId)
        => new(ToPcm(completed.Samples), completed.CapturedAtUtc, sessionId, IsPartial: false);

    private static short[] ToPcm(float[] samples)
    {
        var pcm = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i];
            if (v > 1.0f)
            {
                v = 1.0f;
            }
            else if (v < -1.0f)
            {
                v = -1.0f;
            }

            pcm[i] = v < 0.0f
                ? (short)(v * -short.MinValue)
                : (short)(v * short.MaxValue);
        }

        return pcm;
    }

    private static string JoinSegments(IReadOnlyList<TranscriptSegment> segments)
        => string.Join(" ", segments.Select(s => s.Text).Where(t => !string.IsNullOrWhiteSpace(t))).Trim();

    private static bool IsHallucinatedPunctuation(string text)
    {
        int dots = text.Count(c => c == '.');
        return dots >= 8 && dots * 2 >= text.Length;
    }
}
