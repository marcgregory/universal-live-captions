using System.Threading.Channels;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Speech;

namespace UniversalCaptions.Speech;

/// <summary>
/// Test seam: decodes a window of mono 16 kHz samples into segments. The production implementation
/// runs the model; tests inject a deterministic decoder. Wrapped as an <see cref="ISTTDecoder"/>.
/// </summary>
internal delegate IReadOnlyList<TranscriptSegment> SegmentDecoder(ReadOnlyMemory<float> samples, CancellationToken cancellationToken);

/// <summary>
/// An <see cref="ISpeechToTextEngine"/> that buffers streaming audio into a sliding window,
/// re-decodes on an interval, and surfaces newly finalized text as final transcripts and the
/// in-progress tail as partial transcripts. All model-specific decoding lives behind
/// <see cref="ISTTDecoder"/>, so the windowing/trim/commit orchestration is engine-neutral and
/// reused by any decoder (whisper.cpp today, faster-whisper via the same seam).
/// </summary>
public sealed class WhisperSpeechToTextEngine : ISpeechToTextEngine, IAsyncDisposable
{
    private readonly WhisperEngineOptions _options;
    private readonly ISTTDecoder _decoder;
    private readonly StreamingTranscriptCommitter _committer;

    private Channel<ChunkData>? _channel;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _invalidFormatReported;
    private long _sequence;

    private readonly object _gate = new();

    private sealed record ChunkData(float[] Samples, DateTime CapturedAtUtc, long Sequence);

    /// <inheritdoc />
    public event EventHandler<PartialTranscript>? PartialTranscriptAvailable;

    /// <inheritdoc />
    public event EventHandler<FinalTranscript>? FinalTranscriptAvailable;

    /// <inheritdoc />
    public event EventHandler<SpeechRecognitionError>? RecognitionFailed;

    /// <inheritdoc />
    public bool IsRecognizing { get; private set; }

    /// <summary>
    /// Creates a Whisper engine that loads <see cref="WhisperEngineOptions.ModelPath"/> on start.
    /// </summary>
    public WhisperSpeechToTextEngine(WhisperEngineOptions options)
        : this(options, new WhisperCppDecoder(options))
    {
    }

    internal WhisperSpeechToTextEngine(WhisperEngineOptions options, SegmentDecoder decoder)
        : this(options, InnerSegmentDecoder(decoder))
    {
    }

