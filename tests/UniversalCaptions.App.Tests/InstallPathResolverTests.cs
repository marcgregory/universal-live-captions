using System;
using System.IO;
using UniversalCaptions.App;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Pins the resolution chain used by both <see cref="SpeechEngineFactory"/> (faster-whisper
/// worker python) and the Argos wiring in <c>App.xaml.cs</c> (Argos server python). The
/// chain is:
/// <c>UC_FW_PYTHON</c> / <c>UC_ARGOS_PYTHON</c> env var → bundled <c>py\python.exe</c>
/// sibling of the app → legacy <c>%TEMP%\fwv</c> / <c>%TEMP%\argosv</c> dev venv →
/// system <c>python</c> on PATH.
/// </summary>
public class InstallPathResolverTests
    : IDisposable
{
    private const string FwEnv = "UC_FW_PYTHON";
    private const string ArgosEnv = "UC_ARGOS_PYTHON";
    private const string TempEnv = "TEMP";

    private readonly string? _previousFw;
    private readonly string? _previousArgos;
    private readonly string? _previousTemp;
    private readonly Func<string> _previousBaseAccessor;
    private readonly string? _scratchRoot;

    public InstallPathResolverTests()
    {
        _previousFw = Environment.GetEnvironmentVariable(FwEnv);
        _previousArgos = Environment.GetEnvironmentVariable(ArgosEnv);
        _previousTemp = Environment.GetEnvironmentVariable(TempEnv);
        _previousBaseAccessor = InstallPathResolver.BaseDirectoryAccessor;
        _scratchRoot = Path.Combine(Path.GetTempPath(), "uc_install_resolver_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FwEnv, _previousFw);
        Environment.SetEnvironmentVariable(ArgosEnv, _previousArgos);
        Environment.SetEnvironmentVariable(TempEnv, _previousTemp);
        InstallPathResolver.BaseDirectoryAccessor = _previousBaseAccessor;
        if (_scratchRoot is not null && Directory.Exists(_scratchRoot))
        {
            try { Directory.Delete(_scratchRoot, recursive: true); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void EnvVarSet_Wins_OverInstallSiblingAndDevVenv()
    {
        // Even when both an install sibling and a dev venv exist, the explicit env var
        // (set by the installer launcher) takes priority.
        string installSibling = CreateFakeInstallPython();
        string sharedTemp = Path.Combine(_scratchRoot!, "dev_temp_env");
        Directory.CreateDirectory(sharedTemp);
        string devVenvPython = CreateFakeDevVenvPythonUnder(sharedTemp, "fwv");

        Environment.SetEnvironmentVariable(FwEnv, @"C:\explicit\python.exe");
        InstallPathResolver.BaseDirectoryAccessor = () => Path.GetDirectoryName(Path.GetDirectoryName(installSibling))!;
        Environment.SetEnvironmentVariable(TempEnv, sharedTemp);

        string resolved = InstallPathResolver.ResolveFasterWhisperPython();
        Assert.Equal(@"C:\explicit\python.exe", resolved);
    }

    [Fact]
    public void EnvVarUnset_InstallSibling_FoundAndReturned()
    {
        // The bundled install / portable-ZIP case: the .exe lives next to a py\python.exe.
        // This is the new auto-resolution seam added in v0.5.31.
        // IMPORTANT: override TEMP so the dev venv fallback (which exists on real dev
        // machines at %TEMP%\fwv) does not pre-empt the install sibling and mask the test.
        string installSibling = CreateFakeInstallPython();
        string emptyTemp = Path.Combine(_scratchRoot!, "empty_temp");
        Directory.CreateDirectory(emptyTemp);

        Environment.SetEnvironmentVariable(FwEnv, null);
        // The accessor must return the install ROOT (parent of py/), not the dir
        // containing python.exe — the resolver appends "py/python.exe" itself.
        string baseDir = Path.GetDirectoryName(Path.GetDirectoryName(installSibling)!)!;
        InstallPathResolver.BaseDirectoryAccessor = () => baseDir;
        Environment.SetEnvironmentVariable(TempEnv, emptyTemp);

        Assert.True(File.Exists(installSibling), $"setup: expected file does not exist at {installSibling}");
        Assert.True(Directory.Exists(baseDir), $"setup: base dir missing at {baseDir}");

        string resolved = InstallPathResolver.ResolveFasterWhisperPython();
        Assert.Equal(installSibling, resolved);
    }

    [Fact]
    public void EnvVarUnset_NoInstallSibling_DevVenvUnderTemp_FoundAndReturned()
    {
        // The legacy dev workflow: `dotnet run` developer with a pre-staged %TEMP%\fwv
        // venv. Auto-resolution must still work so existing developers see no behavior
        // change.
        string sharedTemp = Path.Combine(_scratchRoot!, "dev_temp");
        Directory.CreateDirectory(sharedTemp);
        string devVenvPython = CreateFakeDevVenvPythonUnder(sharedTemp, "fwv");
        string installRoot = Path.Combine(_scratchRoot!, "empty_install");
        Directory.CreateDirectory(installRoot);

        Environment.SetEnvironmentVariable(FwEnv, null);
        InstallPathResolver.BaseDirectoryAccessor = () => installRoot;
        Environment.SetEnvironmentVariable(TempEnv, sharedTemp);

        string resolved = InstallPathResolver.ResolveFasterWhisperPython();
        Assert.Equal(devVenvPython, resolved);
    }

    [Fact]
    public void EnvVarUnset_NoInstallSibling_NoDevVenv_FallsBackToSystemPython()
    {
        // No env var, no install sibling, no dev venv → system python on PATH.
        string installRoot = Path.Combine(_scratchRoot!, "no_install");
        Directory.CreateDirectory(installRoot);
        string emptyTemp = Path.Combine(_scratchRoot!, "no_venv");
        Directory.CreateDirectory(emptyTemp);

        Environment.SetEnvironmentVariable(FwEnv, null);
        InstallPathResolver.BaseDirectoryAccessor = () => installRoot;
        Environment.SetEnvironmentVariable(TempEnv, emptyTemp);

        string resolved = InstallPathResolver.ResolveFasterWhisperPython();
        Assert.Equal("python", resolved);
    }

    [Fact]
    public void ArgosResolver_UsesIndependentDevVenvName_ButSameInstallSibling()
    {
        // The Argos resolver must look for %TEMP%\argosv (not %TEMP%\fwv) when
        // auto-detecting a dev venv, while still sharing the install-root
        // py\python.exe with the faster-whisper resolver.
        string sharedInstall = CreateFakeInstallPython();
        string installRoot = Path.Combine(_scratchRoot!, "shared_install");
        Directory.CreateDirectory(installRoot);
        // Place the same shared install path under this base so both resolvers
        // pick it up.
        string sharedInstallForRoot = Path.Combine(installRoot, "py", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(sharedInstallForRoot)!);
        File.WriteAllBytes(sharedInstallForRoot, Array.Empty<byte>());

        Environment.SetEnvironmentVariable(FwEnv, null);
        Environment.SetEnvironmentVariable(ArgosEnv, null);
        InstallPathResolver.BaseDirectoryAccessor = () => installRoot;
        Environment.SetEnvironmentVariable(TempEnv, Path.GetTempPath()); // no venvs under here

        // Both resolvers see the same bundled runtime.
        Assert.Equal(sharedInstallForRoot, InstallPathResolver.ResolveFasterWhisperPython());
        Assert.Equal(sharedInstallForRoot, InstallPathResolver.ResolveArgosPython());
    }

    [Fact]
    public void ArgosResolver_WhenNoInstallSibling_FindsArgosvDevVenv_NotFwv()
    {
        // Pin the dev-venv naming distinction: %TEMP%\argosv is for Argos, not Fw.
        // A pre-staged %TEMP%\fwv must NOT be returned by the Argos resolver.
        string installRoot = Path.Combine(_scratchRoot!, "no_install");
        Directory.CreateDirectory(installRoot);
        // Both venvs under a single shared temp root.
        string sharedTemp = Path.Combine(_scratchRoot!, "shared_temp");
        Directory.CreateDirectory(sharedTemp);
        string fwDevPython = CreateFakeDevVenvPythonUnder(sharedTemp, "fwv");
        string argosDevPython = CreateFakeDevVenvPythonUnder(sharedTemp, "argosv");

        Environment.SetEnvironmentVariable(FwEnv, null);
        Environment.SetEnvironmentVariable(ArgosEnv, null);
        InstallPathResolver.BaseDirectoryAccessor = () => installRoot;
        Environment.SetEnvironmentVariable(TempEnv, sharedTemp);

        // Fw resolver finds fwv.
        Assert.Equal(fwDevPython, InstallPathResolver.ResolveFasterWhisperPython());
        // Argos resolver finds argosv (NOT fwv — distinct dev-venv name).
        Assert.Equal(argosDevPython, InstallPathResolver.ResolveArgosPython());
    }

    [Fact]
    public void EnvVarSetToWhitespace_FallsThroughToInstallSibling()
    {
        // Defensive: a whitespace-only env var must not be treated as configured.
        // The original v0.5.25+ resolver already used IsNullOrWhiteSpace; preserve it.
        // Override TEMP so the real %TEMP%\fwv dev venv does not win via the dev-venv
        // fallback (this is a real path on the dev machine that runs these tests).
        string installSibling = CreateFakeInstallPython();
        string emptyTemp = Path.Combine(_scratchRoot!, "empty_temp_ws");
        Directory.CreateDirectory(emptyTemp);

        Environment.SetEnvironmentVariable(FwEnv, "   ");
        // The accessor must return the install ROOT (parent of py/), not the dir
        // containing python.exe — the resolver appends "py/python.exe" itself.
        string baseDir = Path.GetDirectoryName(Path.GetDirectoryName(installSibling)!)!;
        InstallPathResolver.BaseDirectoryAccessor = () => baseDir;
        Environment.SetEnvironmentVariable(TempEnv, emptyTemp);

        string resolved = InstallPathResolver.ResolveFasterWhisperPython();
        Assert.Equal(installSibling, resolved);
    }

    /// <summary>
    /// Creates a fake <c>py\python.exe</c> inside a unique scratch subdir of the test's
    /// scratch root. Returns the full path to the fake file.
    /// </summary>
    private string CreateFakeInstallPython()
    {
        string root = Path.Combine(_scratchRoot!, "install_" + Guid.NewGuid().ToString("N"));
        string pyDir = Path.Combine(root, "py");
        Directory.CreateDirectory(pyDir);
        string path = Path.Combine(pyDir, "python.exe");
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    /// <summary>
    /// Creates a fake dev-venv python at <c>{TEMP}\{devVenv}\Scripts\python.exe</c>.
    /// </summary>
    private string CreateFakeDevVenvPython(string devVenv)
    {
        string tempRoot = Path.Combine(_scratchRoot!, "temp_" + Guid.NewGuid().ToString("N"));
        return CreateFakeDevVenvPythonUnder(tempRoot, devVenv);
    }

    /// <summary>
    /// Creates a fake dev-venv python at <c>{tempRoot}\{devVenv}\Scripts\python.exe</c>.
    /// Used when a test needs multiple venvs under the same TEMP root.
    /// </summary>
    private string CreateFakeDevVenvPythonUnder(string tempRoot, string devVenv)
    {
        string scripts = Path.Combine(tempRoot, devVenv, "Scripts");
        Directory.CreateDirectory(scripts);
        string path = Path.Combine(scripts, "python.exe");
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }
}
