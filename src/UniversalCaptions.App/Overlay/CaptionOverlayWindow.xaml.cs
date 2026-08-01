using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using UniversalCaptions.Core.Captions;

namespace UniversalCaptions.App.Overlay;

/// <summary>
/// The always-on-top caption overlay. Borderless and transparent; renders the caption service's
/// state (active line + bounded history, newest first) using <see cref="CaptionDisplayPolicy"/>.
/// Implements <see cref="IOverlayService"/> so the control window can configure appearance and
/// placement without touching caption state (ADR-0004).
/// </summary>
public partial class CaptionOverlayWindow : Window, IOverlayService
{
    private readonly ICaptionService _captions;
    private readonly EventHandler<CaptionLine> _lineChangedHandler;
    private readonly EventHandler<CaptionState> _stateChangedHandler;

    private double _opacity = 0.9;
    private double _fontSize = 24;
    private bool _clickThrough;
    private bool _renderQueued;
    private bool _resizing;
    private bool _positioned;

    /// <summary>
    /// Creates the overlay. Caption state events are consumed on the dispatcher.
    /// </summary>
    /// <param name="captions">The caption service whose state this overlay renders.</param>
    public CaptionOverlayWindow(ICaptionService captions)
    {
        _captions = captions ?? throw new ArgumentNullException(nameof(captions));
        _lineChangedHandler = (_, _) => OnCaptionChanged();
        _stateChangedHandler = (_, _) => OnCaptionChanged();
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        ApplyAppearance();
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

    void IOverlayService.Show()
    {
        EnsureDefaultPosition();
        Show();
    }

    void IOverlayService.Hide() => Hide();

    void IOverlayService.ShowAt(double left, double top)
    {
        Left = left;
        Top = top;
        _positioned = true;
        Show();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureDefaultPosition();
        _captions.ActiveLineChanged += _lineChangedHandler;
        _captions.CaptionLineCommitted += _lineChangedHandler;
        _captions.CaptionLineUpdated += _lineChangedHandler;
        _captions.StateChanged += _stateChangedHandler;
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
        CaptionDisplayModel model = CaptionDisplayPolicy.ToDisplayModel(_captions.GetSnapshot());
        ActiveCaption.Text = model.ActiveLine?.Text ?? string.Empty;
        HistoryList.ItemsSource = model.History;
        HintText.Visibility = model.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnChromeMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_resizing || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (IsInsideGrip(e.OriginalSource))
        {
            // The resize grip owns the drag: let it resize the window instead of moving it.
            return;
        }

        DragMove();
    }

    /// <summary>True when the pressed element is the resize grip or one of its template parts.</summary>
    private bool IsInsideGrip(object? source)
    {
        for (DependencyObject? node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ResizeGrip))
            {
                return true;
            }
        }

        return false;
    }

    private void OnResizeGripDragStarted(object sender, DragStartedEventArgs e) => _resizing = true;

    private void OnResizeGripDragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void OnResizeGripDragCompleted(object sender, DragCompletedEventArgs e) => _resizing = false;

    private void EnsureDefaultPosition()
    {
        if (_positioned)
        {
            return;
        }

        Left = (SystemParameters.VirtualScreenWidth - Width) / 2;
        Top = SystemParameters.VirtualScreenHeight - Height - 48;
        _positioned = true;
    }

    private void ApplyAppearance()
    {
        if (OverlayRoot is null)
        {
            return;
        }

        OverlayChrome.Opacity = _opacity;
        TextElement.SetFontSize(OverlayRoot, _fontSize);
        ActiveCaption.FontSize = _fontSize * 1.25;
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
