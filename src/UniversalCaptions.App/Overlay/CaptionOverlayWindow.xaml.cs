using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UniversalCaptions.App.Settings;
using UniversalCaptions.Core.Captions;

namespace UniversalCaptions.App.Overlay;

/// <summary>
/// The always-on-top caption overlay styled after Google Chrome's Live Caption panel: a dark
/// semi-transparent rectangle with a header row showing the session language badge, a fixed-height
/// scrolling text area, and a collapse/expand chevron at the footer. The text area renders the
/// live/partial active line as a single mutable block at the bottom (rewritten in place on each
/// partial, never rebuilt) above the committed FINAL history — each final freezes the active line
/// into a stable history block. Implements <see cref="IOverlayService"/> so the control window can
/// configure appearance and placement without touching caption state (ADR-0004).
/// </summary>
public partial class CaptionOverlayWindow : Window, IOverlayService
{
    private readonly ICaptionService _captions;
    private string _sourceLanguage;
    private readonly ISettingsStore _settingsStore;
    private readonly UserSettings _settings;
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
    // live in-progress line is a single mutable TextBlock (_activeBlock) that partials rewrite in
    // place and a Final freezes into history — partials never churn the committed blocks.
    private readonly List<TextBlock> _historyBlocks = new();
    private TextBlock? _activeBlock;

    /// <summary>
    /// The subtle cyan used for the unstable tail of the live partial line (v0.5.38): words
    /// the engine has not yet confirmed are tinted cyan (matching the landing-page accent #67e8f9)
    /// until the next partial re-recognizes them or a FINAL freezes the line into history. Kept
    /// deliberately muted so the stable white head stays the visual dominant. Frozen so any thread
    /// can read it (tests run each case on its own STA thread).
    /// </summary>
    private static readonly Brush PartialUnstableBrush = CreatePartialUnstableBrush();

