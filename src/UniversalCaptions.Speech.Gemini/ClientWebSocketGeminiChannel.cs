using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace UniversalCaptions.Speech.Gemini;

/// <summary>
/// Production <see cref="IGeminiLiveTranslateChannel"/> backed by
/// <see cref="ClientWebSocket"/>. Pure transport: opens, sends text frames,
/// receives text frames, closes. No Gemini protocol knowledge lives here â€”
/// <see cref="GeminiLiveTranslateProtocol"/> owns that.
/// </summary>
internal sealed class ClientWebSocketGeminiChannel : IGeminiLiveTranslateChannel
{
    private const int ReceiveBufferBytes = 65_536;
    private const int CloseTimeoutMs = 5_000;

    private readonly ClientWebSocket _socket = new();
    private readonly TaskCompletionSource _openedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    // TEMP DIAGNOSTIC: file log of received frame shape. Remove after diagnosis.
    private static readonly object LogGate = new();
    private static long RxCounter;

    private static void RxLog(string entry)
    {
        try
        {
            lock (LogGate)
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "gemini_channel_rx.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {entry}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    /// <inheritdoc />
    public async Task OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        _openedTcs.TrySetResult();
    }

    /// <inheritdoc />
    public async Task SendTextAsync(string json, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(json);

        // Wait for OpenAsync to complete. The engine starts the send loop BEFORE OpenAsync
        // returns, and the capture callback pushes audio into that loop immediately (the App
        // starts capturing before the live-translation block), so without this gate
        // SendTextAsync races ahead of the WebSocket handshake and _socket.SendAsync throws
        // "The WebSocket is not connected" against the still-pending socket — the send-before-open
        // race that tore live translation down at startup (reproduced 2026-08-12 via harness).
        // ReceiveTextAsync uses the same gate; OpenAsync is expected to complete before any
        // genuine send is attempted in the sequential path (setup frame is sent after OpenAsync).
        // WaitAsync(cancellationToken) also unblocks the loop if OpenAsync fails: the tcs never
        // completes, but StopAsync cancels the session token, so the wait throws OCE and the
        // send loop exits cleanly instead of hanging the engine shutdown.
        await _openedTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        byte[] payload = Encoding.UTF8.GetBytes(json);
        string tag = payload.Length > 40 ? "audio" : json;
        RxLog($"TX jsonlen={json.Length} -> {tag}");
        await _socket
            .SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool IsClosed => _socket.State is WebSocketState.CloseReceived or WebSocketState.Closed or WebSocketState.Aborted;

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Wait for OpenAsync to complete. The engine starts the receive loop BEFORE OpenAsync
        // returns, so without this gate the receive loop races ahead of the WebSocket handshake and
        // _socket.ReceiveAsync throws "The WebSocket is not connected" against the still-pending
        // socket. WaitAsync(cancellationToken) also unblocks the loop if OpenAsync fails: the tcs
        // never completes, but StopAsync cancels the session token, so the wait throws OCE and the
        // receive loop exits cleanly instead of hanging the engine shutdown.
        await _openedTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_socket.State is WebSocketState.CloseReceived or WebSocketState.Closed or WebSocketState.Aborted)
        {
            return null;
        }

        var buffer = new byte[ReceiveBufferBytes];
        using var messageStream = new MemoryStream();
        ValueWebSocketReceiveResult result;
        long reads = 0;
        do
        {
            result = await _socket
                .ReceiveAsync(new Memory<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);
            reads++;

            if (result.Count > 0)
            {
                messageStream.Write(buffer, 0, result.Count);
            }

            if (reads == 1 || reads % 32 == 0)
            {
                RxLog($"READ msgtype={result.MessageType} count={result.Count} reads={reads} stream={messageStream.Length} end={result.EndOfMessage}");
            }
        }
        while (!result.EndOfMessage);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            RxLog($"RX close (reads={reads})");
            return null;
        }

        ReadOnlySpan<byte> payload = new ReadOnlySpan<byte>(messageStream.GetBuffer(), 0, (int)messageStream.Length);
        long rxId = Interlocked.Increment(ref RxCounter);
        RxLog($"RX#{rxId} msgtype={result.MessageType} bytes={payload.Length} reads={reads} endOfMessage={result.EndOfMessage}");

        if (result.MessageType == WebSocketMessageType.Text)
        {
            string text = Encoding.UTF8.GetString(payload);
            RxLog($"  -> TEXT prefix={Prefix(text)}");
            return text;
        }

        try
        {
            string decoded = Encoding.UTF8.GetString(payload);
            int nonWhitespace = 0;
            while (nonWhitespace < decoded.Length && char.IsWhiteSpace(decoded[nonWhitespace]))
            {
                nonWhitespace++;
            }

            if (nonWhitespace < decoded.Length && (decoded[nonWhitespace] == '{' || decoded[nonWhitespace] == '['))
            {
                if (decoded.Length <= 2000)
                {
                    RxLog($"  -> BINARY JSON FULL: {decoded}");
                }
                else
                {
                    // Dump the full decoded JSON once for the first large frame, then summarize.
                    if (rxId <= 3)
                    {
                        RxLog($"  -> BINARY JSON FULL: {decoded}");
                    }
                    else
                    {
                        // Compact marker summary (no 16KB audio dump): which serverContent surfaces
                        // are present in this frame. Proves whether turnComplete ever arrives inside
                        // the large modelTurn frames (finals only commit on turnComplete).
                        string markers = MarkerSummary(decoded);
                        RxLog($"  -> BINARY JSON (len={decoded.Length}) prefix={Prefix(decoded)} MARKERS[{markers}]");
                    }
                }

                return decoded;
            }

            RxLog($"  -> BINARY NOT json (nonWhitespace={nonWhitespace}/{decoded.Length}, first16={FormatFirstBytesHex(payload, 16)})");
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            RxLog($"  -> BINARY decode threw {ex.GetType().Name}");
        }

