using UniversalCaptions.App.Overlay;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Unit tests for the v0.5.38 partial-rendering split: which leading words of the current partial
/// the immediately-previous partial already confirmed ("stable", painted normal) versus the newly
/// appeared tail ("unstable", painted the subtle green), and where the two-tone boundary lands
/// while preserving the original spacing.
/// </summary>
public class CaptionPartialStabilityTests
{
    [Fact]
    public void No_previous_partial_means_nothing_is_stable()
    {
        Assert.Equal(0, CaptionPartialStability.StableWordCount(null, "hello world"));
        Assert.Equal(0, CaptionPartialStability.StableWordCount(string.Empty, "hello world"));
        Assert.Equal(0, CaptionPartialStability.StableWordCount("   ", "hello world"));
    }

    [Fact]
    public void Null_or_blank_current_partial_is_never_stable()
    {
        Assert.Equal(0, CaptionPartialStability.StableWordCount("hello world", null!));
        Assert.Equal(0, CaptionPartialStability.StableWordCount("hello world", string.Empty));
    }

    [Fact]
    public void Identical_partials_are_entirely_stable()
    {
        Assert.Equal(3, CaptionPartialStability.StableWordCount("the quick brown", "the quick brown"));
    }

    [Fact]
    public void Growing_partial_confirms_the_shared_prefix()
    {
        Assert.Equal(2, CaptionPartialStability.StableWordCount("the quick", "the quick brown"));
    }

    [Fact]
    public void Comparison_is_case_insensitive()
    {
        Assert.Equal(2, CaptionPartialStability.StableWordCount("The Quick", "the quick brown"));
    }

    [Fact]
    public void Trailing_punctuation_is_ignored_when_comparing_words()
    {
        Assert.Equal(3, CaptionPartialStability.StableWordCount("the quick brown.", "the quick brown fox"));
        Assert.Equal(3, CaptionPartialStability.StableWordCount("the, quick; brown!", "the quick brown fox"));
    }

    [Fact]
    public void Revised_word_stops_the_stable_prefix()
    {
        // "Administrtion" (the previous partial's typo) is NOT re-recognized as "Administration",
        // so even the re-confirmed head stays unconfirmed — the whole line paints unstable.
        Assert.Equal(0, CaptionPartialStability.StableWordCount("Administrtion is", "Administration is a"));
    }

    [Fact]
    public void Revised_tail_word_confirms_only_the_shared_head()
    {
        Assert.Equal(3, CaptionPartialStability.StableWordCount("the quick brown fox", "the quick brown cat"));
    }

    [Fact]
    public void Shrinking_partial_confirms_the_shared_prefix()
    {
        Assert.Equal(2, CaptionPartialStability.StableWordCount("the quick brown", "the quick"));
    }

    [Fact]
    public void Diverging_second_word_stops_at_the_first()
    {
        Assert.Equal(1, CaptionPartialStability.StableWordCount("the quick brown", "the slow red fox"));
    }

    [Fact]
    public void Empty_text_splits_to_empty_parts()
    {
        Assert.Equal((string.Empty, string.Empty), CaptionPartialStability.SplitAtWord(string.Empty, 0));
    }

    [Fact]
    public void Zero_stable_words_paints_the_whole_line_unstable()
    {
        Assert.Equal((string.Empty, "hello world"), CaptionPartialStability.SplitAtWord("hello world", 0));
        Assert.Equal((string.Empty, "hello world"), CaptionPartialStability.SplitAtWord("hello world", -1));
    }

    [Fact]
    public void Count_beyond_all_words_paints_the_whole_line_stable()
    {
        Assert.Equal(("hello world", string.Empty), CaptionPartialStability.SplitAtWord("hello world", 2));
        Assert.Equal(("hello world", string.Empty), CaptionPartialStability.SplitAtWord("hello world", 5));
    }

    [Fact]
    public void Boundary_lands_after_the_stable_word_preserving_spacing()
    {
        (string stable, string unstable) = CaptionPartialStability.SplitAtWord("the quick brown fox", 3);
        Assert.Equal("the quick brown", stable);
        Assert.Equal(" fox", unstable);
        Assert.Equal("the quick brown fox", stable + unstable);
    }

    [Fact]
    public void Multiple_whitespace_between_words_is_preserved_in_the_unstable_tail()
    {
        (string stable, string unstable) = CaptionPartialStability.SplitAtWord("the quick   brown fox", 2);
        Assert.Equal("the quick", stable);
        Assert.Equal("   brown fox", unstable);
        Assert.Equal("the quick   brown fox", stable + unstable);
    }

    [Fact]
    public void Leading_punctuation_words_are_counted_as_words()
    {
        Assert.Equal(2, CaptionPartialStability.StableWordCount("(hello) world", "(hello) world again"));
    }
}
