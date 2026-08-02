using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UniversalCaptions.Core.Captions;

namespace UniversalCaptions.App.Overlay;

/// <summary>
/// The always-on-top caption overlay styled after Google Chrome's Live Caption panel: a dark
/// semi-transparent rectangle with a header row showing the session language badge, a fixed-height
/// scrolling text area that renders each committed FINAL caption as its own stable item (newest text
/// always at the bottom), and a collapse/expand chevron at the footer. The live/partial active line
/// is deliberately not painted, so the overlay shows only stable translated FINAL captions. Implements
/// <see cref="IOverlayService"/> so the control window can configure appearance and placement without
/// touching caption state (ADR-0004).
/// </summary>
public partial class CaptionOverlayWindow : Window, IOverlayService
{
    private readonly ICaptionService _captions;
    private readonly string _sourceLanguage;
    private readonly EventHandler<CaptionLine> _lineChangedHandler;
    private readonly EventHandler<CaptionState> _stateChangedHandler;

    private double _opacity = 1.0;
    private double _fontSize = 16;
    private bool _clickThrough;
    private bool _expanded = true;
    private bool _renderQueued;
    private bool _positioned;
    private bool _bottomAnchored = true;

    // Stable visual items: one TextBlock per committed FINAL caption line, kept in display order and
    // reused by sequence so a new forwarded caption never rebuilds the caption visual tree. The
    // live in-progress line is never painted (_activeBlock stays null), so partials do not churn the
    // panel — the overlay is committed-FINAL-only.
    private readonly List<TextBlock> _historyBlocks = new();
    private TextBlock? _activeBlock;

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

        // The overlay is committed-FINAL-only: the live/partial active line is never rendered. While
        // the active line is in flight we hold the existing history on screen (no re-render), so the
        // overlay never churns on ASR partials, never flashes a blank, and never shows the source
        // language. Reconciliation happens when a NEW FINAL is available (snapshot.ActiveLine is null).
        bool shouldUpdate = model.ActiveLine is not null
            || !model.TranslationEnabled
            || snapshot.ActiveLine is null;

        bool newBlockAdded = false;
        if (shouldUpdate)
        {
            // Returns true only when a brand-new caption block (a committed Final) was inserted —
            // the one event that legitimately requires a bottom scroll. Live active-line mutations
            // are never painted, so they never touch the block list and never force a scroll.
            newBlockAdded = UpdateCaptionItems(model);
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

        // Auto-scroll to the newest caption only when a block was actually added (a Final commits or
        // the first line appears), and only when the content really overflows the fixed-height
        // viewport. A Partial that only rewrites the live line's text never scrolls and never causes
        // the caption area to reflow. Scrolling is never used to paper over a re-render problem.
        if (newBlockAdded)
        {
            ScrollToBottomIfNeeded();
        }
        // No per-render bottom re-anchor here: the overlay's height is fixed (fixed-height caption
        // scroll area + reserved hover chrome), so a caption render never changes the window size.
        // Re-anchoring happens only on Loaded and on the collapse/hover toggles (see those methods).
    }

    /// <summary>
    /// Reconciles the caption panel against the display model by mutating only the items that
    /// changed: committed history lines are reused by sequence (a new final simply appends a fresh
    /// block above the live line) and only the live in-progress line's text is rewritten in place.
    /// Existing finalized items keep their TextBlock instance and are never rebuilt. A Partial that
    /// only rewrites the active line's text returns false; only a freshly inserted block (a Final or
    /// the first ever line) returns true, so the caller knows a bottom scroll is warranted.
    /// </summary>
    private bool UpdateCaptionItems(CaptionDisplayModel model)
    {
        bool newBlockAdded = ReconcileHistory(model.History);

        // The overlay displays committed FINAL translated captions only. The live/partial active line
        // is intentionally never painted, so the display is stable instead of churning on every ASR
        // partial. Remove any block that may already exist (e.g. from an earlier full-window render).
        if (_activeBlock is not null)
        {
            CaptionPanel.Children.Remove(_activeBlock);
            _activeBlock = null;
        }

        bool hasContent = model.History.Count > 0;
        CaptionPanel.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
        HintText.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
        return newBlockAdded;
    }