        // Binary frame: the real Gemini Live Translate server sends binary WebSocket frames
        // (the docs example implies text, but the live traffic is binary — verified 2026-08-08
        // spike). Try to decode as UTF-8 JSON. If that succeeds, return the JSON text so the
        // protocol parser can do its work. Otherwise raise a diagnostic exception that captures
        // the payload metadata so the spike runner can identify the encoding.
        try
        {
            string decoded = Encoding.UTF8.GetString(payload);
            // Be conservative: only treat the payload as JSON-text if it starts with `{` or `[`
            // after trimming. This avoids passing raw audio bytes through the protocol parser.
            int nonWhitespace = 0;
            while (nonWhitespace < decoded.Length && char.IsWhiteSpace(decoded[nonWhitespace]))
            {
                nonWhitespace++;
            }

            if (nonWhitespace < decoded.Length && (decoded[nonWhitespace] == '{' || decoded[nonWhitespace] == '['))
            {
                return decoded;
            }
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            // UTF-8 decode failed — payload is not text. Fall through to the diagnostic.
        }

        // Binary frame whose bytes are not UTF-8 JSON. Build a diagnostic message: payload length,
        // first 16 bytes as hex, UTF-8 decode attempt, and JSON-parse attempt. The spike runner
        // captures this so we can identify the actual encoding (raw audio? protobuf? something
        // else?) before changing the protocol parser.
        string hex = FormatFirstBytesHex(payload, 16);
        string utf8Attempt = TryUtf8Decode(payload);
        string jsonAttempt = TryJsonParse(payload);

        string message =
            $"Gemini Live Translate server sent a binary frame that did not decode as UTF-8 JSON. " +
            $"payloadLength={payload.Length} " +
            $"first16Bytes=[{hex}] " +
            $"utf8Attempt={utf8Attempt} " +
            $"jsonAttempt={jsonAttempt}";

        throw new InvalidOperationException(message);
    }

    private static string Prefix(string s) => s.Length <= 120 ? s : s[..120] + "...";

    /// <summary>
    /// Compact presence summary of the serverContent surfaces in a large frame: which of
    /// turnComplete / modelTurn / inputTranscription / outputTranscription / usageMetadata /
    /// groundingMetadata appear. Lets the channel log prove whether a given signal (e.g.
    /// <c>turnComplete</c>) ever arrives without dumping 16 KB of base64 audio per frame.
    /// </summary>
    private static string MarkerSummary(string decoded)
    {
        var sb = new StringBuilder(96);
        AppendFlag(sb, decoded, "tc", "turnComplete");
        AppendFlag(sb, decoded, "mt", "modelTurn");
        AppendFlag(sb, decoded, "it", "inputTranscription");
        AppendFlag(sb, decoded, "ot", "outputTranscription");
        AppendFlag(sb, decoded, "um", "usageMetadata");
        AppendFlag(sb, decoded, "gm", "groundingMetadata");
        return sb.ToString();
    }

    private static void AppendFlag(StringBuilder sb, string haystack, string label, string needle)
    {
        if (sb.Length > 0)
        {
            sb.Append(' ');
        }

        sb.Append(label).Append('=').Append(haystack.Contains(needle, StringComparison.Ordinal) ? "1" : "0");
    }

    private static string FormatFirstBytesHex(ReadOnlySpan<byte> payload, int maxBytes)
    {
        int n = Math.Min(payload.Length, maxBytes);
        if (n == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(n * 3);
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(payload[i].ToString("x2"));
        }

        if (payload.Length > n)
        {
            sb.Append($" ...({payload.Length - n} more)");
        }

        return sb.ToString();
    }

    private static string TryUtf8Decode(ReadOnlySpan<byte> payload)
    {
        try
        {
            _ = Encoding.UTF8.GetString(payload);
            return "ok";
        }
        catch (DecoderFallbackException ex)
        {
            return $"fail: {ex.Message}";
        }
    }

    private static string TryJsonParse(ReadOnlySpan<byte> payload)
    {
        try
        {
            // JsonDocument.Parse does not accept ReadOnlySpan<byte>; copy into a byte[] for the
            // attempt. The payload is bounded (small diagnostic frames only).
            byte[] copy = payload.ToArray();
            using var doc = JsonDocument.Parse(copy);
            return $"ok: top={doc.RootElement.ValueKind}";
        }
        catch (JsonException ex)
        {
            return $"fail: {ex.Message}";
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(string reason, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(CloseTimeoutMs));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeout.Token);
            try
            {
                await _socket
                    .CloseAsync(WebSocketCloseStatus.NormalClosure, reason, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // Best-effort: socket did not close within CloseTimeoutMs; abandon.
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _socket.Dispose();
        }
        catch
        {
            // Best-effort dispose.
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
