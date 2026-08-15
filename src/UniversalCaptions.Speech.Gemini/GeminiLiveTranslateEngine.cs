using System.Threading.Channels;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.Speech.Gemini;

/// <summary>
/// Production <see cref="ILiveAudioTranslationEngine"/> that talks to Google's Gemini Live
/// Translate service over WebSockets. The engine owns the session lifecycle (StartAsync/StopAsync),
/// runs a background receive loop, and raises
/// <see cref="ILiveAudioTranslationEngine.PartialTranslationAvailable"/>,
/// <see cref="ILiveAudioTranslationEngine.FinalTranslationAvailable"/>, and
/// <see cref="ILiveAudioTranslationEngine.TranslationFailed"/> on the consumers.
/// </summary>
/// <remarks>
/// <para>
/// Audio path: <see cref="PushAudio"/> writes 16 kHz mono float32 <see cref="AudioChunk"/>s to a
/// bounded <see cref="Channel{T}"/> with capacity 64 and <see cref="BoundedChannelFullMode.DropOldest"/>.
/// A single send-task drains the channel, converts each chunk to 16-bit signed little-endian PCM,
/// and forwards the result to <see cref="GeminiLiveTranslateProtocol.BuildRealtimeAudioFrame"/>. The
/// drop policy keeps the freshest audio on the wire when Gemini falls behind, so captions always
/// reflect the speaker's most recent words.
/// </para>
/// <para>
/// Output path: a single receive-task loops over <see cref="IGeminiLiveTranslateChannel.ReceiveTextAsync"/>,
/// hands each frame to <see cref="GeminiLiveTranslateProtocol.TryParseServerFrame"/>, and updates
/// the per-turn accumulator. On every non-empty partial text the engine raises
/// <see cref="ILiveAudioTranslationEngine.PartialTranslationAvailable"/>; on
/// <c>turnComplete</c> it raises <see cref="ILiveAudioTranslationEngine.FinalTranslationAvailable"/>
/// and clears the accumulator. If the receive loop ends while text remains, the engine tail-flushes
/// the accumulator as a final translation so no translated words are dropped.
/// </para>
/// <para>
/// Precedence: <c>outputTranscription.text</c> is canonical; <c>modelTurn.parts[].text</c> is
/// compatibility fallback and only consulted when <c>outputTranscription</c> is absent from the
/// frame. The protocol layer encodes this; the engine never re-evaluates the choice.
/// </para>
/// <para>
/// Failure isolation: a <see cref="LiveTranslationError"/> raised here stops and disposes this
/// engine only. The Caption pipeline's failure handler takes care of unsubscribing events and
/// tearing the engine down; Whisper and the offline caption pipeline are unaffected.
/// </para>
/// </remarks>
public sealed class GeminiLiveTranslateEngine : ILiveAudioTranslationEngine
{
    /// <summary>Capacity of the bounded audio queue between the capture callback thread and the send-task.</summary>
    private const int AudioQueueCapacity = 64;

    private readonly GeminiLiveTranslateEngineOptions _options;
    private readonly IGeminiLiveTranslateChannel _channel;

    private readonly Channel<AudioChunk> _audioQueue;

    /// <summary>
    /// Signals that the setup frame has been sent on the wire. The send loop must not transmit
    /// audio frames before this completes: the Gemini Live API rejects an audio frame that arrives
    /// ahead of <c>setup</c> (the session never starts producing translations). The App pushes
    /// audio into the queue immediately after capture starts — which can race ahead of
    /// <see cref="StartAsync"/>'s sequential open → setup sequence — so the loop waits here instead
    /// of depending on callers to hold audio until StartAsync returns. Replaced with a fresh
    /// instance on every <see cref="StartAsync"/> so a restarted session re-gates its send loop.
    /// </summary>
    private TaskCompletionSource _sessionReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly object _stateGate = new();
    private Task? _sendTask;
    private Task? _receiveTask;
    private CancellationTokenSource? _sessionCts;
    private string? _accumulatedText;
    private bool _accumulatorHasContent;
    private long _nextSequence;
    private bool _disposed;
    private DateTime _capturedAtUtcBase;

    private readonly object _commitGate = new();
    private CancellationTokenSource? _commitCts;
    private long _commitGeneration;

