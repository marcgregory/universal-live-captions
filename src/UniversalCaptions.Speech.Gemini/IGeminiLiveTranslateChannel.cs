using System.Net.WebSockets;

namespace UniversalCaptions.Speech.Gemini;

/// <summary>
/// Transport-level seam for the Gemini Live Translate WebSocket session.
/// Implementations own the underlying <see cref="WebSocket"/> (or test fake);
/// they MUST NOT interpret Gemini protocol frames. The
/// <see cref="GeminiLiveTranslateProtocol"/> type is responsible for framing,
/// parsing, and session-state, so this seam is purely a byte/JSON transport.
/// </summary>
/// <summary>
/// Public seam so consumers (and tests) can substitute their own transport. Production code
/// uses <see cref="ClientWebSocketGeminiChannel"/>; tests inject a fake.
/// </summary>
public interface IGeminiLiveTranslateChannel : IAsyncDisposable
{
    /// <summary>
    /// Opens the WebSocket connection to the Gemini Live Translate endpoint.
    /// </summary>
    /// <param name="uri">Fully-qualified WSS endpoint including API key query string.</param>
    /// <param name="cancellationToken">Cancellation propagated to the underlying socket connect.</param>
    Task OpenAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a single UTF-8 text frame (one JSON document) to the server.
    /// </summary>
    Task SendTextAsync(string json, CancellationToken cancellationToken);

    /// <summary>
    /// Receives the next complete frame from the server and returns its contents decoded as a
    /// UTF-8 string. The contract is: text frames are returned as their UTF-8 text; binary frames
    /// whose decoded bytes form valid UTF-8 JSON are returned as that JSON text; any other binary
    /// frame raises <see cref="InvalidOperationException"/> with a diagnostic description of the
    /// payload (length, UTF-8 decode attempt, JSON parse attempt).
    /// </summary>
    /// <remarks>
    /// Returns null when the server has closed the connection cleanly (NormalClosure with no
    /// further frames) or the channel has been disposed. Otherwise returns a single coalesced
    /// string per server message.
    /// </remarks>
    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Closes the WebSocket with NormalClosure and the supplied reason. Idempotent.
    /// </summary>
    Task CloseAsync(string reason, CancellationToken cancellationToken);
}
