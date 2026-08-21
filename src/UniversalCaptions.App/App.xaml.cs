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
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.App;
#pragma warning disable CA1416

/// <summary>
/// The application bootstrap and DI composition root (TD-003): constructs the real pipeline once
/// (loopback capture, audio processor, Gemini Live speech engine — transcription + translation,
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
        var captionOptions = new CaptionServiceOptions(sourceLanguage: "en", targetLanguage: "en", historyCapacity: 50);
        services.AddSingleton(captionOptions);

        // ADR-0011: the caption service is a pure relay — Gemini owns transcription AND translation.
        services.AddSingleton<ICaptionService>(_ => new CaptionService(captionOptions));

        services.AddSingleton<IAudioProcessor>(_ => new AudioProcessor(new AudioFormat(16_000, 1, 32)));

        services.AddSingleton<Func<string?, IAudioCapture>>(_ => deviceId =>
            deviceId is null
                ? WasapiLoopbackCaptureSource.CreateDefault()
                : WasapiLoopbackCaptureSource.CreateForDevice(deviceId));

        // The session's single speech engine factory (ADR-0009 + ADR-0011): the Gemini API key comes
        // from the Windows Credential Manager via ICredentialStore; the credential store is
        // registered below so the factory closure can resolve it. The factory reads the key once
        // when a session starts and the engine drops it from memory on Dispose.
        services.AddSingleton<ICredentialStore>(_ => new WindowsCredentialStore());
        services.AddSingleton<Func<(string? SourceLanguage, string? TargetLanguage), ILiveAudioTranslationEngine?>>(
            sp => pair => LiveTranslationEngineFactory.Create(
                sp.GetRequiredService<ICredentialStore>(),
                pair.SourceLanguage,
                pair.TargetLanguage));

        // Gemini availability (actionable errors): the evaluator reads the stored key, applies the
        // fast local syntax gate, and runs an authoritative live validation via the REST key
        // validator so the control window can surface a precise message when the stored key is bad.
        services.AddSingleton<IGeminiApiKeyValidator>(_ => new GeminiRestApiKeyValidator());
        services.AddSingleton<GeminiAvailabilityEvaluator>();

        // TD-002 auto-recovery: a WASAPI endpoint-change notifier feeds the pipeline's
        // default-device recovery coordinator; the pipeline starts/stops monitoring with each session.
        services.AddSingleton<IDeviceChangeMonitor, WasapiDeviceChangeNotifier>();

        // TD-005 settings persistence: the persisted user settings are loaded BEFORE any window is
        // constructed so the control window and overlay start with the user's last session (audio
        // source, language, translation, overlay appearance/placement/view state). Only UI preferences
        // are persisted; engine/environment knobs (UC_GEMINI_*) stay env-var-driven and never appear
        // in the settings file.
        var settingsStore = new SettingsStore();
        UserSettings userSettings = settingsStore.Load();
        services.AddSingleton<ISettingsStore>(settingsStore);
        services.AddSingleton(userSettings);

        services.AddSingleton<CaptionPipeline>(sp => new CaptionPipeline(
                    sp.GetRequiredService<Func<string?, IAudioCapture>>(),
                    sp.GetRequiredService<IAudioProcessor>(),
                    sp.GetRequiredService<ICaptionService>(),
                    sp.GetRequiredService<Func<(string? SourceLanguage, string? TargetLanguage), ILiveAudioTranslationEngine?>>(),
                    sp.GetRequiredService<IDeviceChangeMonitor>(),
                    captionOptions.SourceLanguage,
                    captionOptions.TargetLanguage));
        services.AddSingleton<CaptionOverlayWindow>();
        services.AddSingleton<IOverlayService>(sp => sp.GetRequiredService<CaptionOverlayWindow>());
        services.AddSingleton<ControlWindow>();
    }
}
