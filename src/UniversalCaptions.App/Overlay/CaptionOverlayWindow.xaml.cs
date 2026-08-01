using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UniversalCaptions.Core.Captions;

namespace UniversalCaptions.App.Overlay;

/// <summary>
/// The always-on-top caption overlay styled after Google Chrome's Live Caption panel: a dark
/// semi-transparent rectangle with a header row showing the session language badge, a single
/// scrolling text area that renders history and the active in-progress line as a continuous
/// paragraph (newest text always at the bottom), and a collapse/expand chevron at the footer.
/// Implements <see cref="IOverlayService"/> so the control window can configure appearance and
/// placement without touching caption state (ADR-0004).
/// </summary>
public partial class CaptionOverlayWindow : Window, IOverlayService
{
    private readonly ICaptionService _captions;
    private readonly string _sourceLanguage;
    private readonly EventHandler<CaptionLine> _lineChangedHandler;
    private readonly EventHandler<CaptionState> _stateChangedHandler;

    private double _opacity = 0.9;
    private double _fontSize = 20;
    private bool _clickThrough;
    private bool _expanded = true;
    private bool _renderQueued;
    private bool _positioned;
    private bool _bottomAnchored = true;

    /// <summary>
    /// Creates the overlay. Caption state events are consumed on the dispatcher.
    /// </summary>
    /// <param name="captions">The caption service whose state this overlay renders.</param>
    /// <param name="options">Caption service options — used to read the source language for the
    /// language badge header.</param>
    public CaptionOverlayWindow(ICaptionService captions, CaptionServiceOptions options)
    {
        _captions = captions ?? throw new ArgumentNullException(nameof(captions));
        ArgumentNullException.ThrowIfNull(options);
        _sourceLanguage = options.SourceLanguage.ToUpperInvariant();

        _lineChangedHandler = (_, _) => OnCaptionChanged();
        _stateChangedHandler = (_, _) => OnCaptionChanged();
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        // Subscribe once here rather than on Loaded: Loaded re-fires after every Hide/Show cycle,
        // which would otherwise accumulate duplicate subscriptions.
        _captions.ActiveLineChanged += _lineChangedHandler;
        _captions.CaptionLineCommitted += _lineChangedHandler;
        _captions.CaptionLineUpdated += _lineChangedHandler;
        _captions.StateChanged += _stateChangedHandler;
        ApplyAppearance();
        ApplyExpandedState();
    }

    bool IOverlayService.IsVisible => IsVisible;

    double IOverlayService.Opacity
    {
        get => _opacity;
        set
        {
            _opacity = Math.Clamp(value, 0.2, 1.0);
            ApplyAppearance();
        }
    }

    double IOverlayService.FontSize
    {
        get => _fontSize;
        set
        {
            _fontSize = Math.Clamp(value, 10, 96);
            ApplyAppearance();
        }
    }

