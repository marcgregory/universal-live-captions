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

namespace UniversalCaptions.App;

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
        services.AddSingleton<ITranslationEngine>(_ => new ArgosTranslationEngine());

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
}
