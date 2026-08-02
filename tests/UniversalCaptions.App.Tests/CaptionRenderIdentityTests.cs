using System.Reflection;
using System.Windows.Controls;
using UniversalCaptions.App.Overlay;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Speech;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Slice 7 render-path tests: proves the overlay's <c>UpdateCaptionItems</c>/<c>ReconcileHistory</c>
/// reuse TextBlock instances by identity so that (A) a Partial never rebuilds history blocks and
/// only mutates the active line's text, and a Final finalizes the active block and appends a fresh
/// block above it. Runs on a dedicated STA thread because WPF element construction requires STA.
/// </summary>
public class CaptionRenderIdentityTests
{

    /// <summary>A stub caption service used only to satisfy the overlay constructor. </summary>
    private sealed class NoopCaptionService : ICaptionService
    {
        public event EventHandler<CaptionLine>? ActiveLineChanged { add { } remove { } }
        public event EventHandler<CaptionLine>? CaptionLineCommitted { add { } remove { } }
        public event EventHandler<CaptionLine>? CaptionLineUpdated { add { } remove { } }
        public event EventHandler<CaptionState>? StateChanged { add { } remove { } }

        public CaptionState State => throw new NotSupportedException();
        public bool IsRunning => false;

        public CaptionSnapshot GetSnapshot() => throw new NotSupportedException();
        public void Start() { }
        public void Stop() { }
        public void Reset() { }
        public void SetTranslationEnabled(bool enabled, string? targetLanguage = null) { }
        public void ProcessPartial(PartialTranscript transcript) { }
        public void ProcessFinal(FinalTranscript transcript) { }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static CaptionDisplayLine D(string text, long seq, bool translated = false) =>
        new(text, seq, translated);

    private static CaptionDisplayModel Model(params CaptionDisplayLine[] lines) =>
        new(
            ActiveLine: null,
            History: lines);

    /// <summary>Builds a real overlay window on the test's STA thread.</summary>
    private static CaptionDisplayCaller CreateOverlay()
    {
        var overlay = new CaptionOverlayWindow(new NoopCaptionService(), new CaptionServiceOptions("en"));
        return new CaptionDisplayCaller(overlay);
    }

    [Fact]
    public void Partial_updates_do_not_rebuild_history_blocks()
    {
        RunOnSta(() =>
        {
            var caller = CreateOverlay();
            // Two committed lines are present before the partial stream begins.
            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("he", 99),
                History: new[] { D("hello world", 1), D("how are you", 2) }));

            TextBlock[] before = caller.HistoryBlocks().ToArray();

            // A newer partial for the same active utterance only rewrites the active line's text.
            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("he", 99),
                History: new[] { D("hello world", 1), D("how are you", 2) }));

            // History instances are bit-for-bit unchanged.
            var after = caller.HistoryBlocks();
            Assert.Equal(before.Length, after.Count);
            for (int i = 0; i < before.Length; i++)
            {
                Assert.Same(before[i], after[i]);
            }
            // Active text updated in place.
            Assert.Equal("he", caller.ActiveText());
        });
    }

    [Fact]
    public void Partial_with_growing_text_updates_active_in_place()
    {
        RunOnSta(() =>
        {
            var caller = CreateOverlay();
            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("the quick", 0),
                History: Array.Empty<CaptionDisplayLine>()));

            TextBlock activeBefore = caller.ActiveBlock()!;

            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("the quick brown", 0),
                History: Array.Empty<CaptionDisplayLine>()));

            // The same active TextBlock instance is reused; only its text changed.
            Assert.Same(activeBefore, caller.ActiveBlock());
            Assert.Equal("the quick brown", caller.ActiveText());
        });
    }

    [Fact]
    public void Final_finalizes_active_and_appends_a_fresh_block()
    {
        RunOnSta(() =>
        {
            var caller = CreateOverlay();
            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("the quick brown fox", 1),
                History: Array.Empty<CaptionDisplayLine>()));
            TextBlock? originalActive = caller.ActiveBlock();

            // Commit: active text finalizes into history (as a fresh block); the live active TextBlock is
            // reused in place and now carries the next partial's text — history is never rebuilt.
            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("is now speaking", 2),
                History: new[] { D("the quick brown fox", 1) }));

            // The finalized prior active is now the sole history entry (fresh block), while the
            // single live TextBlock is reused and its text rewritten to the next partial.
            Assert.Single(caller.HistoryBlocks());
            Assert.Equal("the quick brown fox", caller.HistoryBlocks()[0].Text);
            Assert.Same(originalActive, caller.ActiveBlock());
            Assert.Equal("is now speaking", caller.ActiveText());
        });
    }

    [Fact]
    public void Finalized_blocks_keep_their_own_text_instances_and_order()
    {
        RunOnSta(() =>
        {
            var caller = CreateOverlay();
            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("third", 3),
                History: new[] { D("first", 1), D("second", 2) }));
            TextBlock first = caller.HistoryBlocks()[0];
            TextBlock second = caller.HistoryBlocks()[1];

            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("fourth", 4),
                History: new[] { D("first", 1), D("second", 2), D("third", 3) }));

            Assert.Same(first, caller.HistoryBlocks()[0]);
            Assert.Same(second, caller.HistoryBlocks()[1]);
            Assert.Equal(3, caller.HistoryBlocks().Count);
            Assert.Equal(new[] { "first", "second", "third" }, caller.HistoryTexts());
        });
    }

    /// <summary>Thin reflection wrapper around the overlay's private render seams.</summary>
    private sealed class CaptionDisplayCaller
    {
        private readonly object _overlay;
        private readonly MethodInfo _update;
        private readonly FieldInfo _history;
        private readonly FieldInfo _active;

        public CaptionDisplayCaller(CaptionOverlayWindow overlay)
        {
            _overlay = overlay;
            _update = typeof(CaptionOverlayWindow).GetMethod("UpdateCaptionItems",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            _history = typeof(CaptionOverlayWindow).GetField("_historyBlocks",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            _active = typeof(CaptionOverlayWindow).GetField("_activeBlock",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
        }

        public void Update(CaptionDisplayModel model) => _update.Invoke(_overlay, new object[] { model });

        public IList<TextBlock> HistoryBlocks() => ((List<TextBlock>)_history.GetValue(_overlay)!).ToList();

        public TextBlock? ActiveBlock() => (TextBlock?)_active.GetValue(_overlay);

        public string? ActiveText() => ActiveBlock()?.Text;

        public IList<string> HistoryTexts() => HistoryBlocks().Select(b => b.Text).ToList();
    }

    /// <summary>Runs an action on a dedicated STA thread (WPF elements require it).</summary>
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }
}
