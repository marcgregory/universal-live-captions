namespace UniversalCaptions.Translation.Argos;

/// <summary>
/// Options that control how <see cref="ArgosTranslationEngine"/> launches and talks to the
/// local Argos process.
/// </summary>
public sealed class ArgosTranslationEngineOptions
{
    /// <summary>
    /// The path to the Python interpreter used to run the Argos server script.
    /// Defaults to <c>python</c> (resolved from PATH).
    /// </summary>
    public string PythonExecutablePath { get; set; } = "python";

    /// <summary>
    /// Path to the Argos server script. When null, the engine locates the bundled
    /// <c>argos_translate_server.py</c> next to its own assembly.
    /// </summary>
    public string? ServerScriptPath { get; set; }

    /// <summary>How long to wait for the process to start and respond to a first request.</summary>
    public TimeSpan StartupTimeout
    {
        get => _startupTimeout;
        set => _startupTimeout = RequirePositiveTimeout(value, nameof(StartupTimeout));
    }

    /// <summary>How long to wait for a translation request to complete.</summary>
    public TimeSpan RequestTimeout
    {
        get => _requestTimeout;
        set => _requestTimeout = RequirePositiveTimeout(value, nameof(RequestTimeout));
    }

    /// <summary>
    /// The short throwaway source text sent as a one-time background warm-up after the process
    /// model/lifetime loads, so the first real caption does not pay the one-time model
    /// instantiation cost. Never surfaced as a caption.
    /// </summary>
    public string WarmUpText { get; set; } = "The quick brown fox jumps over the lazy dog.";

    private TimeSpan _startupTimeout = TimeSpan.FromSeconds(30);
    private TimeSpan _requestTimeout = TimeSpan.FromSeconds(60);

    private static TimeSpan RequirePositiveTimeout(TimeSpan value, string paramName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} must be greater than zero.");
        }

        return value;
    }
}