    private static Brush CreatePartialUnstableBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x67, 0xE8, 0xF9));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Creates the overlay. Caption state events are consumed on the dispatcher. Persisted settings
    /// (TD-005) seed appearance, placement, and view state on load.
    /// </summary>
    /// <param name="captions">The caption service whose state this overlay renders.</param>
    /// <param name="options">Caption service options — used to read the source language for the
    /// language badge header.</param>
    /// <param name="settingsStore">The settings store this overlay saves placement + view state to (TD-005).</param>
    /// <param name="settings">The persisted user settings applied to appearance/placement on load (TD-005).</param>
    public CaptionOverlayWindow(ICaptionService captions, CaptionServiceOptions options, ISettingsStore settingsStore, UserSettings settings)
    {
        _captions = captions ?? throw new ArgumentNullException(nameof(captions));
        ArgumentNullException.ThrowIfNull(options);
        _sourceLanguage = options.SourceLanguage.ToUpperInvariant();
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _opacity = Math.Clamp(_settings.Opacity ?? 1.0, 0.2, 1.0);
        _fontSize = Math.Clamp(_settings.FontSize ?? 16, 10, 96);
        _clickThrough = _settings.ClickThrough == true;
        _expanded = _settings.OverlayExpanded != false;

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

    void IOverlayService.SetSourceLanguage(string? sourceLanguage)
    {
        _sourceLanguage = string.IsNullOrWhiteSpace(sourceLanguage) ? "AUTO" : sourceLanguage.Trim().ToUpperInvariant();
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
        // Restore an explicitly saved placement; otherwise use the adaptive bottom-anchored default.
        if (_settings.OverlayLeft is double savedLeft && _settings.OverlayTop is double savedTop)
        {
            Left = savedLeft;
            Top = savedTop;
            _positioned = true;
            _bottomAnchored = false;
        }
        else
        {
            EnsureDefaultPosition();
        }

        ApplyClickThrough();
        Render();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _captions.ActiveLineChanged -= _lineChangedHandler;
        _captions.CaptionLineCommitted -= _lineChangedHandler;
        _captions.CaptionLineUpdated -= _lineChangedHandler;
        _captions.StateChanged -= _stateChangedHandler;
        SaveOverlayState();
    }

    /// <summary>
    /// Persists the overlay placement + view state (TD-005) by merging into the currently persisted
    /// settings, so the control-window-owned categories (device/language/translation/appearance) are
    /// preserved. Placement is stored only once the user explicitly positioned the overlay (dragged),
    /// so a never-dragged overlay keeps the adaptive bottom-anchored default placement. Safe to call
    /// from the <c>Closed</c> handler (a closed window still reports its last <c>Left</c>/<c>Top</c>),
    /// which flushes any unsaved placement at app exit.
    /// </summary>
    private void SaveOverlayState()
    {
        UserSettings current = _settingsStore.Load();
        UserSettings merged = current with { OverlayExpanded = _expanded };
        if (!_bottomAnchored)
        {
            merged = merged with { OverlayLeft = Left, OverlayTop = Top };
        }

        _settingsStore.Save(merged);
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

        // Render whenever there is something new to paint: a visible live active line (a partial, or
        // its completed live translation), translation disabled (the active line is always shown), or
        // a FINAL that just committed (snapshot.ActiveLine is null — the active line froze into
        // history). While translation is enabled and a partial is still being translated the display
        // model hides the active line, so we hold the existing captions on screen (no re-render, no
        // source-language flash) until the translation completes or the FINAL commits.
        bool shouldUpdate = model.ActiveLine is not null
            || !model.TranslationEnabled
            || snapshot.ActiveLine is null;

        bool newBlockAdded = false;
        if (shouldUpdate)
        {
            // Returns true only when a brand-new caption block was inserted (the live active line
            // first appears, or a committed Final freezes into history) — the events that
            // legitimately warrant a bottom scroll. In-place active-line text rewrites never insert
            // a block and never force a scroll.
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
    /// block) and the live in-progress line's text is rewritten in place on the single mutable block.
    /// Existing finalized items keep their TextBlock instance and are never rebuilt; a Partial never
    /// inserts or removes a history block. Returns true only when a brand-new block was inserted (a
    /// Final freeze or the active line's first appearance), so the caller knows a bottom scroll is
    /// warranted.
    /// </summary>
    private bool UpdateCaptionItems(CaptionDisplayModel model)
    {
        bool newBlockAdded = ReconcileHistory(model.History);

        // Paint the live in-progress line as a single mutable block at the bottom of the panel. A
        // partial rewrites that block's text in place (identity preserved — no rebuild); a null
        // active line (committed, stopped, or hidden while its translation is pending) removes it.
        if (model.ActiveLine is { } active)
        {
            if (_activeBlock is null)
            {
                _activeBlock = CreateActiveCaptionBlock(active.Text, active.Sequence);
                CaptionPanel.Children.Add(_activeBlock);
                newBlockAdded = true;
            }
            else if (!string.Equals(GetBlockText(_activeBlock), active.Text, StringComparison.Ordinal))
            {
                // v0.5.38: re-paint the live block with the stable/unstable word split against the
                // immediately-previous partial text. The whole block instance is still rewritten in
                // place — only its Inlines change, never its identity.
                PaintActiveCaptionBlock(_activeBlock, active.Text);
            }
        }
        else if (_activeBlock is not null)
        {
            CaptionPanel.Children.Remove(_activeBlock);
            _activeBlock = null;
        }

        bool hasContent = model.History.Count > 0 || _activeBlock is not null;
        CaptionPanel.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
        HintText.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
        return newBlockAdded;
    }

    /// <summary>
    /// Reconciles the committed-history region of the panel against the display history, reusing
    /// TextBlock instances by sequence. Blocks that already match are left untouched; new finals get
    /// a fresh block inserted in chronological position; a block whose text changed (e.g. a completed
    /// translation replaces source on an already-visible line) is updated in place; stale blocks are
    /// removed. History blocks are the committed lines only — the live active line is a separate
    /// block handled by <see cref="UpdateCaptionItems"/> and never appears here.
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
    /// Creates the live active-line block. v0.5.38: painted with the stable/unstable two-tone split
    /// (never plain Text) so a partial instantly communicates which words are confirmed. The first
    /// partial of an utterance has no previous partial, so it paints entirely in the unstable tint.
    /// </summary>
    private TextBlock CreateActiveCaptionBlock(string text, long sequence)
    {
        TextBlock block = CreateCaptionBlock(string.Empty, sequence);
        PaintActiveCaptionBlock(block, text);
        return block;
    }

    /// <summary>
    /// Rewrites the live active block's Inlines in place with the v0.5.38 two-tone split: the stable
    /// word prefix (common with the immediately-previous partial) stays white/normal, and the
    /// unstable tail paints in the subtle partial tint. The block instance is preserved — only its
    /// Inlines change, never its identity.
    /// </summary>
    private void PaintActiveCaptionBlock(TextBlock block, string text)
    {
        string previous = GetBlockText(block);
        int stableWordCount = CaptionPartialStability.StableWordCount(previous, text);
        (string stable, string unstable) = CaptionPartialStability.SplitAtWord(text, stableWordCount);

        block.Inlines.Clear();
        if (stable.Length > 0)
        {
            block.Inlines.Add(new Run(stable));
        }

        if (unstable.Length > 0)
        {
            block.Inlines.Add(new Run(unstable) { Foreground = PartialUnstableBrush });
        }
    }

    /// <summary>
    /// Reads a caption block's display text whether it stores plain Text (history blocks) or
    /// two-tone Inlines (the live active block).
    /// </summary>
    private static string GetBlockText(TextBlock block)
    {
        if (block.Inlines.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            foreach (Inline inline in block.Inlines)
            {
                if (inline is Run run)
                {
                    sb.Append(run.Text);
                }
            }

            return sb.ToString();
        }

        return block.Text;
    }

    /// <summary>
    /// Advances the scroll position only when the content overflows the fixed-height viewport, so
    /// the newest caption remains visible. When everything fits, no scroll pass runs at all.
    /// </summary>
    private void ScrollToBottomIfNeeded()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (CaptionScroller.ScrollableHeight > 0)
            {
                CaptionScroller.ScrollToBottom();
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnCollapseToggled(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        ApplyExpandedState();
        ScheduleBottomAnchor();
        SaveOverlayState();
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
            CaptionScroller.Height = 200;
            // Keep the first line fully readable; the old top fade made text look clipped.
            CaptionScroller.OpacityMask = null;
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

        // Scroll after the layout pass so wrapped lines and the bottom padding are measured first.
        Dispatcher.BeginInvoke(CaptionScroller.ScrollToBottom, DispatcherPriority.Loaded);
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
            if (child is TextBlock block)
            {
                string blockText = GetBlockText(block);
                if (string.IsNullOrWhiteSpace(blockText))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.Append(blockText);
            }
        }

        if (sb.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            _ = FlashCopyFeedbackAsync();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard is occasionally busy (held by another app); silently skip the copy.
        }
    }

    /// <summary>
    /// Gives visual feedback that the copy succeeded: the glyph swaps for a green checkmark and the
    /// tooltip becomes "Copied!" for a moment, then everything is restored.
    /// </summary>
    private async Task FlashCopyFeedbackAsync()
    {
        string originalGlyph = CopyGlyph.Text;
        Brush originalForeground = CopyGlyph.Foreground;
        object originalTooltip = CopyButton.ToolTip;

        CopyGlyph.Text = "\u2713"; // ✓
        CopyGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0xDD, 0x8B));
        CopyButton.ToolTip = "Copied!";
        try
        {
            await Task.Delay(1200);
        }
        finally
        {
            CopyGlyph.Text = originalGlyph;
            CopyGlyph.Foreground = originalForeground;
            CopyButton.ToolTip = originalTooltip;
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
        SaveOverlayState();
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
