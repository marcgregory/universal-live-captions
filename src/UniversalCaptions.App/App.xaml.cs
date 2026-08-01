using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UniversalCaptions.App.Controls;
using UniversalCaptions.App.Overlay;
using UniversalCaptions.App.Pipeline;
using UniversalCaptions.Audio.Capture;
using UniversalCaptions.Audio.Processing;
using UniversalCaptions.Captions;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Capture;
using UniversalCaptions.Core.Processing;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Core.Translation;
using UniversalCaptions.Speech;
using UniversalCaptions.Translation;
using UniversalCaptions.Translation.Argos;

namespace UniversalCaptions.App;
#pragma warning disable CA1416

/// <summary>
/// The application bootstrap and DI composition root (TD-003): constructs the real pipeline once
/// (loopback capture, audio processor, local Whisper engine, optional local Argos translation,
/// caption service) and shows the control window and the caption overlay. WPF code here stays thin;
/// all wiring lives behind the Core contracts.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _provider;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        RegisterServices(services);
        _provider = services.BuildServiceProvider();

        var controlWindow = _provider.GetRequiredService<ControlWindow>();
        var overlay = _provider.GetRequiredService<IOverlayService>();

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow = controlWindow;
        controlWindow.Show();
        overlay.Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _provider?.Dispose();
        base.OnExit(e);
    }

    private static void RegisterServices(IServiceCollection services)
    {
        var argosOptions = new ArgosTranslationEngineOptions();
        string? envPython = Environment.GetEnvironmentVariable("UC_ARGOS_PYTHON");
        if (!string.IsNullOrWhiteSpace(envPython))
        {
            argosOptions.PythonExecutablePath = envPython.Trim();
        }
        else
        {
            var tempPath = Environment.GetEnvironmentVariable("TEMP");
            if (!string.IsNullOrWhiteSpace(tempPath))
            {
                var autoPython = Path.Combine(tempPath, "argosv", "Scripts", "python.exe");
                if (File.Exists(autoPython))
                {
                    argosOptions.PythonExecutablePath = autoPython;
                }
            }
        }

        services.AddSingleton<ITranslationEngine>(_ => new ArgosTranslationEngine(argosOptions));

        var captionOptions = new CaptionServiceOptions(sourceLanguage: "en", targetLanguage: "en", historyCapacity: 50);
        services.AddSingleton(captionOptions);

        services.AddSingleton<ICaptionService>(sp => new CaptionService(
            captionOptions,
            sp.GetRequiredService<ITranslationEngine>()));

        services.AddSingleton<IAudioProcessor>(_ => new AudioProcessor(new AudioFormat(16_000, 1, 32)));

        services.AddSingleton<Func<string?, IAudioCapture>>(_ => deviceId =>
            deviceId is null
                ? WasapiLoopbackCaptureSource.CreateDefault()
                : WasapiLoopbackCaptureSource.CreateForDevice(deviceId));

        services.AddSingleton<Func<string?, ISpeechToTextEngine>>(_ => language =>
        {
            var options = new WhisperEngineOptions
            {
                ModelPath = ResolveModelPath(),
                Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim().ToLowerInvariant(),
                WindowDuration = TimeSpan.FromSeconds(ResolveDoubleEnv("UC_STT_WINDOW", 8)),
                // 0.5 s interval: decodes 2× per second so partials appear as the speaker talks
                // without triggering epoch boundary transitions too frequently (was 0.3 s, which
                // caused rapid duplicate caption replay due to Whisper sliding-window resets).
                DecodeInterval = TimeSpan.FromSeconds(ResolveDoubleEnv("UC_STT_INTERVAL", 0.5)),
                // 0.5 s minimum before first decode: Whisper can produce reliable output from
                // ~0.5 s of audio. The previous 2 s default guaranteed a 2 s silent wait before
                // the first caption ever appeared.
                MinimumAudioBeforeFirstDecode = TimeSpan.FromSeconds(ResolveDoubleEnv("UC_STT_MIN_AUDIO", 0.5)),
                StabilityWindow = ResolveIntEnv("UC_STT_STABILITY", 2),
            };
            return new WhisperSpeechToTextEngine(options);
        });

        services.AddSingleton<CaptionPipeline>();
        services.AddSingleton<CaptionOverlayWindow>();
        services.AddSingleton<IOverlayService>(sp => sp.GetRequiredService<CaptionOverlayWindow>());
        services.AddSingleton<ControlWindow>();
    }

    /// <summary>
    /// Resolves the Whisper model path: the <c>UC_STT_MODEL_PATH</c> environment variable when set,
    /// otherwise the repository-relative <c>artifacts/models/ggml-base.bin</c>.
    /// </summary>
    private static string ResolveModelPath()
    {
        string? configured = Environment.GetEnvironmentVariable("UC_STT_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine("artifacts", "models", "ggml-base.bin");
    }

    /// <summary>
    /// Reads an optional integer benchmark override (for example <c>UC_STT_STABILITY</c>); returns
    /// <paramref name="fallback"/> when unset or unparseable. Overrides never change the built-in
    /// default — the fallback here is the validated Slice 6 baseline (8 s window / 1 s interval /
    /// StabilityWindow 2), the single authoritative configuration shared with the benchmark.
    /// </summary>
    private static int ResolveIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int value) ? value : fallback;
    }

    private static double ResolveDoubleEnv(string name, double fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;
    }
}
