using System.IO;
using System.Reflection;
using System.Windows.Controls;
using UniversalCaptions.App.Overlay;
using UniversalCaptions.App.Settings;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Speech;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Slice 7 render-path tests (updated for the Entry 15 overlay integration): proves the overlay's
/// <c>UpdateCaptionItems</c>/<c>ReconcileHistory</c> reuse TextBlock instances by identity, paint the
/// live/partial active line as a single mutable block that partials rewrite in place, and freeze a
/// Final into history while removing the active block — so partials never churn the caption panel and
/// never leak into the committed history. Runs on a dedicated STA thread because WPF element
/// construction requires STA.
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
        public void SetCaptionLineTranslation(bool enabled) { }
        public void ProcessPartial(PartialTranscript transcript) { }
        public void ProcessFinal(FinalTranscript transcript) { }
        public void ProcessPartialTranslation(UniversalCaptions.Core.Translation.PartialTranslation translation) { }
        public void ProcessFinalTranslation(UniversalCaptions.Core.Translation.FinalTranslation translation) { }
        public void ClearLiveTranslationActiveLine() { }
        public void ClearTranslationHistory() { }
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
        var overlay = new CaptionOverlayWindow(
            new NoopCaptionService(),
            new CaptionServiceOptions("en"),
            new SettingsStore(Path.Combine(Path.GetTempPath(), "ulc_overlay_" + Guid.NewGuid().ToString("N"))),
            new UserSettings());
        return new CaptionDisplayCaller(overlay);
    }

    [Fact]
    public void Partial_churn_paints_and_rewrites_the_same_active_block_and_history_is_unaffected()
    {
        RunOnSta(() =>
        {
            var caller = CreateOverlay();
            // A committed history is present; the live active line carries a partial.
            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("hell", 99),
                History: new[] { D("hello world", 1), D("how are you", 2) }));

            TextBlock[] before = caller.HistoryBlocks().ToArray();
            TextBlock activeBefore = Assert.IsType<TextBlock>(caller.ActiveBlock());
            Assert.Equal("hell", activeBefore.Text);

            // A newer partial for the same in-flight utterance arrives.
            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("hello", 99),
                History: new[] { D("hello world", 1), D("how are you", 2) }));

            // History instances are bit-for-bit unchanged.
            var after = caller.HistoryBlocks();
            Assert.Equal(before.Length, after.Count);
            for (int i = 0; i < before.Length; i++)
            {
                Assert.Same(before[i], after[i]);
            }

            // The active line is painted and rewritten in place: same block instance, new text.
            Assert.Same(activeBefore, caller.ActiveBlock());
            Assert.Equal("hello", caller.ActiveText());
        });
    }

    [Fact]
    public void Growing_partial_stream_paints_one_live_block_with_no_history_churn()
    {
        RunOnSta(() =>
        {
            var caller = CreateOverlay();
            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("the quick", 0),
                History: Array.Empty<CaptionDisplayLine>()));

            TextBlock active = Assert.IsType<TextBlock>(caller.ActiveBlock());
            Assert.Equal("the quick", active.Text);

            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("the quick brown", 0),
                History: Array.Empty<CaptionDisplayLine>()));

            // The partial stream paints one live block and mutates it in place; no history is born.
            Assert.Same(active, caller.ActiveBlock());
            Assert.Equal("the quick brown", caller.ActiveText());
            Assert.Empty(caller.HistoryBlocks());
        });
    }

    [Fact]
    public void No_partial_is_ever_written_into_committed_history()
    {
        RunOnSta(() =>
        {
            var caller = CreateOverlay();
            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("the quick", 0),
                History: Array.Empty<CaptionDisplayLine>()));
            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("the quick brown", 0),
                History: Array.Empty<CaptionDisplayLine>()));

            // The live partial is only ever the active block, never a committed history block.
            Assert.NotNull(caller.ActiveBlock());
            Assert.Empty(caller.HistoryBlocks());
            Assert.DoesNotContain(caller.ActiveBlock(), caller.HistoryBlocks());
        });
    }

    [Fact]
    public void Final_freezes_active_into_history_and_removes_the_active_block()
    {
        RunOnSta(() =>
        {
            var caller = CreateOverlay();
            // The live partial is painted while the utterance is in flight.
            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("the quick brown fox", 1),
                History: Array.Empty<CaptionDisplayLine>()));
            Assert.NotNull(caller.ActiveBlock());

            // The utterance finalizes: the active line freezes into history and the active block
            // is removed so the same text is never shown twice.
            caller.Update(new CaptionDisplayModel(
                ActiveLine: null,
                History: new[] { D("the quick brown fox", 1) }));

            Assert.Single(caller.HistoryBlocks());
            Assert.Equal("the quick brown fox", caller.HistoryBlocks()[0].Text);
            Assert.Null(caller.ActiveBlock());
        });
    }

    [Fact]
    public void Cleared_active_line_removes_the_active_block_and_keeps_history()
    {
        RunOnSta(() =>
        {
            var caller = CreateOverlay();
            caller.Update(new CaptionDisplayModel(
                ActiveLine: D("partial text", 7),
                History: new[] { D("committed line", 1) }));
            Assert.NotNull(caller.ActiveBlock());

            // Stop/session end clears the active line; the committed history stays intact.
            caller.Update(new CaptionDisplayModel(
                ActiveLine: null,
                History: new[] { D("committed line", 1) }));

            Assert.Null(caller.ActiveBlock());
            Assert.Single(caller.HistoryBlocks());
            Assert.Equal("committed line", caller.HistoryBlocks()[0].Text);
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
