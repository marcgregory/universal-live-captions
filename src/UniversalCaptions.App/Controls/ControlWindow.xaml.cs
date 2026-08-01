using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UniversalCaptions.App.Overlay;
using UniversalCaptions.App.Pipeline;
using UniversalCaptions.Audio.Capture;
using UniversalCaptions.Core.Captions;

namespace UniversalCaptions.App.Controls;

/// <summary>
/// The minimal control window: selects the audio source and speech language, toggles translation
/// and its target, starts/stops captions, shows status and latency, and applies overlay appearance
/// settings (FR-8/FR-9/FR-10/FR-14). It only calls the Core contracts and the pipeline; WPF event
/// handlers marshal pipeline events onto the dispatcher.
/// </summary>
public partial class ControlWindow : Window
{
    private readonly CaptionPipeline _pipeline;
    private readonly IOverlayService _overlay;
    private readonly ICaptionService _captions;
    private readonly string _captionSourceLanguage;

    private sealed record LanguageOption(string Label, string? Code);

    private static readonly LanguageOption[] SourceLanguages =
    [
        new("Auto (detect)", null),
        new("English (en)", "en"),
        new("Japanese (ja)", "ja"),
        new("Tagalog (tl)", "tl"),
    ];

    private static readonly LanguageOption[] TargetLanguages =
    [
        new("English (en)", "en"),
        new("Japanese (ja)", "ja"),
        new("Tagalog (tl)", "tl"),
    ];

    /// <summary>
    /// Creates the control window.
    /// </summary>
    /// <param name="pipeline">The caption pipeline this window starts and stops.</param>
    /// <param name="overlay">The overlay this window configures.</param>
    /// <param name="captions">The caption service this window toggles translation on.</param>
    /// <param name="captionOptions">The caption service options, for the caption source language.</param>
    public ControlWindow(CaptionPipeline pipeline, IOverlayService overlay, ICaptionService captions, CaptionServiceOptions captionOptions)
    {
        _pipeline = pipeline;
        _overlay = overlay;
        _captions = captions;
        _captionSourceLanguage = (captionOptions ?? throw new ArgumentNullException(nameof(captionOptions))).SourceLanguage;
        InitializeComponent();

        Loaded += OnLoaded;
        Closed += OnClosed;
        _pipeline.StatusChanged += OnPipelineStatus;
        _pipeline.LatencyUpdated += OnLatencyUpdated;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AudioSourceLoadResult result = AudioSourceLoader.Load(
            LoopbackDeviceEnumerator.EnumerateRenderDevices,
            LoopbackDeviceEnumerator.GetDefaultRenderDevice);

        if (!result.Succeeded)
        {
            AudioSourceCombo.IsEnabled = false;
            StartButton.IsEnabled = false;
            StatusText.Text = "Could not list audio devices. Check that the Windows audio service is running.";
            SetIndicator(PipelineStatusKind.Error);
        }
        else if (result.Devices.Count == 0)
        {
            AudioSourceCombo.IsEnabled = false;
            StatusText.Text = "No audio output device found. Connect a speaker or headset.";
            SetIndicator(PipelineStatusKind.Error);
        }
        else
        {
            AudioSourceCombo.ItemsSource = result.Devices;
            AudioSourceCombo.DisplayMemberPath = nameof(LoopbackDevice.FriendlyName);
            int index = result.Preferred is null ? 0 : FindDeviceIndex(result.Devices, result.Preferred.Id);
            AudioSourceCombo.SelectedIndex = index;
        }

        LanguageCombo.ItemsSource = SourceLanguages;
        LanguageCombo.SelectedIndex = 0;

        TargetLanguageCombo.ItemsSource = TargetLanguages;
        TargetLanguageCombo.SelectedIndex = 0;
        TargetLanguageCombo.IsEnabled = false;

        OpacitySlider.Value = _overlay.Opacity;
        FontSizeSlider.Value = _overlay.FontSize;
    }

    private static int FindDeviceIndex(IReadOnlyList<LoopbackDevice> devices, string id)
    {
        for (int i = 0; i < devices.Count; i++)
        {
            if (string.Equals(devices[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _pipeline.StatusChanged -= OnPipelineStatus;
        _pipeline.LatencyUpdated -= OnLatencyUpdated;
        StopPipeline();
    }

    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        string? deviceId = (AudioSourceCombo.SelectedItem as LoopbackDevice)?.Id;
        string? language = (LanguageCombo.SelectedItem as LanguageOption)?.Code;
        _pipeline.Start(deviceId, language);
    }

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        StopPipeline();
    }

    /// <summary>
    /// Stops captions off the UI thread. The pipeline returns immediately and tears the session
    /// down on a background task, but the stop must still not run on the UI thread so the WPF
    /// dispatcher is never held up during teardown (ARCHITECTURE: UI thread never blocks on the
    /// audio pipeline). The app waits for teardown in <c>App.OnExit</c> via <c>CaptionPipeline.Dispose</c>.
    /// </summary>
    private void StopPipeline()
    {
        _ = Task.Run(() => _pipeline.Stop());
    }

    private void OnTranslationToggled(object sender, RoutedEventArgs e)
    {
        TargetLanguageCombo.IsEnabled = TranslationToggle.IsChecked == true;
        ApplyTranslationSettings();
    }

    private void OnTargetLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyTranslationSettings();
        }
    }

    private void ApplyTranslationSettings()
    {
        bool enabled = TranslationToggle.IsChecked == true;
        string? target = (TargetLanguageCombo.SelectedItem as LanguageOption)?.Code;

        if (enabled)
        {
            string? error = TranslationGuard.Validate(_captionSourceLanguage, target);
            if (error is not null)
            {
                // Keep the toggle on and the target combo enabled so the user can choose a valid
                // target language; just surface the reason and do not apply a translation that
                // would always fail at the backend (a language cannot be translated into itself).
                StatusText.Text = error;
                SetIndicator(PipelineStatusKind.Error);
                return;
            }
        }

        _captions.SetTranslationEnabled(enabled, target);
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _overlay.Opacity = e.NewValue;
    }

    private void OnFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _overlay.FontSize = e.NewValue;
    }

    private void OnClickThroughToggled(object sender, RoutedEventArgs e)
    {
        _overlay.ClickThrough = ClickThroughToggle.IsChecked == true;
    }

    private void OnPipelineStatus(object? sender, PipelineStatus status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = status.Message;
            SetIndicator(status.Kind);
            StartButton.IsEnabled = status.Kind != PipelineStatusKind.Capturing;
            StopButton.IsEnabled = status.Kind == PipelineStatusKind.Capturing;
        });
    }

    private void OnLatencyUpdated(object? sender, TimeSpan latency)
    {
        Dispatcher.InvokeAsync(() => LatencyText.Text = $"{latency.TotalMilliseconds:0} ms");
    }

    private void SetIndicator(PipelineStatusKind kind)
    {
        CaptureIndicator.Foreground = kind switch
        {
            PipelineStatusKind.Capturing => Brushes.LimeGreen,
            PipelineStatusKind.Error => Brushes.IndianRed,
            _ => Brushes.Gray,
        };
    }
}