    internal WhisperSpeechToTextEngine(WhisperEngineOptions options, ISTTDecoder decoder)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ModelPath))
        {
            throw new ArgumentException("ModelPath must be set.", nameof(options));
        }

        if (options.StabilityWindow < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "StabilityWindow must be at least 2 so partials are emitted before finals.");
        }

        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _committer = new StreamingTranscriptCommitter(
            options.StabilityWindow,
            options.BoundaryWaitBudget,
            () => DateTime.UtcNow);
    }

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
                _decoder.EnsureReady();
            }
            catch (FileNotFoundException ex)
            {
                RecognitionFailed?.Invoke(this, new SpeechRecognitionError(
                    SpeechRecognitionErrorKind.ModelNotFound,
                    $"Whisper model file '{_options.ModelPath}' was not found.",
                    ex));
                return;
            }
            catch (Exception ex)
            {
                RecognitionFailed?.Invoke(this, new SpeechRecognitionError(
                    SpeechRecognitionErrorKind.ModelLoadFailed,
                    $"Whisper model '{_options.ModelPath}' could not be loaded.",
                    ex));
                return;
            }

            _invalidFormatReported = false;
            _committer.Reset();
            _channel = Channel.CreateUnbounded<ChunkData>(new UnboundedChannelOptions { SingleReader = true });
            _cts = new CancellationTokenSource();
            IsRecognizing = true;
            var token = _cts.Token;
            _loopTask = Task.Run(() => RunLoop(token));
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            if (!IsRecognizing)
            {
                return;
            }

            IsRecognizing = false;
            cts = _cts;
            loop = _loopTask;
            _cts = null;
            _loopTask = null;
        }

        cts?.Cancel();
        if (loop is not null)
        {
            try
            {
                loop.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
                // The loop unwinds on cancellation; a lingering exception is not actionable.
            }
        }
    }

    /// <inheritdoc />
    public void Process(AudioChunk chunk)
    {
        if (!IsRecognizing)
        {
            return;
        }

        if (chunk.Format.SampleRate != _options.SampleRate || chunk.Format.Channels != 1)
        {
            if (!_invalidFormatReported)
            {
                _invalidFormatReported = true;
                RecognitionFailed?.Invoke(this, new SpeechRecognitionError(
                    SpeechRecognitionErrorKind.InvalidAudioFormat,
                    $"The engine expects mono {_options.SampleRate} Hz audio but received {chunk.Format}."));
            }

            return;
        }

        var copy = new float[chunk.Samples.Length];
        Array.Copy(chunk.Samples, copy, copy.Length);
        _channel?.Writer.TryWrite(new ChunkData(copy, chunk.CapturedAtUtc, chunk.Sequence));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _cts?.Cancel();
        }

        _decoder.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Stops recognition and releases the model. Prefer this over <see cref="Dispose"/> when a
    /// decode is potentially in flight, so the engine can wait for it to unwind.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Stop();
        lock (_gate)
        {
            _cts?.Cancel();
        }

        await _decoder.DisposeAsync().ConfigureAwait(false);
    }

    private void RunLoop(CancellationToken ct)
    {
        var buffer = new List<float>();
        var windowChunks = new Queue<ChunkData>();
        var maxWindowFrames = (int)(_options.WindowDuration.TotalSeconds * _options.SampleRate);
        var firstDecodeFrames = (int)(_options.MinimumAudioBeforeFirstDecode.TotalSeconds * _options.SampleRate);
        var intervalFrames = (int)(_options.DecodeInterval.TotalSeconds * _options.SampleRate);
        long framesSinceDecode = 0;
        bool decodedOnce = false;
        var windowEndUtc = DateTime.MinValue;

        var reader = _channel!.Reader;

        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                bool drained = false;
                while (reader.TryRead(out var chunk))
                {
                    drained = true;
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    buffer.AddRange(chunk.Samples);
                    windowChunks.Enqueue(chunk);
                    framesSinceDecode += chunk.Samples.Length;
                    windowEndUtc = chunk.CapturedAtUtc + TimeSpan.FromSeconds((double)chunk.Samples.Length / _options.SampleRate);
                }

                if (!drained)
                {
                    reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult();
                    continue;
                }

                bool shouldDecode = !decodedOnce
                    ? buffer.Count >= firstDecodeFrames
                    : framesSinceDecode >= intervalFrames;
                if (shouldDecode && windowChunks.Count > 0)
                {
                    DecodeAndEmit(buffer, windowChunks, ct);
                    framesSinceDecode = 0;
                    decodedOnce = true;
                }

                if (buffer.Count > maxWindowFrames && TrimToCommitted(buffer, windowChunks, windowEndUtc))
                {
                    decodedOnce = false;
                    framesSinceDecode = 0;
                }
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

            IsRecognizing = false;
            _cts?.Cancel();
            RecognitionFailed?.Invoke(this, new SpeechRecognitionError(
                SpeechRecognitionErrorKind.EngineFailed,
                "Whisper recognition failed.",
                ex));
        }
    }

    private void DecodeAndEmit(List<float> buffer, Queue<ChunkData> windowChunks, CancellationToken ct)
    {
        var samples = new float[buffer.Count];
        buffer.CopyTo(samples, 0);

        var windowStartUtc = windowChunks.Peek().CapturedAtUtc;
        var last = windowChunks.Last();
        var windowEndUtc = last.CapturedAtUtc + TimeSpan.FromSeconds((double)last.Samples.Length / _options.SampleRate);

        IReadOnlyList<TranscriptSegment> segments = _decoder.Decode(samples, ct);

        var result = _committer.Update(segments, windowStartUtc);

        if (!string.IsNullOrEmpty(result.FinalText))
        {
            FinalTranscriptAvailable?.Invoke(this, new FinalTranscript(
                result.FinalText,
                _committer.CommittedUntilUtc,
                DateTime.UtcNow,
                _sequence++));
        }

        if (!string.IsNullOrEmpty(result.PartialText))
        {
            PartialTranscriptAvailable?.Invoke(this, new PartialTranscript(
                result.PartialText,
                windowEndUtc,
                DateTime.UtcNow,
                _sequence++));
        }
    }

    /// <summary>
    /// Drops audio from the front of the window once the window exceeds its cap. Only audio that
    /// ends before the committed boundary (or, failing that, before the window tail) is removed, so
    /// in-progress hypotheses are never truncated. Returns true when anything was dropped, which
    /// starts a fresh window epoch.
    /// </summary>
    private bool TrimToCommitted(List<float> buffer, Queue<ChunkData> windowChunks, DateTime windowEndUtc)
    {
        var trimUntil = windowEndUtc - _options.CommitOverlap;
        if (_committer.CommittedUntilUtc > DateTime.MinValue && _committer.CommittedUntilUtc < trimUntil)
        {
            trimUntil = _committer.CommittedUntilUtc;
        }

        bool trimmed = false;
        while (windowChunks.Count > 0)
        {
            var first = windowChunks.Peek();
            var chunkEndUtc = first.CapturedAtUtc + TimeSpan.FromSeconds((double)first.Samples.Length / _options.SampleRate);
            if (chunkEndUtc > trimUntil)
            {
                break;
            }

            buffer.RemoveRange(0, first.Samples.Length);
            windowChunks.Dequeue();
            trimmed = true;
        }

        return trimmed;
    }

    private sealed class InnerDecoder : ISTTDecoder
    {
        private readonly SegmentDecoder _decode;

        public InnerDecoder(SegmentDecoder decode) => _decode = decode;

        public void EnsureReady()
        {
        }

        public IReadOnlyList<TranscriptSegment> Decode(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
            => _decode(samples, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ISTTDecoder InnerSegmentDecoder(SegmentDecoder decoder)
        => new InnerDecoder(decoder ?? throw new ArgumentNullException(nameof(decoder)));
}
