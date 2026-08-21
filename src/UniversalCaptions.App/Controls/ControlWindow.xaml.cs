using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UniversalCaptions.App.Overlay;
using UniversalCaptions.App.Pipeline;
using UniversalCaptions.App.Settings;
using UniversalCaptions.Audio.Capture;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.App.Controls;

/// <summary>
/// The minimal control window: selects the audio source and speech language, toggles translation
/// and its target, starts/stops captions, shows status and latency, and applies overlay appearance
/// settings (FR-8/FR-9/FR-10/FR-14). It only calls the Core contracts and the pipeline; WPF event
/// handlers marshal pipeline events onto the dispatcher. Persisted settings (TD-005) are applied on
/// load and saved on change. ADR-0011: Gemini is the only speech engine — there is no provider
/// dropdown; the Gemini API-key panel is the primary gate.
/// </summary>
public partial class ControlWindow : Window
{
    private readonly CaptionPipeline _pipeline;
    private readonly IOverlayService _overlay;
    private readonly ICaptionService _captions;
    private readonly string _captionSourceLanguage;
    private readonly ISettingsStore _settingsStore;
    private readonly UserSettings _settings;
    private readonly ICredentialStore _credentialStore;
    private readonly GeminiAvailabilityEvaluator _geminiEvaluator;

    private bool _initializing = true;
    private bool _savePending;
    private bool _isStarting;
    private bool _isStopping;
    private GeminiAvailability _geminiAvailability = GeminiAvailability.Unknown;

    private sealed record LanguageOption(string Label, string? Code, bool IsEnabled = true);

    private const string GeminiKeyTarget = "UniversalCaptions:GeminiApiKey";

    private static readonly LanguageOption[] SourceLanguages =
    [
        new("Auto (detect)", null),
        new("Afrikaans (af)", "af"),
        new("Akan (ak)", "ak"),
        new("Albanian (sq)", "sq"),
        new("Amharic (am)", "am"),
        new("Arabic (ar)", "ar"),
        new("Armenian (hy)", "hy"),
        new("Assamese (as)", "as"),
        new("Azerbaijani (az)", "az"),
        new("Basque (eu)", "eu"),
        new("Belarusian (be)", "be"),
        new("Bengali (bn)", "bn"),
        new("Bosnian (bs)", "bs"),
        new("Bulgarian (bg)", "bg"),
        new("Burmese (my)", "my"),
        new("Catalan (ca)", "ca"),
        new("Cebuano (ceb)", "ceb"),
        new("Chinese (zh)", "zh"),
        new("Croatian (hr)", "hr"),
        new("Czech (cs)", "cs"),
        new("Danish (da)", "da"),
        new("Dutch (nl)", "nl"),
        new("English (en)", "en"),
        new("Estonian (et)", "et"),
        new("Faroese (fo)", "fo"),
        new("Filipino / Tagalog (fil)", "fil"),
        new("Finnish (fi)", "fi"),
        new("French (fr)", "fr"),
        new("Galician (gl)", "gl"),
        new("Georgian (ka)", "ka"),
        new("German (de)", "de"),
        new("Greek (el)", "el"),
        new("Gujarati (gu)", "gu"),
        new("Hausa (ha)", "ha"),
        new("Hebrew (iw)", "iw"),
        new("Hindi (hi)", "hi"),
        new("Hungarian (hu)", "hu"),
        new("Icelandic (is)", "is"),
        new("Indonesian (id)", "id"),
        new("Italian (it)", "it"),
        new("Japanese (ja)", "ja"),
        new("Javanese (jv)", "jv"),
        new("Kannada (kn)", "kn"),
        new("Kazakh (kk)", "kk"),
        new("Khmer (km)", "km"),
        new("Korean (ko)", "ko"),
        new("Lao (lo)", "lo"),
        new("Latvian (lv)", "lv"),
        new("Lithuanian (lt)", "lt"),
        new("Macedonian (mk)", "mk"),
        new("Malay (ms)", "ms"),
        new("Malayalam (ml)", "ml"),
        new("Maltese (mt)", "mt"),
        new("Maori (mi)", "mi"),
        new("Marathi (mr)", "mr"),
        new("Mongolian (mn)", "mn"),
        new("Nepali (ne)", "ne"),
        new("Norwegian (no)", "no"),
        new("Odia (or)", "or"),
        new("Oromo (om)", "om"),
        new("Pashto (ps)", "ps"),
        new("Persian (fa)", "fa"),
        new("Polish (pl)", "pl"),
        new("Portuguese (pt)", "pt"),
        new("Punjabi (pa)", "pa"),
        new("Quechua (qu)", "qu"),
        new("Romanian (ro)", "ro"),
        new("Romansh (rm)", "rm"),
        new("Russian (ru)", "ru"),
        new("Serbian (sr)", "sr"),
        new("Sindhi (sd)", "sd"),
        new("Sinhala (si)", "si"),
        new("Slovak (sk)", "sk"),
        new("Slovenian (sl)", "sl"),
        new("Somali (so)", "so"),
        new("Southern Sotho (st)", "st"),
        new("Spanish (es)", "es"),
        new("Swahili (sw)", "sw"),
        new("Swedish (sv)", "sv"),
        new("Tajik (tg)", "tg"),
        new("Tamil (ta)", "ta"),
        new("Telugu (te)", "te"),
        new("Thai (th)", "th"),
        new("Turkish (tr)", "tr"),
        new("Ukrainian (uk)", "uk"),
        new("Urdu (ur)", "ur"),
        new("Uzbek (uz)", "uz"),
        new("Vietnamese (vi)", "vi"),
    ];

