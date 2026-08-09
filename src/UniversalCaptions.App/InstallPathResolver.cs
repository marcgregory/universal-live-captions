using System;
using System.IO;

namespace UniversalCaptions.App;

/// <summary>
/// Resolves the Python interpreter for the faster-whisper and Argos workers when the App is
/// running from an Inno Setup install or an extracted portable ZIP, without requiring the
/// launcher to set <c>UC_FW_PYTHON</c> / <c>UC_ARGOS_PYTHON</c>. The same helper is called from
/// <see cref="SpeechEngineFactory"/> and from <c>App.xaml.cs</c> so the two resolution sites
/// share one fallback chain.
/// </summary>
/// <remarks>
/// <para>Resolution order:</para>
/// <list type="number">
///   <item>
///     <c>UC_FW_PYTHON</c> / <c>UC_ARGOS_PYTHON</c> environment variable when set (the
///     <c>packaging/launcher.cmd</c> always sets these for installed launches, so this is
///     the primary path in the installed bundle).
///   </item>
///   <item>
///     <c>{AppContext.BaseDirectory}/py/python.exe</c> — the bundled relocatable Python
///     runtime co-located with the app, used by portable-ZIP users who run
///     <c>UniversalCaptions.App.exe</c> directly without <c>launcher.cmd</c>. Same
///     <c>AppContext.BaseDirectory</c> pattern as
///     <c>LineProtocolFasterWhisperProcess.ResolveBundledScript</c> and
///     <c>LineProtocolArgosProcess.ResolveBundledScript</c>.
///   </item>
///   <item>
///     <c>%TEMP%\fwv\Scripts\python.exe</c> / <c>%TEMP%\argosv\Scripts\python.exe</c> — the
///     legacy dev venv path, preserved so <c>dotnet run</c> developers with a pre-staged
///     <c>fwv</c> / <c>argosv</c> venv keep working unchanged.
///   </item>
///   <item>
///     <c>python</c> on <c>PATH</c> as a last resort.
///   </item>
/// </list>
/// <para>
/// Behavior change vs. the pre-v0.5.31 resolver: step 2 is new. With it, a portable user who
/// runs the exe directly (skipping the launcher) and an installed user both resolve the
/// bundled runtime without any env-var setup; the existing env-var path remains the highest
/// priority so the launcher and any developer override continue to win.
/// </para>
/// </remarks>
internal static class InstallPathResolver
{
    /// <summary>
    /// Internal seam: returns the base directory used when probing for the bundled
    /// <c>py\python.exe</c>. Defaults to <see cref="AppContext.BaseDirectory"/>. Tests
    /// override this to point at a controlled temp dir without touching the production
    /// path; production always sees <c>AppContext.BaseDirectory</c>.
    /// </summary>
    internal static Func<string> BaseDirectoryAccessor { get; set; } = () => AppContext.BaseDirectory;

    /// <summary>
    /// Resolves the Python interpreter hosting the faster-whisper worker. See the
    /// <see cref="InstallPathResolver"/> remarks for the full resolution order.
    /// </summary>
    public static string ResolveFasterWhisperPython()
    {
        return ResolveBundledPython(envName: "UC_FW_PYTHON", devVenv: "fwv");
    }

    /// <summary>
    /// Resolves the Python interpreter hosting the Argos translation worker. See the
    /// <see cref="InstallPathResolver"/> remarks for the full resolution order. In the
    /// installed bundle the same <c>py\python.exe</c> is shared with the faster-whisper
    /// worker — both workers consume the single relocatable Python runtime.
    /// </summary>
    public static string ResolveArgosPython()
    {
        return ResolveBundledPython(envName: "UC_ARGOS_PYTHON", devVenv: "argosv");
    }

    private static string ResolveBundledPython(string envName, string devVenv)
    {
        string? configured = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        // Bundled install / portable-ZIP runtime co-located with the app. The check uses
        // AppContext.BaseDirectory (already the codebase's idiomatic "directory of the
        // .exe" — see LineProtocolFasterWhisperProcess.ResolveBundledScript and
        // LineProtocolArgosProcess.ResolveBundledScript for the bundled server scripts).
        string baseDir = BaseDirectoryAccessor();
        string sibling = Path.Combine(baseDir, "py", "python.exe");
        if (File.Exists(sibling))
        {
            return sibling;
        }

        // Legacy dev venv under %TEMP%. Preserved so `dotnet run` developers with a
        // pre-staged venv keep working without any change. Not an error if absent — the
        // final fallback below is the system python on PATH.
        string? tempPath = Environment.GetEnvironmentVariable("TEMP");
        if (!string.IsNullOrWhiteSpace(tempPath))
        {
            string devPython = Path.Combine(tempPath, devVenv, "Scripts", "python.exe");
            if (File.Exists(devPython))
            {
                return devPython;
            }
        }

        return "python";
    }
}
