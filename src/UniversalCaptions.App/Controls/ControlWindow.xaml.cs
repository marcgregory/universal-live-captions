using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UniversalCaptions.App.Overlay;
using UniversalCaptions.App.Pipeline;
using UniversalCaptions.App.Settings;
using UniversalCaptions.Audio.Capture;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Translation;

namespace UniversalCaptions.App.Controls;

/// <summary>
/// The minimal control window: selects the audio source and speech language, toggles translation
/// and its target, starts/stops captions, shows status and latency, and applies overlay appearance
/// settings (FR-8/FR-9/FR-10/FR-14). It only calls the Core contracts and the pipeline; WPF event
/// handlers marshal pipeline events onto the dispatcher. Persisted settings (TD-005) are applied on
/// load and saved on change.
/// </summary>
public partial class ControlWindow : Window
{
    private readonly CaptionPipeline _pipeline;
    private readonly IOverlayService _overlay;
    private readonly ICaptionService _captions;
    private readonly ArgosTranslationEngine _translationEngine;
    private readonly string _captionSourceLanguage;
    private readonly ISettingsStore _settingsStore;
    private readonly UserSettings _settings;
    private readonly ICredentialStore _credentialStore;

    private bool _initializing = true;
    private bool _savePending;

    private sealed record LanguageOption(string Label, string? Code);

    private sealed record ProviderOption(string Label, TranslationProvider Value);

    private static readonly ProviderOption[] TranslationProviders =
    [
        new("Argos (offline)", TranslationProvider.Argos),
        new("Gemini (cloud)", TranslationProvider.Gemini),
    ];

    private const string GeminiKeyTarget = "UniversalCaptions:GeminiApiKey";

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
    /// <param name="settingsStore">The settings store this window saves its user preferences to (TD-005).</param>
    /// <param name="settings">The persisted user settings applied to the controls on load (TD-005).</param>
    /// <param name="credentialStore">
    /// The Windows Credential Manager seam used by the Gemini API-key flow (ADR-0009).
    /// </param>
    public ControlWindow(CaptionPipeline pipeline, IOverlayService overlay, ICaptionService captions, CaptionServiceOptions captionOptions, ArgosTranslationEngine translationEngine, ISettingsStore settingsStore, UserSettings settings, ICredentialStore credentialStore)
    {
        _pipeline = pipeline;
        _overlay = overlay;
        _captions = captions;
        _translationEngine = translationEngine ?? throw new ArgumentNullException(nameof(translationEngine));
        _captionSourceLanguage = (captionOptions ?? throw new ArgumentNullException(nameof(captionOptions))).SourceLanguage;
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        InitializeComponent();

        Loaded += OnLoaded;
        Closed += OnClosed;
        _pipeline.StatusChanged += OnPipelineStatus;
        _pipeline.LatencyUpdated += OnLatencyUpdated;
        _pipeline.EndToEndLatencyUpdated += OnEndToEndLatencyUpdated;
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

            // Startup pre-warm: OnLoaded sets the toggle above while _initializing, so
            // OnTranslationToggled returns early and would otherwise never fire the Argos pre-warm
            // until the user toggles translation again. With translation persisted enabled, boot the
            // engine in the background now so the first real caption reuses a warm process instead
            // of paying the cold Argos import + lazy model-load inline (the ~19 s first-caption).
            // The pre-warm is skipped when Gemini owns translation: Argos must not start at all in a
            // Gemini session (TranslationProviderPolicy).
            if (translationEnabled && TranslationProviderPolicy.UsesCaptionLineTranslation(_settings.Provider))
            {
                string? target = (TargetLanguageCombo.SelectedItem as LanguageOption)?.Code;
                if (!string.IsNullOrWhiteSpace(target))
                {
                    _ = PreheatInBackgroundAsync(target!);
                }
            }

            ProviderCombo.ItemsSource = TranslationProviders;
            ProviderCombo.SelectedIndex = FindProviderIndex(_settings.Provider ?? TranslationProvider.Argos);
            UpdateGeminiKeyPanelState();

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

    private static int FindProviderIndex(TranslationProvider value)
    {
        for (int i = 0; i < TranslationProviders.Length; i++)
        {
            if (TranslationProviders[i].Value == value)
            {
                return i;
            }
        }
        return 0;
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
        // Flush any pending coalesced save so the user's final state survives shutdown (TD-005).
        _settingsStore.Save(ReadCurrentSettings());
    }

    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        string? deviceId = (AudioSourceCombo.SelectedItem as LoopbackDevice)?.Id;
        string? language = (LanguageCombo.SelectedItem as LanguageOption)?.Code;

        // Reset the caption service to clear previous session's history/text from the overlay
        _captions.Reset();
        // Re-apply the translation settings (as Reset disables them by default)
        ApplyTranslationSettings();

        // The live-translation provider + languages for this session. The provider was previously
        // dead-ended in UserSettings (saved but never read by the engine factory); it now flows to
        // the pipeline so the UI selection actually constructs the Gemini engine. Translation off →
        // no provider → no live engine. Argos → UsesLiveAudioEngine is false, so no live engine is
        // requested and the offline caption-line path handles translation.
        TranslationProvider? selectedProvider = (ProviderCombo.SelectedItem as ProviderOption)?.Value;
        TranslationProvider? liveProvider = TranslationToggle.IsChecked == true
            ? TranslationProviderPolicy.UsesLiveAudioEngine(selectedProvider) ? selectedProvider : null
            : null;
        string? source = (LanguageCombo.SelectedItem as LanguageOption)?.Code;
        string? target = (TargetLanguageCombo.SelectedItem as LanguageOption)?.Code;

        _pipeline.Start(deviceId, language, liveProvider, source, target);
        _overlay.Show();
    }