    /// <summary>
    /// Target languages exposed for live translation, matching Google's official Gemini Live
    /// Translate supported-language table (verified 2026-08-15 at
    /// https://ai.google.dev/gemini-api/docs/live-api/live-translate). Every code is a BCP-47 tag
    /// accepted verbatim by the API.
    /// </summary>
    private static readonly LanguageOption[] TargetLanguages =
    [
        new("Afrikaans (af)", "af"),
        new("Akan (ak)", "ak"),
        new("Albanian (sq)", "sq"),
        new("Amharic (am)", "am"),
        new("Arabic (ar)", "ar"),
        new("Armenian (hy)", "hy"),
        new("Azerbaijani (az)", "az"),
        new("Basque (eu)", "eu"),
        new("Belarusian (be)", "be"),
        new("Bengali (bn)", "bn"),
        new("Bulgarian (bg)", "bg"),
        new("Burmese (Myanmar) (my)", "my"),
        new("Catalan (ca)", "ca"),
        new("Chinese (Simplified) (zh-Hans)", "zh-Hans"),
        new("Chinese (Traditional) (zh-Hant)", "zh-Hant"),
        new("Croatian (hr)", "hr"),
        new("Czech (cs)", "cs"),
        new("Danish (da)", "da"),
        new("Dutch (nl)", "nl"),
        new("English (en)", "en"),
        new("Estonian (et)", "et"),
        new("Filipino / Tagalog (fil)", "fil"),
        new("Finnish (fi)", "fi"),
        new("French (fr)", "fr"),
        new("Galician (gl)", "gl"),
        new("Georgian (ka)", "ka"),
        new("German (de)", "de"),
        new("Greek (el)", "el"),
        new("Gujarati (gu)", "gu"),
        new("Hausa (ha)", "ha"),
        new("Hebrew (he)", "he"),
        new("Hindi (hi)", "hi"),
        new("Hungarian (hu)", "hu"),
        new("Icelandic (is)", "is"),
        new("Indonesian (id)", "id"),
        new("Italian (it)", "it"),
        new("Japanese (ja)", "ja"),
        new("Javanese (jv)", "jv"),
        new("Kannada (kn)", "kn"),
        new("Kazakh (kk)", "kk"),
        new("Khmer (km)", "km"),
        new("Kinyarwanda (rw)", "rw"),
        new("Korean (ko)", "ko"),
        new("Lao (lo)", "lo"),
        new("Latvian (lv)", "lv"),
        new("Lithuanian (lt)", "lt"),
        new("Macedonian (mk)", "mk"),
        new("Malay (ms)", "ms"),
        new("Malayalam (ml)", "ml"),
        new("Marathi (mr)", "mr"),
        new("Mongolian (mn)", "mn"),
        new("Nepali (ne)", "ne"),
        new("Norwegian (no)", "no"),
        new("Persian (fa)", "fa"),
        new("Polish (pl)", "pl"),
        new("Portuguese (Brazil) (pt-BR)", "pt-BR"),
        new("Portuguese (Portugal) (pt-PT)", "pt-PT"),
        new("Punjabi (pa)", "pa"),
        new("Romanian (ro)", "ro"),
        new("Russian (ru)", "ru"),
        new("Serbian (sr)", "sr"),
        new("Sindhi (sd)", "sd"),
        new("Sinhala (si)", "si"),
        new("Slovak (sk)", "sk"),
        new("Slovenian (sl)", "sl"),
        new("Spanish (es)", "es"),
        new("Sundanese (su)", "su"),
        new("Swahili (sw)", "sw"),
        new("Swedish (sv)", "sv"),
        new("Tamil (ta)", "ta"),
        new("Telugu (te)", "te"),
        new("Thai (th)", "th"),
        new("Turkish (tr)", "tr"),
        new("Ukrainian (uk)", "uk"),
        new("Urdu (ur)", "ur"),
        new("Uzbek (uz)", "uz"),
        new("Vietnamese (vi)", "vi"),
        new("Zulu (zu)", "zu"),
    ];

