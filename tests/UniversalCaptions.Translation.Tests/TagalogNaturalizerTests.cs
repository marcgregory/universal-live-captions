namespace UniversalCaptions.Translation.Tests;

/// <summary>
/// Verifies the deterministic rule-based <see cref="TagalogNaturalizer"/>: each recurring Argos
/// en→tl construction is rewritten to conversational Tagalog, unmatched text is untouched,
/// matching is word-boundary aware, and case is preserved.
/// </summary>
public sealed class TagalogNaturalizerTests
{
    [Fact]
    public void Naturalize_MalugodNaTanggapin_BecomesMaligayangPagdating()
    {
        string result = TagalogNaturalizer.Naturalize(
            "Hello at malugod na tanggapin sa unang pulong ng aming Conversional Tagalog Practice Group.");

        Assert.Equal(
            "Kamusta at maligayang pagdating sa unang pulong ng aming Conversational Tagalog Practice Group.",
            result);
    }

    [Fact]
    public void Naturalize_PakisuyongBuksan_BecomesPakibuksan()
    {
        string result = TagalogNaturalizer.Naturalize(
            "Pakisuyong buksan ang inyong mga kuwaderno sa unang pahina.");

        Assert.Equal("Pakibuksan ang inyong mga kuwaderno sa unang pahina.", result);
    }

    [Fact]
    public void Naturalize_MakikitaKaNamin_BecomesMagkikitaTayoUlit()
    {
        string result = TagalogNaturalizer.Naturalize("Makikita ka namin sa susunod na linggo.");

        Assert.Equal("Magkikita tayo ulit sa susunod na linggo.", result);
    }

    [Fact]
    public void Naturalize_DakilangGawaAngLahat_BecomesMagandangTrabahoSaInyongLahat()
    {
        string result = TagalogNaturalizer.Naturalize(
            "Dakilang gawa ang lahat, iyan ang wakas ng kasalukuyang sesyon ng pagsasanay.");

        Assert.Equal(
            "Magandang trabaho sa inyong lahat, iyan ang katapusan ng ating sesyon sa pagsasanay.",
            result);
    }

    [Fact]
    public void Naturalize_SaNgayonAyMagsasanayPambungad_BecomesNaturalOpening()
    {
        string result = TagalogNaturalizer.Naturalize(
            "Sa ngayon ay magsasanay tayo ng mga pagbati at pambungad.");

        Assert.Equal("Ngayon ay mag-eensayo tayo ng mga pagbati at pagpapakilala.", result);
    }

    [Fact]
    public void Naturalize_SpacedReduplicationAndMisspelling_AreCleaned()
    {
        string result = TagalogNaturalizer.Naturalize(
            "Hello at malugod na tanggapin sa unang pulong ng aming grupong nag - uusap - usap na tangalog.");

        Assert.Equal(
            "Kamusta at maligayang pagdating sa unang pulong ng aming grupong nakikipag-usap-usap na tagalog.",
            result);
    }

    [Fact]
    public void Naturalize_NoMatch_ReturnsTextUnchanged()
    {
        string[] unchanged = new[]
        {
            "Ang pangalan ko ay Maria.",
            "Ano ang pangalan mo?",
            "Magandang umaga lahat.",
            "Salamat sa inyong pakikinig.",
            "Magsisimula tayo sa numerong 1 hanggang 10, pagkatapos ay lilipat tayo sa mga araw ng sanlinggo.",
        };

        foreach (string text in unchanged)
        {
            Assert.Equal(text, TagalogNaturalizer.Naturalize(text));
        }
    }

    [Fact]
    public void Naturalize_MidWord_DoesNotFire()
    {
        // "pambungad" must not fire inside "pagpambungad", nor "tagalog" inside "Katangalog".
        Assert.Equal("pagpambungad Katangalog", TagalogNaturalizer.Naturalize("pagpambungad Katangalog"));
    }

    [Fact]
    public void Naturalize_LowercaseInput_StaysLowercase()
    {
        string result = TagalogNaturalizer.Naturalize("hello at malugod na tanggapin");

        Assert.Equal("kamusta at maligayang pagdating", result);
    }

    [Fact]
    public void Naturalize_AllCapsInput_BecomesAllCaps()
    {
        string result = TagalogNaturalizer.Naturalize("MAKIKITA KA NAMIN SA SUSUNOD NA LINGGO.");

        Assert.Equal("MAGKIKITA TAYO ULIT SA SUSUNOD NA LINGGO.", result);
    }

    [Fact]
    public void Naturalize_RepeatedApplication_IsIdempotent()
    {
        const string text = "Dakilang gawa ang lahat, iyan ang wakas ng kasalukuyang sesyon ng pagsasanay.";

        string once = TagalogNaturalizer.Naturalize(text);
        string twice = TagalogNaturalizer.Naturalize(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Naturalize_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TagalogNaturalizer.Naturalize(string.Empty));
    }

    [Fact]
    public void Naturalize_NullText_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TagalogNaturalizer.Naturalize(null!));
    }
}