    bool IOverlayService.ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value)
            {
                return;
            }

            _clickThrough = value;
            ApplyClickThrough();
        }
    }

    void IOverlayService.Show() => Show();

    void IOverlayService.Hide() => Hide();

    void IOverlayService.ShowAt(double left, double top)
    {
        Left = left;
        Top = top;
        _positioned = true;
        _bottomAnchored = false;
        Show();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureDefaultPosition();
        ApplyClickThrough();
        Render();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _captions.ActiveLineChanged -= _lineChangedHandler;
        _captions.CaptionLineCommitted -= _lineChangedHandler;
        _captions.CaptionLineUpdated -= _lineChangedHandler;
        _captions.StateChanged -= _stateChangedHandler;
    }

    private void OnCaptionChanged()
    {
        if (_renderQueued)
        {
            return;
        }

        _renderQueued = true;
        Dispatcher.BeginInvoke(Render);
    }

    private void Render()
    {
        _renderQueued = false;
        // Render from a synchronized snapshot: caption events fire outside the caption service's
        // internal lock, so a live read of _captions.State could race a concurrent commit.
        CaptionSnapshot snapshot = _captions.GetSnapshot();
        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(snapshot);

        // When translation is enabled and the active line is still being translated, do not update
        // the text block at all: keep the previously shown translated caption so the overlay never
        // flashes a blank or shows the source language between live translations.
        bool shouldUpdate = model.ActiveLine is not null
            || !model.TranslationEnabled
            || snapshot.ActiveLine is null;

        if (shouldUpdate)
        {
            // Combine committed history + active line into a single continuous paragraph so the
            // overlay scrolls naturally, mirroring Chrome's Live Caption behaviour.
            var sb = new StringBuilder();
            foreach (var line in model.History)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(line.Text);
            }

            if (model.ActiveLine is { } active)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(active.Text);
            }

            string combined = sb.ToString();
            bool hasContent = !string.IsNullOrWhiteSpace(combined);

            CaptionTextBlock.Text = hasContent ? combined : string.Empty;
            CaptionTextBlock.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
            HintText.Visibility = model.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        // Language badge header: show source→target pills when translation is active.
        if (model.TranslationEnabled && model.LanguageBadge is not null)
        {
            SourceLanguageBadge.Text = _sourceLanguage;
            TargetLanguageBadge.Text = model.LanguageBadge;
            TranslationBadgePanel.Visibility = Visibility.Visible;
        }
        else
        {
            TranslationBadgePanel.Visibility = Visibility.Collapsed;
        }

        // Auto-scroll to the bottom so the newest line is always visible.
        CaptionScroller.ScrollToBottom();

        ScheduleBottomAnchor();
    }

    private void OnCollapseToggled(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        ApplyExpandedState();
        ScheduleBottomAnchor();
    }

    private readonly LinearGradientBrush _topFadeMask = new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(0, 1),
        GradientStops = new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.0),
            new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.12),
            new GradientStop(Color.FromArgb(255, 0, 0, 0), 1.0),
        }
    };

    private void ApplyExpandedState()
    {
        if (CaptionScroller is null || CollapseChevron is null)
        {
            return;
        }

        if (_expanded)
        {
            // Expanded: fixed-height box (~5-6 lines), matching Chrome Live Caption.
            CaptionScroller.Height = 160;
            CaptionScroller.OpacityMask = _topFadeMask;
            CollapseChevron.Text = "\uE70E"; // chevron up — click will collapse
            CollapseButton.ToolTip = "Collapse";
        }
        else
        {
            // Collapsed: show 2 full lines dynamically sized to font height + padding.
            double lineHeight = _fontSize * 1.4;
            CaptionScroller.Height = Math.Max(56, lineHeight * 2 + 10);
            CaptionScroller.OpacityMask = null;
            CollapseChevron.Text = "\uE70D"; // chevron down — click will expand
            CollapseButton.ToolTip = "Expand";
        }

        // After resizing, scroll to the bottom so the newest caption is visible in either state.
        CaptionScroller.ScrollToBottom();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Hide();

    private void OnChromeMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        // Interactive elements own their clicks; only the caption surface drags the window.
        if (IsInsideInteractiveElement(e.OriginalSource))
        {
            return;
        }

        _bottomAnchored = false;
        DragMove();
    }

    /// <summary>
    /// Queues a bottom-edge re-anchor after the pending layout pass so the window stays pinned to
    /// the bottom of the screen (Chrome-style) and grows upward as captions wrap, keeping the
    /// current caption visible at the bottom edge. Skipped once the user has dragged the window.
    /// </summary>
    private void ScheduleBottomAnchor() =>
        Dispatcher.BeginInvoke(AnchorToBottom, DispatcherPriority.Loaded);

    private void AnchorToBottom()
    {
        if (!_bottomAnchored || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        Left = Math.Max(0, (SystemParameters.VirtualScreenWidth - ActualWidth) / 2);
        Top = Math.Max(0, SystemParameters.VirtualScreenHeight - ActualHeight - 48);
    }

    /// <summary>True when the pressed element is one of the overlay's interactive controls.</summary>
    private bool IsInsideInteractiveElement(object? source)
    {
        for (DependencyObject? node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, CloseButton) || ReferenceEquals(node, CollapseButton))
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureDefaultPosition()
    {
        if (_positioned)
        {
            return;
        }

        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : Height;
        Left = (SystemParameters.VirtualScreenWidth - width) / 2;
        Top = SystemParameters.VirtualScreenHeight - height - 48;
        _positioned = true;
    }

    private void ApplyAppearance()
    {
        if (CaptionScroller is null)
        {
            return;
        }

        OverlayChrome.Opacity = _opacity;
        CaptionTextBlock.FontSize = _fontSize;
        CaptionTextBlock.LineHeight = _fontSize * 1.4;
        HintText.FontSize = Math.Max(12, _fontSize * 0.6);
        SourceLanguageBadge.FontSize = Math.Max(10, _fontSize * 0.45);
        TargetLanguageBadge.FontSize = Math.Max(10, _fontSize * 0.45);
        ApplyExpandedState();
    }

    private void ApplyClickThrough()
    {
        if (!IsLoaded)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        int exStyle = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
        if (_clickThrough)
        {
            exStyle |= (int)NativeMethods.WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle &= ~(int)NativeMethods.WS_EX_TRANSPARENT;
        }

        NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE, exStyle);
        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
    }
}