    /// <summary>
    /// Creates the control window.
    /// </summary>
    /// <param name="pipeline">The caption pipeline this window starts and stops.</param>
    /// <param name="overlay">The overlay this window configures.</param>
    /// <param name="captions">The caption service this window toggles translation on.</param>
    /// <param name="captionOptions">The caption service options, for the caption source language.</param>
    /// <param name="settingsStore">The settings store this window saves its user preferences to (TD-005).</param>
    /// <param name="settings">The persisted user settings applied to the controls on load (TD-005).</param>
    /// <param name="credentialStore">
    /// The Windows Credential Manager seam used by the Gemini API-key flow (ADR-0009).
    /// </param>
    /// <param name="geminiEvaluator">
    /// Evaluates whether the stored Gemini key is usable (present + syntactically valid + live
    /// validation), driving the key panel's status text.
    /// </param>
    public ControlWindow(CaptionPipeline pipeline, IOverlayService overlay, ICaptionService captions, CaptionServiceOptions captionOptions, ISettingsStore settingsStore, UserSettings settings, ICredentialStore credentialStore, GeminiAvailabilityEvaluator geminiEvaluator)
    {
        _pipeline = pipeline;
        _overlay = overlay;
        _captions = captions;
        _captionSourceLanguage = (captionOptions ?? throw new ArgumentNullException(nameof(captionOptions))).SourceLanguage;
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _geminiEvaluator = geminiEvaluator ?? throw new ArgumentNullException(nameof(geminiEvaluator));
        InitializeComponent();

        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        Title = version is null ? "Universal Live Captions" : $"Universal Live Captions v{version.ToString(3)}";

        Loaded += OnLoaded;
        Closed += OnClosed;
        _pipeline.StatusChanged += OnPipelineStatus;
        _pipeline.LatencyUpdated += OnLatencyUpdated;
        _pipeline.EndToEndLatencyUpdated += OnEndToEndLatencyUpdated;
        _pipeline.LiveTranslationErrorUpdated += OnLiveTranslationError;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _initializing = true;
        try
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
                AudioSourceCombo.SelectedIndex = ResolveAudioSourceIndex(result.Devices, result.Preferred);
            }

            LanguageCombo.ItemsSource = SourceLanguages;
            LanguageCombo.SelectedIndex = FindLanguageIndex(_settings.Language);

            bool translationEnabled = _settings.TranslationEnabled == true;
            TargetLanguageCombo.ItemsSource = TargetLanguages;
            TargetLanguageCombo.SelectedIndex = FindTargetIndex(_settings.TargetLanguage);
            TargetLanguageCombo.IsEnabled = translationEnabled;

            TranslationToggle.IsChecked = translationEnabled;

            // The key panel reflects ACTUAL runtime availability: the local syntax gate runs
            // synchronously (no network), then a live validation round-trip refines the status text
            // in the background so an invalid/expired key is flagged as soon as the check completes.
            _geminiAvailability = _geminiEvaluator.Evaluate();
            UpdateGeminiKeyPanelState();
            _ = RefreshGeminiAvailabilityLiveAsync();

