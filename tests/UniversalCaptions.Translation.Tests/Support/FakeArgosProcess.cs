using UniversalCaptions.Core.Translation;
using UniversalCaptions.Translation.Argos;

namespace UniversalCaptions.Translation.Tests.Support;

/// <summary>
/// A deterministic <see cref="IArgosProcess"/> used to test <see cref="ArgosTranslationEngine"/>
/// without a Python runtime.
/// </summary>
internal sealed class FakeArgosProcess : IArgosProcess
{
    private readonly List<ArgosRequest> _requests = [];
    private Func<ArgosRequest, ArgosResponse>? _handler;
    private Exception? _startException;
    private bool _throwOnTranslate;
    private TranslationErrorKind _translateErrorKind;
    private string _translateErrorMessage = "translate failed";
    private TimeSpan _translateDelay;

    /// <summary>True once <see cref="StartAsync"/> succeeded.</summary>
    public bool Started { get; private set; }

    /// <summary>Number of times <see cref="StartAsync"/> was called.</summary>
    public int StartCount { get; private set; }

    /// <summary>True once <see cref="Dispose"/> was called.</summary>
    public bool Disposed { get; private set; }

    /// <summary>All requests received, in order.</summary>
    public IReadOnlyList<ArgosRequest> Requests => _requests;

    /// <summary>Makes the next <see cref="StartAsync"/> throw.</summary>
    public void ThrowOnStart(Exception exception) => _startException = exception;

    /// <summary>Makes every <see cref="TranslateAsync"/> throw a process failure.</summary>
    public void FailOnTranslate(TranslationErrorKind kind, string message)
    {
        _throwOnTranslate = true;
        _translateErrorKind = kind;
        _translateErrorMessage = message;
    }

    /// <summary>Clears a failure set by <see cref="FailOnTranslate"/>.</summary>
    public void ClearFailure()
    {
        _throwOnTranslate = false;
    }

    /// <summary>Adds artificial delay to each translation.</summary>
    public void AddTranslateDelay(TimeSpan delay) => _translateDelay = delay;

    /// <summary>Sets the deterministic response for every translation request.</summary>
    public void SetHandler(Func<ArgosRequest, ArgosResponse> handler) => _handler = handler;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        if (_startException is not null)
        {
            throw _startException;
        }

        Started = true;
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<ArgosResponse> TranslateAsync(ArgosRequest request, CancellationToken cancellationToken)
    {
        _requests.Add(request);
        if (_translateDelay > TimeSpan.Zero)
        {
            await Task.Delay(_translateDelay, cancellationToken);
        }

        if (_throwOnTranslate)
        {
            throw new TranslationProcessException(_translateErrorKind, _translateErrorMessage);
        }

        return _handler is not null
            ? _handler(request)
            : new ArgosResponse(true, $"[{request.Target}] {request.Text}", null, false, null, null, null, null);
    }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
