using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UniversalCaptions.App.Controls;
using UniversalCaptions.App.Overlay;
using UniversalCaptions.App.Pipeline;
using UniversalCaptions.App.Settings;
using UniversalCaptions.Audio.Capture;
using UniversalCaptions.Audio.Processing;
using UniversalCaptions.Captions;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Capture;
using UniversalCaptions.Core.Processing;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Core.Translation;
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

        // Single shared Argos engine instance: the concrete type is registered so the control
        // window can trigger the background pre-warm, and the interface key resolves to it so the
        // caption service and the concrete engine share one process/initialization.
        var argosEngine = new ArgosTranslationEngine(argosOptions);
        services.AddSingleton(argosEngine);
        services.AddSingleton<ITranslationEngine>(argosEngine);

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
            // Entry 14 promotion: the production default is the faster-whisper native streaming
            // engine with Chrome-style live partials (SpeechEngineFactory); UC_STT_ENGINE=ggml-base
            // selects the original local-Whisper engine as the explicit fallback. See
            // SpeechEngineFactory for the full selection table.
            return SpeechEngineFactory.Create(language);
        });

        // TD-002 auto-recovery: a WASAPI endpoint-change notifier feeds the pipeline's
        // default-device recovery coordinator; the pipeline starts/stops monitoring with each session.
        services.AddSingleton<IDeviceChangeMonitor, WasapiDeviceChangeNotifier>();

        // TD-005 settings persistence: the persisted user settings are loaded BEFORE any window is
        // constructed so the control window and overlay start with the user's last session (audio
        // source, language, translation, overlay appearance/placement/view state). Only UI preferences
        // are persisted; engine/environment knobs (UC_STT_*, Argos/Python, model selection) stay
        // env-var-driven and never appear in the settings file.
        var settingsStore = new SettingsStore();
        UserSettings userSettings = settingsStore.Load();
        services.AddSingleton<ISettingsStore>(settingsStore);
        services.AddSingleton(userSettings);

        services.AddSingleton<CaptionPipeline>();
        services.AddSingleton<CaptionOverlayWindow>();
        services.AddSingleton<IOverlayService>(sp => sp.GetRequiredService<CaptionOverlayWindow>());
        services.AddSingleton<ControlWindow>();
    }
}