            ClickThroughToggle.IsChecked = _settings.ClickThrough == true;

            OpacitySlider.Value = _overlay.Opacity;
            FontSizeSlider.Value = _overlay.FontSize;
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>
    /// Resolves the audio-source combo index: the persisted device when it is still present, otherwise
    /// the current default render device, otherwise the first device.
    /// </summary>
    private int ResolveAudioSourceIndex(IReadOnlyList<LoopbackDevice> devices, LoopbackDevice? preferred)
    {
        if (_settings.DeviceId is string savedId &&
            devices.Any(d => string.Equals(d.Id, savedId, StringComparison.OrdinalIgnoreCase)))
        {
            return FindDeviceIndex(devices, savedId);
        }

        return preferred is null ? 0 : FindDeviceIndex(devices, preferred.Id);
    }

    private static int FindLanguageIndex(string? code)
    {
        code = NormalizeLegacyFilipinoCode(code);
        if (code is null)
        {
            return 0;
        }

        for (int i = 0; i < SourceLanguages.Length; i++)
        {
            if (string.Equals(SourceLanguages[i].Code, code, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private static int FindTargetIndex(string? code)
    {
        code = NormalizeLegacyFilipinoCode(code);
        if (code is null)
        {
            return 0;
        }

        for (int i = 0; i < TargetLanguages.Length; i++)
        {
            if (string.Equals(TargetLanguages[i].Code, code, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private static string? NormalizeLegacyFilipinoCode(string? code)
    {
        return string.Equals(code, "tl", StringComparison.OrdinalIgnoreCase) ? "fil" : code;
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
        _pipeline.EndToEndLatencyUpdated -= OnEndToEndLatencyUpdated;
        _pipeline.LiveTranslationErrorUpdated -= OnLiveTranslationError;
        _ = Task.Run(() => _pipeline.StopAsync());
        // Flush any pending coalesced save so the user's final state survives shutdown (TD-005).
        _settingsStore.Save(ReadCurrentSettings());
    }

    private async void OnStartClicked(object sender, RoutedEventArgs e)
    {
        if (_isStarting || _isStopping)
        {
            return;
        }

        if (_pipeline.IsRunning)
        {
            if (!_pipeline.HasLiveTranslationSession)
            {
                _isStarting = true;
                StatusText.Text = "Reconnecting captions...";
                StartButton.IsEnabled = true;
                StartButton.IsHitTestVisible = false;
                StartButtonText.Text = "Reconnecting captions...";
                StartProgress.Visibility = Visibility.Visible;
                try
                {
                    await _pipeline.RestartLiveTranslationAsync();
                    if (_pipeline.HasLiveTranslationSession)
                    {
                        _overlay.Show();
                    }
                }
                finally
                {
                    _isStarting = false;
                    StartProgress.Visibility = Visibility.Collapsed;
                    StartButtonText.Text = "Start Captions";
                    StartButton.IsHitTestVisible = true;
                    StartButton.IsEnabled = !_pipeline.IsRunning || !_pipeline.HasLiveTranslationSession;
                }
            }

            return;
        }

        string? deviceId = (AudioSourceCombo.SelectedItem as LoopbackDevice)?.Id;
        string? source = (LanguageCombo.SelectedItem as LanguageOption)?.Code;
        string? target = (TargetLanguageCombo.SelectedItem as LanguageOption)?.Code;
        _overlay.SetSourceLanguage(source);
        bool translationEnabled = TranslationToggle.IsChecked == true;

        _isStarting = true;
        // Keep the button visually active while loading; the guard above prevents double-starts.
        StartButton.IsEnabled = true;
        StartButton.IsHitTestVisible = false;
        StartButton.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
        StartButton.Foreground = Brushes.White;
        StartProgress.Foreground = Brushes.White;
        StopButton.IsEnabled = false;
        StartButtonText.Text = "Starting captions...";
        StartProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Starting captions...";
        SetIndicator(PipelineStatusKind.Stopped);

        // Let WPF paint the loading state before the synchronous startup handshake begins.
        await Dispatcher.InvokeAsync(
            () => { },
            System.Windows.Threading.DispatcherPriority.Render);

        try
        {
            // Reset the caption service to clear previous session's history/text from the overlay.
            _captions.Reset();

            // CaptionPipeline performs the Gemini handshake synchronously. Keep that work off the
            // dispatcher so the loading state remains visible and the control window stays responsive.
            await Task.Run(() => _pipeline.Start(deviceId, source, target, translationEnabled));

            if (_pipeline.IsRunning)
            {
                _overlay.Show();
            }
        }
        finally
        {
            _isStarting = false;
            StartProgress.Visibility = Visibility.Collapsed;
            StartButtonText.Text = "Start Captions";
            StartButton.ClearValue(Control.BackgroundProperty);
            StartButton.ClearValue(Control.ForegroundProperty);
            StartProgress.ClearValue(ProgressBar.ForegroundProperty);
            StartButton.IsEnabled = !_pipeline.IsRunning;
            StopButton.IsEnabled = _pipeline.IsRunning;
        }
    }

    private void OnShowCaptionsClicked(object sender, RoutedEventArgs e) => _overlay.Show();

    private async void OnStopClicked(object sender, RoutedEventArgs e)
    {
        await StopPipelineAsync();
    }

    /// <summary>
    /// Completes the pipeline teardown before re-enabling Start. CaptionPipeline.Stop() intentionally
    /// returns before disposal finishes, so restarting from its immediate Stopped event can race the
    /// old audio/Gemini resources.
    /// </summary>
    private async Task StopPipelineAsync()
    {
        if (_isStopping)
        {
            return;
        }

        _isStopping = true;
        StartButton.IsEnabled = false;
        StartButton.IsHitTestVisible = false;
        StopButton.IsEnabled = false;
        StatusText.Text = "Stopping captions...";
        _overlay.Hide();

        try
        {
            await Task.Run(() => _pipeline.StopAsync());
        }
        finally
        {
            _isStopping = false;
            StartButton.IsHitTestVisible = true;
            StartButton.IsEnabled = !_pipeline.IsRunning;
            StopButton.IsEnabled = _pipeline.IsRunning;
        }
    }

    private void OnAudioSourceChanged(object sender, SelectionChangedEventArgs e) => SaveSettings();

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e) => SaveSettings();

    private void OnTranslationToggled(object sender, RoutedEventArgs e)
    {
        TargetLanguageCombo.IsEnabled = TranslationToggle.IsChecked == true;
        if (_initializing)
        {
            return;
        }

        ApplyTranslationSettings();
        SaveSettings();
    }

    private void OnTargetLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_initializing)
        {
            ApplyTranslationSettings();
            SaveSettings();
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

        // The common translation state/UX layer: the Translate checkbox + target dropdown update the
        // caption service's display state immediately (history scrubbing included).
        _captions.SetTranslationEnabled(enabled, target);

        // Runtime reconfiguration: when a session is live, toggling translation suppresses/restores
        // translated lines WITHOUT touching the Gemini session (it is also the transcription source);
        // changing the target recycles the engine (the target is part of the session setup).
        // No-op when stopped — Start applies its own configuration.
        _pipeline.SetTranslationEnabled(enabled);
        _pipeline.SetTargetLanguage(target);
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _overlay.Opacity = e.NewValue;
        SaveSettings();
    }

    private void OnFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _overlay.FontSize = e.NewValue;
        SaveSettings();
    }

    private void OnClickThroughToggled(object sender, RoutedEventArgs e)
    {
        _overlay.ClickThrough = ClickThroughToggle.IsChecked == true;
        SaveSettings();
    }

    /// <summary>
    /// Persists the current control-window state (TD-005). Saves are coalesced onto the dispatcher so
    /// a burst of changes (e.g. dragging the opacity slider) settles to the last UI state with a
    /// single write; the store lock additionally serializes file writes. Initial UI population never
    /// triggers a save.
    /// </summary>
    private void SaveSettings()
    {
        if (_initializing || !IsLoaded || _savePending)
        {
            return;
        }

        _savePending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _savePending = false;
            _settingsStore.Save(ReadCurrentSettings());
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Merges the current control-window state into the persisted settings so the categories owned by
    /// the overlay (placement + view state) are preserved rather than overwritten with defaults.
    /// </summary>
    private UserSettings ReadCurrentSettings()
    {
        UserSettings current = _settingsStore.Load();
        return current with
        {
            DeviceId = (AudioSourceCombo.SelectedItem as LoopbackDevice)?.Id,
            Language = (LanguageCombo.SelectedItem as LanguageOption)?.Code,
            TranslationEnabled = TranslationToggle.IsChecked,
            TargetLanguage = (TargetLanguageCombo.SelectedItem as LanguageOption)?.Code,
            Opacity = OpacitySlider.Value,
            FontSize = FontSizeSlider.Value,
            ClickThrough = ClickThroughToggle.IsChecked,
        };
    }

    private void OnPipelineStatus(object? sender, PipelineStatus status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = status.Message;
            SetIndicator(status.Kind);
            if (status.Kind != PipelineStatusKind.Capturing)
            {
                _overlay.Hide();
            }
            if (!_isStarting && !_isStopping)
            {
                StartButton.IsEnabled = status.Kind != PipelineStatusKind.Capturing;
            }
            StopButton.IsEnabled = status.Kind == PipelineStatusKind.Capturing;
        });
    }

    private void OnLatencyUpdated(object? sender, TimeSpan latency)
    {
        Dispatcher.InvokeAsync(() => LatencyText.Text = $"{latency.TotalMilliseconds:0} ms");
    }

    private TimeSpan? _lastPartialEndToEnd;
    private TimeSpan? _lastFinalEndToEnd;

    private void OnEndToEndLatencyUpdated(object? sender, EndToEndLatencySample sample)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (sample.Kind == EndToEndLatencyKind.Partial)
            {
                _lastPartialEndToEnd = sample.EndToEndLatency;
            }
            else
            {
                _lastFinalEndToEnd = sample.EndToEndLatency;
            }

            EndToEndLatencyText.Text = $"partial: {_lastPartialEndToEnd?.TotalMilliseconds ?? 0:0} ms · final: {_lastFinalEndToEnd?.TotalMilliseconds ?? 0:0} ms";
        });
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

    /// <summary>
    /// Immutable description of what the Gemini key panel should show for a given credential
    /// presence and availability verdict. Kept free of UI controls so the accessibility rules (a
    /// broken key must never lock the user out of fixing it) are unit-testable. The
    /// <paramref name="isGemini"/> parameter is retained for test compatibility; ADR-0011 removed the
    /// provider dropdown, so the App always passes <c>true</c> — Gemini is the only speech engine.
    /// </summary>
    internal sealed record GeminiKeyPanelState(
        bool IsEnabled,
        string StatusText,
        bool ShowAdd,
        bool ShowUpdate,
        bool ShowRemove,
        bool RemoveEnabled,
        string LastUpdatedText);

    /// <summary>
    /// Computes the Gemini key panel state. The panel must stay reachable whenever the key needs
    /// attention — otherwise a malformed/invalid key permanently locks the user out of fixing it:
    /// captions cannot start without a usable key, and the panel (holding Add/Update/Remove) would
    /// be disabled forever.
    /// </summary>
    internal static GeminiKeyPanelState ComputeGeminiKeyPanelState(
        bool isGemini, bool hasKey, GeminiAvailability availability)
    {
        bool keyNeedsAttention = availability == GeminiAvailability.MissingKey
            || availability == GeminiAvailability.MalformedKey
            || availability == GeminiAvailability.InvalidKey;

        bool isEnabled = isGemini || hasKey || keyNeedsAttention;

        if (!isGemini && !keyNeedsAttention)
        {
            return new GeminiKeyPanelState(
                isEnabled,
                "Not applicable",
                ShowAdd: false,
                ShowUpdate: false,
                ShowRemove: false,
                RemoveEnabled: false,
                LastUpdatedText: string.Empty);
        }

        bool usable = availability == GeminiAvailability.Available;
        return new GeminiKeyPanelState(
            isEnabled,
            DescribeGeminiAvailability(availability),
            ShowAdd: !hasKey,
            ShowUpdate: hasKey,
            ShowRemove: true,
            RemoveEnabled: hasKey,
            LastUpdatedText: usable && hasKey ? "✓ verified" : string.Empty);
    }

    private void UpdateGeminiKeyPanelState()
    {
        bool hasKey = _credentialStore.HasCredential(GeminiKeyTarget);
        GeminiKeyPanelState state = ComputeGeminiKeyPanelState(isGemini: true, hasKey, _geminiAvailability);

        GeminiKeyPanel.IsEnabled = state.IsEnabled;
        GeminiKeyStatusText.Text = state.StatusText;
        AddGeminiKeyButton.Visibility = state.ShowAdd ? Visibility.Visible : Visibility.Collapsed;
        UpdateGeminiKeyButton.Visibility = state.ShowUpdate ? Visibility.Visible : Visibility.Collapsed;
        RemoveGeminiKeyButton.Visibility = state.ShowRemove ? Visibility.Visible : Visibility.Collapsed;
        RemoveGeminiKeyButton.IsEnabled = state.RemoveEnabled;
        // The "last updated" timestamp is intentionally not shown — recording it would require
        // persisting it (which contradicts the "minimum persistence" policy) or querying the
        // Credential Manager's FILETIME (which is not surfaced via CredRead+CredFree here).
        GeminiKeyLastUpdatedText.Text = state.LastUpdatedText;
    }

    /// <summary>
    /// Human-readable Gemini availability for the key-panel status text. The string is the concise
    /// summary; the actionable guidance lives in the status messages.
    /// </summary>
    private static string DescribeGeminiAvailability(GeminiAvailability availability)
    {
        return availability switch
        {
            GeminiAvailability.Available => "Configured",
            GeminiAvailability.MissingKey => "No key stored",
            GeminiAvailability.MalformedKey => "Key looks invalid",
            GeminiAvailability.InvalidKey => "Key rejected — update",
            GeminiAvailability.QuotaExceeded => "Quota exceeded",
            GeminiAvailability.NetworkError => "Check pending",
            _ => "Checking…",
        };
    }

    /// <summary>
    /// Runs the authoritative live Gemini availability check in the background and applies the
    /// result to the key panel. Off-thread on purpose: the network round-trip must never block the
    /// UI thread. The result is marshalled onto the dispatcher before any UI mutation.
    /// </summary>
    private async Task RefreshGeminiAvailabilityLiveAsync()
    {
        GeminiAvailability availability;
        try
        {
            availability = await _geminiEvaluator.EvaluateLiveAsync().ConfigureAwait(false);
        }
        catch
        {
            availability = GeminiAvailability.NetworkError;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (_initializing)
            {
                return;
            }

            _geminiAvailability = availability;
            UpdateGeminiKeyPanelState();
        });
    }

    /// <summary>
    /// Handles a classified live-translation failure from the pipeline (invalid key, quota, network,
    /// server). The status line gets an actionable message; a definitive key problem additionally
    /// triggers the availability re-evaluation so the key panel reflects the server's verdict.
    /// </summary>
    private void OnLiveTranslationError(object? sender, LiveTranslationError error)
    {
        Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = DescribeLiveTranslationError(error);
            SetIndicator(PipelineStatusKind.Error);

            bool definitiveKeyProblem = error.Kind == LiveTranslationErrorKind.SessionRejected
                || error.Message.Contains("API key", StringComparison.OrdinalIgnoreCase);
            if (definitiveKeyProblem)
            {
                // Re-run the authoritative check so the key panel reflects the server's verdict
                // (missing vs malformed vs invalid key) rather than guessing from the message.
                _ = RefreshGeminiAvailabilityLiveAsync();
            }
        });
    }

    /// <summary>
    /// Actionable user message for a classified live-translation failure.
    /// </summary>
    internal static string DescribeLiveTranslationError(LiveTranslationError error)
    {
        return error.Kind switch
        {
            LiveTranslationErrorKind.SessionRejected =>
                "Gemini is unavailable: " + error.Message + " Update the key in the Control Window.",
            LiveTranslationErrorKind.QuotaExceeded =>
                "Gemini quota/rate limit reached. Wait and retry.",
            LiveTranslationErrorKind.Timeout =>
                "Gemini timed out. Check your connection and restart.",
            LiveTranslationErrorKind.SessionEnded =>
                "Gemini session ended. Restart captions to resume.",
            _ =>
                "Gemini is unavailable: " + error.Message + " Check your connection and restart.",
        };
    }

    private void OnAddGeminiKeyClicked(object sender, RoutedEventArgs e) => PromptForGeminiKey();

    private void OnUpdateGeminiKeyClicked(object sender, RoutedEventArgs e) => PromptForGeminiKey();

    /// <summary>
    /// Opens Google AI Studio's API-key page in the default browser. The link label carries the
    /// " ↗ " suffix so the user knows they are leaving the app for Google's site.
    /// </summary>
    private void OnGetGeminiKeyLinkRequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exc)
        {
            MessageBox.Show(this, $"Could not open the browser: {exc.Message}", "Gemini API key", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    /// <summary>
    /// Opens a modal dialog containing a <see cref="PasswordBox"/>. The user enters (or re-enters)
    /// the Gemini API key and clicks Save. The value is passed to <see cref="ICredentialStore"/>
    /// via <see cref="SecureString"/>-backed WPF memory; the local string reference is dropped
    /// immediately after the store call returns. Never logs, never displays, never persists
    /// outside the Credential Manager (ADR-0009).
    /// </summary>
    private void PromptForGeminiKey()
    {
        bool hasExisting = _credentialStore.HasCredential(GeminiKeyTarget);
        string title = hasExisting ? "Update Gemini API key" : "Add Gemini API key";

        Window dialog = new()
        {
            Title = title,
            Owner = this,
            Width = 460,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };

        StackPanel panel = new() { Margin = new Thickness(14) };
        TextBlock instructions = new()
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        instructions.Text = "Paste your Gemini API key. It is stored in Windows Credential Manager and read only when you start a caption session. The key is never displayed back to you.";
        panel.Children.Add(instructions);

        PasswordBox passwordBox = new() { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(passwordBox);

        TextBlock errorText = new()
        {
            Foreground = Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8),
        };
        panel.Children.Add(errorText);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Button saveButton = new()
        {
            Content = "Save",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };
        Button cancelButton = new()
        {
            Content = "Cancel",
            Padding = new Thickness(14, 6, 14, 6),
            IsCancel = true,
        };
        buttons.Children.Add(saveButton);
        buttons.Children.Add(cancelButton);
        panel.Children.Add(buttons);

        dialog.Content = panel;

        bool? dialogResult = null;
        saveButton.Click += (_, _) =>
        {
            string value = passwordBox.Password;
            if (string.IsNullOrWhiteSpace(value))
            {
                errorText.Text = "Please enter a non-empty key.";
                errorText.Visibility = Visibility.Visible;
                return;
            }
            bool ok = _credentialStore.SetCredential(GeminiKeyTarget, value);
            // Drop the local reference. Per ADR-0009: minimum lifetime + minimum copies; we do not
            // claim cryptographic erasure of every managed-string copy.
            value = string.Empty;
            passwordBox.Password = string.Empty;
            if (!ok)
            {
                errorText.Text = "Could not save the credential. Check that Windows Credential Manager is available.";
                errorText.Visibility = Visibility.Visible;
                return;
            }
            dialogResult = true;
            dialog.Close();
        };
        cancelButton.Click += (_, _) =>
        {
            passwordBox.Password = string.Empty;
            dialogResult = false;
            dialog.Close();
        };

        dialog.ShowDialog();

        if (dialogResult == true)
        {
            OnGeminiCredentialChanged();
        }
    }

    /// <summary>
    /// Re-evaluates Gemini availability after the credential changed (added / updated / removed) and
    /// refreshes the key panel. Running sessions are unaffected (the next Start re-reads the key).
    /// </summary>
    private void OnGeminiCredentialChanged()
    {
        _geminiAvailability = _geminiEvaluator.Evaluate();
        UpdateGeminiKeyPanelState();
        _ = RefreshGeminiAvailabilityLiveAsync();
    }

    private void OnRemoveGeminiKeyClicked(object sender, RoutedEventArgs e)
    {
        MessageBoxResult confirm = MessageBox.Show(
            this,
            "Remove the Gemini API key from Windows Credential Manager? Active sessions are not affected (the next Start will re-read).",
            "Remove Gemini API key",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _credentialStore.RemoveCredential(GeminiKeyTarget);
        OnGeminiCredentialChanged();
    }
}