    /// <summary>
    /// Constructs a Gemini Live Translate engine. The API key is never logged, returned, or
    /// included in any exception message; the App is responsible for retrieving it from the
    /// Windows Credential Manager and passing it in.
    /// </summary>
    /// <param name="options">Engine configuration (model, target language, system instruction, endpoint).</param>
    /// <param name="channel">The transport seam. Production wires <see cref="ClientWebSocketGeminiChannel"/>; tests inject a fake.</param>
    public GeminiLiveTranslateEngine(GeminiLiveTranslateEngineOptions options, IGeminiLiveTranslateChannel channel)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(channel);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("API key is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new ArgumentException("Model is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.TargetLanguage))
        {
            throw new ArgumentException("Target language is required.", nameof(options));
        }

        _options = options;
        _channel = channel;
        _audioQueue = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(AudioQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <inheritdoc />
    public event EventHandler<PartialTranslation>? PartialTranslationAvailable;

    /// <inheritdoc />
    public event EventHandler<FinalTranslation>? FinalTranslationAvailable;

    /// <inheritdoc />
    public event EventHandler<LiveTranslationError>? TranslationFailed;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Task sendTask;
        Task receiveTask;
        CancellationTokenSource cts;
        lock (_stateGate)
        {
            if (_sendTask is not null || _receiveTask is not null)
            {
                throw new InvalidOperationException("Engine has already been started.");
            }

            cts = new CancellationTokenSource();
            _sessionCts = cts;
            _sessionReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sendTask = Task.Run(() => SendLoopAsync(cts.Token));
            receiveTask = Task.Run(() => ReceiveLoopAsync(cts.Token));
            _sendTask = sendTask;
            _receiveTask = receiveTask;
        }

        try
        {
            Uri uri = _options.BuildEndpoint();
            await _channel.OpenAsync(uri, cancellationToken).ConfigureAwait(false);

            string setupFrame = GeminiLiveTranslateProtocol.BuildSetupFrame(
                _options.Model,
                _options.ResolveTargetLanguageCode());
            await _channel.SendTextAsync(setupFrame, cancellationToken).ConfigureAwait(false);

            // Unblock the send loop now that the server has received the setup frame; audio frames
            // sent before setup would abort the session before any translation can be produced.
            _sessionReadyTcs.TrySetResult();
            _capturedAtUtcBase = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // Convert startup failures (connect, auth, setup) into a TranslationFailed event so the
            // pipeline's failure handler tears the engine down without ever having emitted a
            // translation. We do NOT await StopAsync here — the channel may have failed at Open
            // and StopAsync would block waiting for a non-existent session.
            await RaiseTranslationFailedAsync(new LiveTranslationError(
                MapStartupException(ex),
                "Live translation engine could not start.",
                ex));
            await DisposeInternalAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public void PushAudio(AudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (_disposed)
        {
            return;
        }

        // Bounded queue + DropOldest: synchronous, non-blocking, non-throwing by contract. The
        // capture callback thread must never be held up by a slow Gemini send path.
        if (!_audioQueue.Writer.TryWrite(chunk))
        {
            // TryWrite on a bounded channel with DropOldest returns true even when the channel
            // is full — the oldest entry is evicted. The only failure mode is "channel completed"
            // (we haven't called Complete yet) so this branch is unreachable in practice. Kept for
            // clarity: if the writer is ever closed without TryWrite returning true, the engine
            // is shutting down and the audio can be discarded safely.
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        Task? sendTask;
        Task? receiveTask;

        lock (_stateGate)
        {
            cts = _sessionCts;
            sendTask = _sendTask;
            receiveTask = _receiveTask;
        }

        if (cts is null || sendTask is null || receiveTask is null)
        {
            return;
        }

        // Stop draining new audio first; the send task observes the completed channel and exits.
        _audioQueue.Writer.TryComplete();
        cts.Cancel();
        CancelCommitTimer();

        try
        {
            await _channel.CloseAsync("client stop", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: a failing Close must not prevent the engine from cleaning up.
        }

        try
        {
            await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);
        }
        catch
        {
            // The send/receive tasks may surface exceptions from the channel on shutdown; absorb.
        }

        // Tail-flush: if the receive loop exited before raising turnComplete, commit whatever is
        // in the accumulator as a FinalTranslation so no translated words are silently dropped.
        FlushAccumulatorAsFinal();

        lock (_stateGate)
        {
            _sendTask = null;
            _receiveTask = null;
            _sessionCts?.Dispose();
            _sessionCts = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposeInternalAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Synchronous dispose: best-effort cleanup. The async path is preferred but callers that
        // only have a sync Dispose can still cancel the session.
        DisposeInternalAsync().GetAwaiter().GetResult();
    }

    private async Task DisposeInternalAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort.
        }

        _audioQueue.Writer.TryComplete();
        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Do not transmit audio until the setup frame is on the wire (set by StartAsync after
            // sending it). The App starts audio capture before the live-translation block, so chunks
            // are already queued when this loop starts; sending one ahead of setup would make Gemini
            // reject the session. WaitAsync keeps shutdown clean: if setup never completes (connect
            // failure), StopAsync cancels the session token and the loop exits via OCE.
            await _sessionReadyTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            await foreach (AudioChunk chunk in _audioQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                ReadOnlyMemory<byte> pcm16 = FloatToPcm16Le(chunk);
                string json = GeminiLiveTranslateProtocol.BuildRealtimeAudioFrame(pcm16.Span);
                try
                {
                    await _channel.SendTextAsync(json, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    await RaiseTranslationFailedAsync(new LiveTranslationError(
                        LiveTranslationErrorKind.ConnectionFailed,
                        "Live translation send failed.",
                        ex)).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? frame;
                try
                {
                    frame = await _channel.ReceiveTextAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    await RaiseTranslationFailedAsync(new LiveTranslationError(
                        LiveTranslationErrorKind.ConnectionFailed,
                        "Live translation receive failed.",
                        ex)).ConfigureAwait(false);
                    return;
                }

                if (frame is null)
                {
                    // The channel returned no data for this iteration — keep polling. Real closes
                    // arrive as goAway / error frames, exceptions, or cancellation triggered by
                    // StopAsync. Treating null as a clean close would prematurely tear the
                    // session down the moment the server momentarily had nothing to send.
                    continue;
                }

                if (!GeminiLiveTranslateProtocol.TryParseServerFrame(frame, out GeminiServerMessage? message, out string? parseError))
                {
                    await RaiseTranslationFailedAsync(new LiveTranslationError(
                        LiveTranslationErrorKind.Unknown,
                        $"Malformed server frame: {parseError}",
                        null)).ConfigureAwait(false);
                    return;
                }

                switch (message)
                {
                    case GeminiServerMessage.SetupComplete:
                        // Acknowledged; nothing to do.
                        break;

                    case GeminiServerMessage.ServerContent content:
                        HandleServerContent(content);
                        break;

                    case GeminiServerMessage.GoAway:
                        // Server is closing the session. Tail-flush any pending translation so
                        // the in-progress line isn't silently dropped — StopAsync's flush is a
                        // belt-and-braces backup for the path where the server closes without
                        // sending goAway.
                        FlushAccumulatorAsFinal();
                        // Surface a graceful-session-end event so the pipeline can detach the
                        // engine and clear the active translation line. Without this, the consumer
                        // has no signal that the receive loop exited via goAway — the engine would
                        // silently stop emitting partials/finals while still being attached, and
                        // the overlay would freeze on whatever translated text it had. This is the
                        // same chokepoint every other failure routes through. Classified as
                        // SessionEnded (not ServerError) so the UI can say "session ended, restart
                        // to resume" instead of implying a connection problem: a goAway here is the
                        // provider's expected wall-clock session limit (e.g. long idle pauses), not
                        // a failure.
                        await RaiseTranslationFailedAsync(new LiveTranslationError(
                            LiveTranslationErrorKind.SessionEnded,
                            "Gemini session ended. Toggle translation off/on or restart to resume.",
                            null)).ConfigureAwait(false);
                        return;

                    case GeminiServerMessage.SessionResumptionUpdate:
                        // Informational frame from Google's Live API — see the comments on
                        // GeminiServerMessage.SessionResumptionUpdate. The real-wire spike
                        // (2026-08-08) observed this frame arriving AFTER the final translation;
                        // previously A5 misclassified it as "Unrecognized top-level frame" and the
                        // engine killed the session. Live Translate doesn't accept
                        // sessionResumption configuration today, so this is a no-op on our side —
                        // the frame must not end the session, must not be treated as an error,
                        // and must not flush the accumulator. We simply continue receiving.
                        break;

                    case GeminiServerMessage.ErrorFrame error:
                        await RaiseTranslationFailedAsync(MapError(error)).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void HandleServerContent(GeminiServerMessage.ServerContent content)
    {
        // The protocol already enforces outputTranscription strict precedence — Text is null when
        // neither surface carried a translation.
        if (!string.IsNullOrEmpty(content.Text))
        {
            // Genuine sentence boundary: the accumulator already ends with terminal punctuation and
            // the incoming fragment starts a DISJOINT new sentence (not a cumulative restatement
            // of the finished one). Commit the completed sentence immediately, then accumulate the
            // fragment as the start of the next sentence — so continuous speech yields
            // sentence-granular finals instead of one ever-growing line.
            //
            // Exception (v0.5.40 segmentation fix): Gemini can place a mid-sentence period on a
            // fragment ("...description.") and then stream the true continuation as a new fragment
            // that begins with whitespace + lowercase (" at halos tugma"). A lowercase-leading
            // continuation is the SAME sentence — append it instead of committing a premature final
            // (observed run 1: FINAL "Nabasa mo na ang job description." → FRAG init " at halos
            // tugma"; the same audio in run 2 produced one clean sentence).
            if (_accumulatorHasContent
                && _accumulatedText is not null
                && EndsWithTerminalPunctuation(_accumulatedText)
                && !IsCumulativeRestatement(_accumulatedText, content.Text)
                && !StartsWithLowercaseContinuation(content.Text))
            {
                FlushAccumulatorAsFinal();
            }

            _accumulatedText = Accumulate(_accumulatedText, content.Text);
            _accumulatorHasContent = true;
            long sequence = Interlocked.Increment(ref _nextSequence);
            DateTime capturedAtUtc = _capturedAtUtcBase;
            DateTime emittedAtUtc = DateTime.UtcNow;
            string partialText = _accumulatedText ?? string.Empty;
            PartialTranslationAvailable?.Invoke(
                this,
                new PartialTranslation(
                    sourceText: null,
                    translatedText: partialText,
                    sourceLanguage: _options.SourceLanguage ?? string.Empty,
                    targetLanguage: _options.TargetLanguage,
                    capturedAtUtc: capturedAtUtc,
                    emittedAtUtc: emittedAtUtc,
                    sequence: sequence));
        }

        if (content.TurnComplete)
        {
            FlushAccumulatorAsFinal();
        }
        else if (!string.IsNullOrEmpty(content.Text))
        {
            // The Live Translate service never sends turnComplete (verified on the real wire
            // 2026-08-12), so finals would never commit. Arm a punctuation+idle commit instead:
            // when the accumulated sentence ends with terminal punctuation, commit it as a final
            // once no newer partial arrives within the idle window.
            ArmCommitTimer(_accumulatedText ?? string.Empty);
        }
    }

    /// <summary>
    /// True when <paramref name="incoming"/> restates <paramref name="current"/> as a prefix
    /// (with an optional trailing punctuation on the current text) — i.e. a chat-style cumulative
    /// partial that must REPLACE the accumulator, never append to it. The word-boundary check
    /// prevents a coincidental prefix ("Ka" → "Kamusta") from being treated as a restatement.
    /// </summary>
    private static bool IsCumulativeRestatement(string current, string incoming)
    {
        string currentStem = current.Trim().TrimEnd('.', '!', '?');
        if (currentStem.Length == 0)
        {
            return false;
        }

        return incoming.StartsWith(currentStem, StringComparison.OrdinalIgnoreCase)
            && (incoming.Length == currentStem.Length
                || !char.IsLetterOrDigit(incoming[currentStem.Length]));
    }

    /// <summary>
    /// True when <paramref name="incoming"/> begins with whitespace followed by a lowercase
    /// character — the signature of a mid-sentence continuation (" at halos tugma") rather than the
    /// start of a disjoint new sentence (" And next..."). The v0.5.40 segmentation guard: when a new
    /// fragment arrives while the accumulator ends in punctuation, a lowercase-leading continuation
    /// must be APPENDED to the same sentence, never treated as a sentence boundary.
    /// </summary>
    private static bool StartsWithLowercaseContinuation(string incoming)
    {
        for (int i = 0; i < incoming.Length; i++)
        {
            if (char.IsWhiteSpace(incoming[i]))
            {
                continue;
            }

            return char.IsLower(incoming[i]);
        }

        return false;
    }

    /// <summary>
    /// Merge a freshly parsed fragment into the accumulated sentence.
    /// </summary>
    /// <remarks>
    /// The chat-style surface sends cumulative partials ("Magandang umaga" → "Magandang umaga
    /// lahat") — those must REPLACE the accumulator, never double it. The Live Translate service
    /// (verified on the real wire 2026-08-12) instead streams word-level DISJOINT fragments
    /// ("Kung mas marami kang maibigay" then "sa Codex, mas") that spell out one sentence — those
    /// must be APPENDED onto the accumulator. A word-boundary prefix check tells the two apart.
    /// </remarks>
    private static string? Accumulate(string? current, string incoming)
    {
        string incomingTrimmed = incoming.Trim();

        if (string.IsNullOrEmpty(current))
        {
            return incomingTrimmed;
        }

        // Chat-style cumulative partial: the new fragment restates the whole prefix — replace.
        if (IsCumulativeRestatement(current, incomingTrimmed))
        {
            return incomingTrimmed;
        }

        string currentTrimmed = current.TrimEnd();

        // Exact repeats of the current tail are noise ("gusto" → "gusto") — keep the accumulator
        // as-is rather than doubling the word.
        if (string.Equals(currentTrimmed, incomingTrimmed, StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        // Live Translate disjoint word-fragment: append onto the growing sentence.
        return currentTrimmed + " " + incomingTrimmed;
    }

    private void ArmCommitTimer(string currentText)
    {
        if (_options.CommitIdleTimeout <= TimeSpan.Zero)
        {
            return;
        }

        if (!EndsWithTerminalPunctuation(currentText))
        {
            CancelCommitTimer();
            return;
        }

        lock (_commitGate)
        {
            _commitCts?.Cancel();
            _commitCts?.Dispose();
            _commitCts = new CancellationTokenSource();
            long generation = ++_commitGeneration;
            TimeSpan delay = _options.CommitIdleTimeout;
            CancellationToken token = _commitCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                bool shouldFlush = false;
                lock (_commitGate)
                {
                    shouldFlush = generation == _commitGeneration
                        && _accumulatorHasContent
                        && _accumulatedText is not null
                        && EndsWithTerminalPunctuation(_accumulatedText);
                }

                if (shouldFlush)
                {
                    FlushAccumulatorAsFinal();
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void CancelCommitTimer()
    {
        lock (_commitGate)
        {
            _commitCts?.Cancel();
            _commitCts?.Dispose();
            _commitCts = null;
            _commitGeneration++;
        }
    }

    private static bool EndsWithTerminalPunctuation(string text)
    {
        string trimmed = text.TrimEnd();
        return trimmed.Length > 0 && (trimmed[^1] is '.' or '!' or '?');
    }

    private void FlushAccumulatorAsFinal()
    {
        if (!_accumulatorHasContent)
        {
            return;
        }

        string finalText = _accumulatedText ?? string.Empty;
        long sequence = Interlocked.Increment(ref _nextSequence);
        DateTime capturedAtUtc = _capturedAtUtcBase;
        DateTime committedAtUtc = DateTime.UtcNow;

        _accumulatedText = null;
        _accumulatorHasContent = false;

        CancelCommitTimer();

        FinalTranslationAvailable?.Invoke(
            this,
            new FinalTranslation(
                sourceText: null,
                translatedText: finalText,
                sourceLanguage: _options.SourceLanguage ?? string.Empty,
                targetLanguage: _options.TargetLanguage,
                capturedAtUtc: capturedAtUtc,
                emittedAtUtc: committedAtUtc,
                sequence: sequence,
                committedAtUtc: committedAtUtc));
    }

    /// <summary>
    /// Float32 → 16-bit signed little-endian PCM. The protocol layer accepts PCM16 bytes; the
    /// engine owns the conversion so the protocol stays agnostic about the application's internal
    /// audio representation.
    /// </summary>
    private static ReadOnlyMemory<byte> FloatToPcm16Le(AudioChunk chunk)
    {
        int sampleCount = chunk.Samples.Length;
        byte[] output = new byte[sampleCount * sizeof(short)];
        for (int i = 0; i < sampleCount; i++)
        {
            float sample = chunk.Samples[i];
            // Clamp + scale. Clamping protects against clipping artifacts when the input floats
            // exceed the [-1, 1] range (shouldn't happen with normal audio but is cheap to guard).
            short pcm = (short)Math.Clamp((int)(Math.Clamp(sample, -1f, 1f) * short.MaxValue), short.MinValue, short.MaxValue);
            output[2 * i] = (byte)(pcm & 0xFF);
            output[(2 * i) + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return output;
    }

    private async Task RaiseTranslationFailedAsync(LiveTranslationError error)
    {
        // Marshal to a single dispatch point so the receive loop is never re-entered by the
        // pipeline's failure handler while we're still draining frames.
        try
        {
            TranslationFailed?.Invoke(this, error);
        }
        catch
        {
            // Subscriber exceptions must not propagate into the receive loop.
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Map an exception thrown during <see cref="StartAsync"/> to a
    /// <see cref="LiveTranslationErrorKind"/>. The WebSocket handshake rejects an invalid key with an
    /// HTTP-level status (observed 400 API_KEY_INVALID), so the exception message carries the status
    /// code / wording; map those to an actionable category before falling back to a connection failure.
    /// </summary>
    private static LiveTranslationErrorKind MapStartupException(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return LiveTranslationErrorKind.Unknown;
        }

        string message = ex.Message ?? string.Empty;
        if (message.Contains("API key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || message.Contains("400", StringComparison.Ordinal)
            || message.Contains("401", StringComparison.Ordinal)
            || message.Contains("403", StringComparison.Ordinal))
        {
            return LiveTranslationErrorKind.SessionRejected;
        }

        if (message.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("429", StringComparison.Ordinal)
            || message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase))
        {
            return LiveTranslationErrorKind.QuotaExceeded;
        }

        return LiveTranslationErrorKind.ConnectionFailed;
    }

    /// <summary>
    /// Map a parsed <see cref="GeminiServerMessage.ErrorFrame"/> to a
    /// <see cref="LiveTranslationErrorKind"/>. Numeric code is checked first (authoritative), then
    /// status string, then message classification, with <see cref="LiveTranslationErrorKind.Unknown"/>
    /// as the final fallback.
    /// </summary>
    internal static LiveTranslationErrorKind MapErrorKind(GeminiServerMessage.ErrorFrame error)
    {
        if (error.Code is int code)
        {
            if (code is 400 or 401 or 403)
            {
                return LiveTranslationErrorKind.SessionRejected;
            }

            if (code is 429)
            {
                return LiveTranslationErrorKind.QuotaExceeded;
            }
        }

        if (!string.IsNullOrWhiteSpace(error.Status))
        {
            if (error.Status.Equals("UNAUTHENTICATED", StringComparison.OrdinalIgnoreCase)
                || error.Status.Equals("PERMISSION_DENIED", StringComparison.OrdinalIgnoreCase)
                || error.Status.Equals("INVALID_ARGUMENT", StringComparison.OrdinalIgnoreCase))
            {
                return LiveTranslationErrorKind.SessionRejected;
            }

            if (error.Status.Equals("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase))
            {
                return LiveTranslationErrorKind.QuotaExceeded;
            }
        }

        if (!string.IsNullOrWhiteSpace(error.Message))
        {
            if (error.Message.Contains("API key", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("permission", StringComparison.OrdinalIgnoreCase))
            {
                return LiveTranslationErrorKind.SessionRejected;
            }

            if (error.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("rate", StringComparison.OrdinalIgnoreCase))
            {
                return LiveTranslationErrorKind.QuotaExceeded;
            }
        }

        return LiveTranslationErrorKind.Unknown;
    }

    private static LiveTranslationError MapError(GeminiServerMessage.ErrorFrame frame)
    {
        string message = frame.Message ?? frame.Status ?? "Gemini returned an error frame.";
        return new LiveTranslationError(MapErrorKind(frame), message, null);
    }
}
