using System.Collections.Concurrent;

namespace UniversalCaptions.Speech.Gemini.Tests;

/// <summary>
/// Deterministic <see cref="IGeminiLiveTranslateChannel"/> used to drive
/// <see cref="GeminiLiveTranslateEngine"/> without a network. Tests inject the exact server frames
/// they want to deliver, the fake captures every frame the engine sends back, and lifecycle knobs
/// (connect/close/cancellation) are scripted so each test can reproduce a specific failure mode.
/// </summary>
/// <remarks>
/// <para>
/// Concurrency: the engine's receive loop reads from <see cref="Receive"/> on a background task.
/// The fake's mutating methods (queue a frame, set <see cref="OpenBehavior"/>, etc.) are called from
/// the test's primary thread, so the public surface is gated by <see cref="Sync"/>; reads from the
/// engine thread do not lock so a slow test cannot deadlock the engine.
/// </para>
/// <para>
/// Backpressure: <see cref="Receive"/> is unbounded — the engine owns its own bounded queue and
/// drop policy (deferred to A6 but not yet implemented; the fake simply hands frames over). This is
/// fine because the engine tests are short-lived; production-grade backpressure is a separate
/// concern.
/// </para>
/// </remarks>
internal sealed class FakeGeminiChannel : IGeminiLiveTranslateChannel
{
    private readonly object _sync = new();
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly List<string> _sent = new();
    private bool _open;
    private bool _disposed;
    private bool _receiveReturnsNullOnEmpty;

    /// <summary>How <see cref="OpenAsync"/> should behave the first time it is called.</summary>
    public OpenBehaviorKind OpenBehavior { get; set; } = OpenBehaviorKind.Succeed;

    /// <summary>How <see cref="CloseAsync"/> should behave when called.</summary>
    public CloseBehaviorKind CloseBehavior { get; set; } = CloseBehaviorKind.Succeed;

    /// <summary>Number of times <see cref="OpenAsync"/> has been called.</summary>
    public int OpenCount { get; private set; }

    /// <summary>Number of times <see cref="CloseAsync"/> has been called.</summary>
    public int CloseCount { get; private set; }

    /// <summary>Number of times <see cref="DisposeAsync"/> has been called.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>All frames the engine sent to <see cref="SendTextAsync"/>, in order.</summary>
    public IReadOnlyList<string> SentFrames
    {
        get { lock (_sync) { return _sent.ToArray(); } }
    }

    /// <summary>The last frame the engine sent, or <c>null</c> when nothing was sent.</summary>
    public string? LastSentFrame
    {
        get { lock (_sync) { return _sent.Count == 0 ? null : _sent[^1]; } }
    }

    /// <summary>Captures every received frame for inspection. Counts every <see cref="ReceiveTextAsync"/> call, even ones that return null.</summary>
    public int ReceiveCount { get; private set; }

    /// <summary>
    /// When <c>true</c>, <see cref="ReceiveTextAsync"/> returns <c>null</c> when the queue is
    /// empty instead of blocking. Tests use this to advance the receive loop deterministically
    /// after queuing the desired number of frames.
    /// </summary>
    public bool ReceiveReturnsNullOnEmpty
    {
        get => _receiveReturnsNullOnEmpty;
        set => _receiveReturnsNullOnEmpty = value;
    }

    /// <summary>Enqueues one server frame to be returned by the next <see cref="ReceiveTextAsync"/> call.</summary>
    public void QueueServerFrame(string json) => _incoming.Enqueue(json);

    /// <summary>Enqueues multiple server frames in order.</summary>
    public void QueueServerFrames(params string[] frames)
    {
        foreach (string frame in frames)
        {
            _incoming.Enqueue(frame);
        }
    }

    /// <inheritdoc />
    public Task OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        OpenCount++;
        cancellationToken.ThrowIfCancellationRequested();

        switch (OpenBehavior)
        {
            case OpenBehaviorKind.Succeed:
                lock (_sync) { _open = true; }
                return Task.CompletedTask;

            case OpenBehaviorKind.ThrowConnectionFailed:
                throw new InvalidOperationException("simulated connect failure");

            case OpenBehaviorKind.ThrowAuthRejected:
                throw new InvalidOperationException("simulated auth rejection");

            default:
                throw new InvalidOperationException("unhandled OpenBehavior");
        }
    }

    /// <inheritdoc />
    public Task SendTextAsync(string json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(json);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) { _sent.Add(json); }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        ReceiveCount++;
        cancellationToken.ThrowIfCancellationRequested();

        if (_incoming.TryDequeue(out string? frame))
        {
            return Task.FromResult<string?>(frame);
        }

        if (_receiveReturnsNullOnEmpty)
        {
            return Task.FromResult<string?>(null);
        }

        // Tests can override this via ReceiveReturnsNullOnEmpty; a blocking wait would deadlock
        // the test runner. The default for new tests should be the non-blocking mode.
        return Task.FromResult<string?>(null);
    }

    /// <inheritdoc />
    public Task CloseAsync(string reason, CancellationToken cancellationToken)
    {
        CloseCount++;
        switch (CloseBehavior)
        {
            case CloseBehaviorKind.Succeed:
                return Task.CompletedTask;

            case CloseBehaviorKind.Throw:
                throw new InvalidOperationException("simulated close failure");

            default:
                throw new InvalidOperationException("unhandled CloseBehavior");
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        lock (_sync) { _disposed = true; _open = false; }
        return ValueTask.CompletedTask;
    }

    /// <summary>True once <see cref="OpenAsync"/> has succeeded.</summary>
    public bool IsOpen
    {
        get { lock (_sync) { return _open && !_disposed; } }
    }

    /// <summary>Scripted behavior for <see cref="OpenAsync"/>.</summary>
    public enum OpenBehaviorKind
    {
        /// <summary>Open succeeds; <see cref="IsOpen"/> becomes <c>true</c>.</summary>
        Succeed,

        /// <summary>Open throws a generic connection failure.</summary>
        ThrowConnectionFailed,

        /// <summary>Open throws an authentication-style rejection.</summary>
        ThrowAuthRejected,
    }

    /// <summary>Scripted behavior for <see cref="CloseAsync"/>.</summary>
    public enum CloseBehaviorKind
    {
        /// <summary>Close succeeds.</summary>
        Succeed,

        /// <summary>Close throws.</summary>
        Throw,
    }
}
