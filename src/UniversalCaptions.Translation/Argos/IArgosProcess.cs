namespace UniversalCaptions.Translation.Argos;

/// <summary>
/// The boundary around a local Argos process. Engine tests substitute a fake implementation so
/// no Python runtime is required.
/// </summary>
internal interface IArgosProcess : IDisposable
{
    /// <summary>Starts the process if it is not already running.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends a translation request and waits for the matching response.
    /// </summary>
    /// <exception cref="TranslationProcessException">The process failed or timed out.</exception>
    Task<ArgosResponse> TranslateAsync(ArgosRequest request, CancellationToken cancellationToken);
}