    /// <summary>
    /// Reconciles the committed-history region of the panel against the display history, reusing
    /// TextBlock instances by sequence. Blocks that already match are left untouched; new finals get
    /// a fresh block inserted in chronological position; a block whose text changed (e.g. a completed
    /// translation replaces source on an already-visible line) is updated in place; stale blocks are
    /// removed. The overlay never paints a live active line, so the history blocks are the only children.
    /// </summary>
    private bool ReconcileHistory(IReadOnlyList<CaptionDisplayLine> history)
    {
        bool newBlockAdded = false;
        int insertIndex = 0;
        foreach (CaptionDisplayLine line in history)
        {
            int existingIndex = FindHistoryIndex(line.Sequence);
            if (existingIndex >= 0)
            {
                TextBlock block = _historyBlocks[existingIndex];
                if (existingIndex != insertIndex)
                {
                    _historyBlocks.RemoveAt(existingIndex);
                    CaptionPanel.Children.Remove(block);
                    _historyBlocks.Insert(insertIndex, block);
                    CaptionPanel.Children.Insert(insertIndex, block);
                }

                if (!string.Equals(block.Text, line.Text, StringComparison.Ordinal))
                {
                    block.Text = line.Text;
                }

                insertIndex++;
            }
            else
            {
                TextBlock block = CreateCaptionBlock(line.Text, line.Sequence);
                _historyBlocks.Insert(insertIndex, block);
                CaptionPanel.Children.Insert(insertIndex, block);
                newBlockAdded = true;
                insertIndex++;
            }
        }

        while (_historyBlocks.Count > history.Count)
        {
            TextBlock stale = _historyBlocks[^1];
            _historyBlocks.RemoveAt(_historyBlocks.Count - 1);
            CaptionPanel.Children.Remove(stale);
        }

        return newBlockAdded;
    }

    private int FindHistoryIndex(long sequence)
    {
        for (int i = 0; i < _historyBlocks.Count; i++)
        {
            if (_historyBlocks[i].Tag is long tag && tag == sequence)
            {
                return i;
            }
        }

        return -1;
    }

    private TextBlock CreateCaptionBlock(string text, long sequence)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = _fontSize,
            LineHeight = _fontSize * 1.4,
            Tag = sequence,
        };
    }

    /// <summary>
    /// Advances the scroll position only when the content overflows the fixed-height viewport, so
    /// the newest caption remains visible. When everything fits, no scroll pass runs at all.
    /// </summary>
    private void ScrollToBottomIfNeeded()
    {
        if (CaptionScroller.ScrollableHeight > 0)
        {
            CaptionScroller.ScrollToBottom();
        }
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

    /// <summary>
    /// Copies the overlay's currently displayed caption text to the clipboard (debug aid). The text
    /// is gathered from the rendered caption blocks in display order, newest last.
    /// </summary>
    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        foreach (object child in CaptionPanel.Children)
        {
            if (child is TextBlock block && !string.IsNullOrWhiteSpace(block.Text))
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.Append(block.Text);
            }
        }

        if (sb.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard is occasionally busy (held by another app); silently skip the copy.
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Hide();

    /// <summary>
    /// Reveals the header bar and collapse chevron on hover (Chrome Live Caption behaviour): by
    /// default only the caption strip is visible; the nav controls appear when the mouse is over the
    /// overlay and hide again when it leaves.
    /// </summary>
    private void OnChromeMouseEnter(object sender, MouseEventArgs e) => SetChromeVisible(true);

    private void OnChromeMouseLeave(object sender, MouseEventArgs e) => SetChromeVisible(false);

    /// <summary>
    /// Shows/hides the header bar and chevron. Both must use <see cref="Visibility.Hidden"/> (not
    /// <see cref="Visibility.Collapsed"/>) while hidden so their layout space is always reserved:
    /// because the window is <c>SizeToContent="Height"</c>, collapsing the rows to zero would change
    /// the overlay's height on hover/leave. Using Hidden keeps the panel a constant size whether or
    /// not it is hovered.
    /// </summary>
    private void SetChromeVisible(bool visible)
    {
        HeaderBar.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        CollapseButton.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        // Re-pin to the bottom edge when anchored so it grows upward like Chrome instead of shifting.
        ScheduleBottomAnchor();
    }

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
        foreach (TextBlock block in _historyBlocks)
        {
            block.FontSize = _fontSize;
            block.LineHeight = _fontSize * 1.4;
        }

        if (_activeBlock is not null)
        {
            _activeBlock.FontSize = _fontSize;
            _activeBlock.LineHeight = _fontSize * 1.4;
        }

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
