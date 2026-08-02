using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Diagnostic layout probe for Slice 7: verifies that each caption TextBlock in the overlay is
/// actually measured at (and wraps at) the full available viewport width, rather than at an
/// unexpectedly narrow width that would force premature wraps. The probe recreates the exact
/// ScrollViewer → Grid → StackPanel → TextBlock tree used by CaptionOverlayWindow.xaml so it measures
/// the live layout behaviour WPF will actually produce. WPF layout requires an STA thread, so each
/// assertion runs on a dedicated STA dispatcher.
/// </summary>
public class CaptionLayoutProbeTests
{
    // Real overlay sizing (CaptionOverlayWindow.xaml):
    //   Window Width=560, OverlayChrome BorderThickness=1
    //   CaptionScroller Grid.Row=1, Margin=16,4,16,4
    //   CaptionPanel StackPanel Margin=0,0,4,0
    private const double WindowWidth = 560;
    private const double ScrollerViewportWidth = WindowWidth - 2 - 32;  // 552 - border(2) - horizontal margins(32)
    private static readonly Thickness StackPanelMargin = new(0, 0, 4, 0);
    private const double FontSize = 20;
    private const double LineHeight = FontSize * 1.4;
    private const double ExpectedTextWidth = ScrollerViewportWidth - 4;

    /// <summary>
    /// Builds a TextBlock and measures it inside the overlay's exact tree on a dedicated STA thread.
    /// Returns realized (width, height), the stack panel's available width, and the wrapped line
    /// count. TextBlock creation and layout both happen on the STA thread, as WPF requires.
    /// </summary>
    private static (double, double, int) MeasureOn(string text)
    {
        double width = 0, height = 0;
        int lines = 0;
        RunOnSta(() =>
        {
            // Create + measure on the STA thread (WPF requires it).
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = FontSize,
                LineHeight = LineHeight,
            };
            // Recreate the exact tree: ScrollViewer(viewport) -> Grid -> StackPanel(margin) -> TextBlock.
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Width = ScrollerViewportWidth,
            };
            var grid = new Grid();
            var panel = new StackPanel { Margin = StackPanelMargin };
            panel.Children.Add(block);
            grid.Children.Add(panel);
            scroller.Content = grid;

            scroller.Measure(new Size(scroller.Width, 10000));
            scroller.Arrange(new Rect(0, 0, scroller.Width, 10000));
            scroller.UpdateLayout();

            width = block.RenderSize.Width;
            height = block.RenderSize.Height;
            lines = (int)Math.Round(height / LineHeight, MidpointRounding.AwayFromZero);
        });
        return (width, height, lines);
    }

    [Fact]
    public void Short_line_is_full_width_and_stays_one_line()
    {
        (double width, _, int lines) = MeasureOn("two words");

        // A wrapped TextBlock must stretch to the full viewport width even for a short utterance,
        // and stay on one line; otherwise appending a longer tail downstream would force a premature
        // new visual line ("two words jump a line").
        Assert.InRange(width, ExpectedTextWidth - 1, ExpectedTextWidth + 1);
        Assert.Equal(1, lines);
    }

    [Fact]
    public void Long_sentence_wraps_only_when_viewport_width_exhausted()
    {
        (double width, _, int lines) = MeasureOn(
            "the quick brown fox jumps over the lazy dog near the river bank while the sun is slowly "
            + "setting behind the distant hills tonight");

        // Full width is used; wrapping onto a second line is caused purely by exhausting 522px.
        Assert.InRange(width, ExpectedTextWidth - 1, ExpectedTextWidth + 1);
        Assert.True(lines >= 2, $"Expected >= 2 wrapped lines, got {lines}.");
    }

    /// <summary>Growing tails must keep the same full width so text wraps instead of shrieking.</summary>
    [Fact]
    public void Width_usage_is_constant_across_growing_tails()
    {
        (double w1, _, int l1) = MeasureOn("the quick");
        (double w2, _, int l2) = MeasureOn("the quick brown fox jumps over the lazy dog today today today today today");

        // Same realized width regardless of tail length, and only more wrapped lines, never a
        // narrower revisit — this is what keeps the newest caption visually stable.
        Assert.InRange(w1, ExpectedTextWidth - 1, ExpectedTextWidth + 1);
        Assert.InRange(w2, ExpectedTextWidth - 1, ExpectedTextWidth + 1);
        Assert.True(l2 > l1, $"Longer tail wrapped to {l2} lines but shorter to {l1}.");
    }

    /// <summary>Creates a new STA thread dispatcher to run a layout pass.</summary>
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Pump the dispatcher if the action queued work; here actions are synchronous, so just join.
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }
}
