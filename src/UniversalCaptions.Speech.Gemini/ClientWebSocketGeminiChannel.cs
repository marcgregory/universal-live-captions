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
        byte[] payload = Encoding.UTF8.GetBytes(json);
        await _socket
            .SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Wait for OpenAsync to complete. The engine starts the receive loop BEFORE OpenAsync
        // returns, so without this gate the receive loop races ahead of the WebSocket handshake and
        // _socket.ReceiveAsync throws "The WebSocket is not connected" against the still-pending
        // socket.
        await _openedTcs.Task.ConfigureAwait(false);

        if (_socket.State is WebSocketState.CloseReceived or WebSocketState.Closed or WebSocketState.Aborted)
        {
            return null;
        }

        var buffer = new byte[ReceiveBufferBytes];
        using var messageStream = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await _socket
                .ReceiveAsync(new Memory<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);

            if (result.Count > 0)
            {
                messageStream.Write(buffer, 0, result.Count);
            }
        }
        while (!result.EndOfMessage);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            return null;
        }

        ReadOnlySpan<byte> payload = new ReadOnlySpan<byte>(messageStream.GetBuffer(), 0, (int)messageStream.Length);

        if (result.MessageType == WebSocketMessageType.Text)
        {
            return Encoding.UTF8.GetString(payload);
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
