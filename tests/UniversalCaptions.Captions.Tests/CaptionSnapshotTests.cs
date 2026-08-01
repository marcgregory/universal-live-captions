using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Speech;

namespace UniversalCaptions.Captions.Tests;

/// <summary>
/// Verifies <see cref="ICaptionService.GetSnapshot"/>: it is a synchronized, immutable, internally
/// consistent copy of <see cref="CaptionState"/> that stays detached from later mutations and never
/// races a concurrent commit.
/// </summary>
public sealed class CaptionSnapshotTests
{
    private static FinalTranscript Final(long sequence, string text) =>
        new(text, DateTime.UtcNow, DateTime.UtcNow, sequence);

    private static CaptionService CreateService(int historyCapacity = 50) =>
        new(new CaptionServiceOptions("en", targetLanguage: "tl", historyCapacity));

    [Fact]
    public void GetSnapshot_matches_current_state()
    {
        var service = CreateService();
        service.Start();
        service.ProcessFinal(Final(1, "hello"));
        service.SetTranslationEnabled(true);

        CaptionSnapshot snapshot = service.GetSnapshot();

        Assert.Single(snapshot.History);
        Assert.Equal("hello", snapshot.History[0].Text);
        Assert.True(snapshot.IsSessionActive);
        Assert.True(snapshot.TranslationEnabled);
        Assert.Equal("tl", snapshot.TargetLanguage);
    }

    [Fact]
    public void GetSnapshot_returns_consistent_active_line_and_history()
    {
        var service = CreateService();
        service.Start();
        service.ProcessPartial(new PartialTranscript("part", DateTime.UtcNow, DateTime.UtcNow, 1));
        service.ProcessFinal(Final(2, "committed"));

        CaptionSnapshot snapshot = service.GetSnapshot();

        Assert.Null(snapshot.ActiveLine);
        Assert.Single(snapshot.History);
    }

    [Fact]
    public void GetSnapshot_is_detached_from_later_commits()
    {
        var service = CreateService();
        service.Start();
        service.ProcessFinal(Final(1, "one"));

        CaptionSnapshot snapshot = service.GetSnapshot();
        service.ProcessFinal(Final(2, "two"));
        service.Reset();

        var captured = Assert.Single(snapshot.History);
        Assert.Equal("one", captured.Text);
    }

    [Fact]
    public void GetSnapshot_history_is_immutable_snapshot_not_a_live_view()
    {
        var service = CreateService();
        service.Start();
        service.ProcessFinal(Final(1, "one"));

        IReadOnlyList<CaptionLine> history = service.GetSnapshot().History;

        service.ProcessFinal(Final(2, "two"));

        Assert.Single(history);
        Assert.Equal("one", history[0].Text);
    }

    [Fact]
    public async Task GetSnapshot_concurrent_with_mutations_is_consistent()
    {
        var service = CreateService(historyCapacity: 10);
        service.Start();

        var mutator = Task.Run(() =>
        {
            for (long i = 0; i < 5000; i++)
            {
                service.ProcessFinal(Final(i, $"line {i}"));
            }
        });

        for (int i = 0; i < 500; i++)
        {
            CaptionSnapshot snapshot = service.GetSnapshot();
            Assert.True(snapshot.History.Count <= 10, "A snapshot must never exceed the history capacity.");
            for (int j = 1; j < snapshot.History.Count; j++)
            {
                Assert.True(
                    snapshot.History[j - 1].Sequence < snapshot.History[j].Sequence,
                    "A snapshot's history must stay in ascending sequence order.");
            }
        }

        await mutator;

        CaptionSnapshot final = service.GetSnapshot();
        Assert.Equal(10, final.History.Count);
        Assert.Equal(4999, final.History[^1].Sequence);
    }
}