    private void OnShowCaptionsClicked(object sender, RoutedEventArgs e) => _overlay.Show();

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
        TranslationProvider? provider = (ProviderCombo.SelectedItem as ProviderOption)?.Value;

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

        // The common translation state/UX layer: the Translate checkbox + target dropdown behave the
        // same for every provider. TranslationEnabled/TargetLanguage reflect the user's intent; only
        // the translation MECHANISM below is provider-specific.
        _captions.SetTranslationEnabled(enabled, target);
        _captions.SetCaptionLineTranslation(TranslationProviderPolicy.UsesCaptionLineTranslation(provider));

        // Kick the Argos pre-warm in the background so the cold-start is not paid on the first real
        // caption. Fire-and-forget on a background task: the UI stays responsive, and the engine's
        // shared warm-up task is awaited by real translations if the user starts speaking first.
        // Only the Argos caption-line path needs Argos — Gemini owns translation (TranslationProviderPolicy).
        if (enabled && TranslationProviderPolicy.UsesCaptionLineTranslation(provider) && !string.IsNullOrWhiteSpace(target))
        {
            _ = PreheatInBackgroundAsync(target!);
        }

        // Runtime reconfiguration: when a session is live, toggling translation on/off or changing the
        // target must take effect immediately (Argos UI/UX parity). The pipeline owns the live engine —
        // a null provider (translation off) stops it so captions return to source; Gemini is recreated
        // with the new target (the target is part of the engine's session setup). No-op when stopped.
        TranslationProvider? liveProvider = enabled && TranslationProviderPolicy.UsesLiveAudioEngine(provider) ? provider : null;
        string? source = (LanguageCombo.SelectedItem as LanguageOption)?.Code;
        _pipeline.SetLiveTranslation(liveProvider, source, target);
    }

    /// <summary>
    /// Starts the Argos pre-warm off the UI thread. The engine shares one initialization with real
    /// translations and swallows/report errors, so a pre-warm failure is never user-visible and the
    /// lazy translation path remains the fallback.
    /// </summary>
    private async Task PreheatInBackgroundAsync(string target)
    {
        try
        {
            await _translationEngine.TriggerPreWarmAsync(_captionSourceLanguage, target).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            System.Diagnostics.Trace.WriteLine($"[UniversalCaptions] Argos pre-warm background error: {exc}");
        }
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
            Provider = (ProviderCombo.SelectedItem as ProviderOption)?.Value,
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
            StartButton.IsEnabled = status.Kind != PipelineStatusKind.Capturing;
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
    /// Reacts to a change in the translation provider combo. Does NOT mutate any active Gemini
    /// session's credential — the new provider applies to the next Start (ADR-0009 §Trade-offs).
    /// </summary>
    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateGeminiKeyPanelState();
        SaveSettings();
    }

    /// <summary>
    /// Reflects the current provider + credential presence in the Gemini key panel's controls and
    /// status text. Called from <see cref="OnLoaded"/>, <see cref="OnProviderChanged"/>, and the
    /// Add/Update/Remove handlers. Pure UI refresh — does not read or write the credential itself.
    /// </summary>
    private void UpdateGeminiKeyPanelState()
    {
        bool isGemini = (ProviderCombo.SelectedItem as ProviderOption)?.Value == TranslationProvider.Gemini;
        bool hasKey = _credentialStore.HasCredential(GeminiKeyTarget);

        GeminiKeyPanel.IsEnabled = isGemini;
        if (!isGemini)
        {
            GeminiKeyStatusText.Text = "Not applicable";
            AddGeminiKeyButton.Visibility = Visibility.Collapsed;
            UpdateGeminiKeyButton.Visibility = Visibility.Collapsed;
            RemoveGeminiKeyButton.Visibility = Visibility.Collapsed;
            GeminiKeyLastUpdatedText.Text = string.Empty;
            return;
        }

        GeminiKeyStatusText.Text = hasKey ? "Configured" : "Not configured";
        AddGeminiKeyButton.Visibility = hasKey ? Visibility.Collapsed : Visibility.Visible;
        UpdateGeminiKeyButton.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
        RemoveGeminiKeyButton.IsEnabled = hasKey;
        // The "last updated" timestamp is intentionally not shown — recording it would require
        // persisting it (which contradicts the "minimum persistence" policy) or querying the
        // Credential Manager's FILETIME (which is not surfaced via CredRead+CredFree here).
        GeminiKeyLastUpdatedText.Text = string.Empty;
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
        instructions.Text = "Paste your Gemini API key. It is stored in Windows Credential Manager and read only when you start a Gemini session. The key is never displayed back to you.";
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
            UpdateGeminiKeyPanelState();
        }
    }

    private void OnRemoveGeminiKeyClicked(object sender, RoutedEventArgs e)
    {
        MessageBoxResult confirm = MessageBox.Show(
            this,
            "Remove the Gemini API key from Windows Credential Manager? Active Gemini sessions are not affected (the next Start will re-read).",
            "Remove Gemini API key",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _credentialStore.RemoveCredential(GeminiKeyTarget);
        UpdateGeminiKeyPanelState();
    }
}
