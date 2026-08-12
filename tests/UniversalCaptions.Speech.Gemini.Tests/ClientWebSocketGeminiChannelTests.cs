namespace UniversalCaptions.Speech.Gemini.Tests;

/// <summary>
/// Deterministic tests for <see cref="ClientWebSocketGeminiChannel"/>'s send-before-open gate.
/// No network: these verify the ordering contract (a send issued while the WebSocket handshake is
/// still pending must WAIT, not throw "The WebSocket is not connected") and the failure contract
/// (an open that never completes must not hang the caller forever — cancellation unblocks it).
/// Regression for the 2026-08-12 send-before-open race: the App starts audio capture before the
/// live-translation block, so the engine's send loop pushed audio frames at the channel before
/// <c>OpenAsync</c> completed; without the gate, <c>SendAsync</c> threw
/// <c>InvalidOperationException: The WebSocket is not connected</c>, raised
/// <c>TranslationFailed</c>, and tore the session down before any translation reached the overlay.
/// </summary>
public sealed class ClientWebSocketGeminiChannelTests
{
    [Fact]
    public async Task SendTextAsync_BeforeOpen_WaitsForOpen_InsteadOfThrowingNotConnected()
    {
        var channel = new ClientWebSocketGeminiChannel();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        // Issue the send WITHOUT opening the socket. The gate must block until OpenAsync completes
        // or cancellation fires — it must NOT immediately throw "The WebSocket is not connected"
        // (the pre-fix behavior that killed the session at startup).
        Task send = channel.SendTextAsync("{\"test\":true}", cts.Token);

        // Give the buggy (pre-gate) path a window to throw; with the gate it stays pending.
        await Task.Delay(150);
        Assert.False(send.IsCompleted, "SendTextAsync must wait for the handshake, not throw.");

        // Cancellation unblocks the gate without the send having touched the socket.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
    }

    [Fact]
    public async Task SendTextAsync_BeforeOpen_UnblockedByCancellation_WhenOpenNeverCompletes()
    {
        var channel = new ClientWebSocketGeminiChannel();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // If open never completes, a pending send must exit via cancellation — never hang.
        // Task.WaitAsync surfaces cancellation as TaskCanceledException (a subclass of
        // OperationCanceledException), so ThrowsAnyAsync is required.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => channel.SendTextAsync("{\"test\":true}", cts.Token));
    }

    [Fact]
    public async Task SendTextAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var channel = new ClientWebSocketGeminiChannel();
        await channel.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => channel.SendTextAsync("{\"test\":true}", CancellationToken.None));
    }
}
