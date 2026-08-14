using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Translation;
using UniversalCaptions.Speech.Gemini;
using Xunit.Abstractions;

namespace UniversalCaptions.Speech.Gemini.Tests;

/// <summary>
/// Corpus-driven phrase-guard validation (2026-08-14, ACTIVE investigation, decision-gated). This is
/// the SECOND, corpus-driven study required before any phrase-level continuation guard may touch
/// production. It is a MEASUREMENT suite: no production code changes, the v0.5.40 gate stays untouched
/// (<c>flushBoundary = terminal &amp;&amp; !restate &amp;&amp; !lowercase</c>), and no v0.5.41 is created.
/// </summary>
/// <remarks>
/// <para>
/// The single decision metric is <b>false-split reduction − over-join cost</b> per candidate phrase.
/// For every candidate the corpus must contain BOTH continuation examples (→ APPEND is the fix) AND
/// genuine sentence-start examples (→ APPEND is an over-join the guard must not cause), including short
/// fragments like <c>Hindi Lunes.</c> (len 12). The 7 observed Cat 2 cases and all 8 Cat 3 ambiguous
/// pairs from the matrix are mandatory corpus members.
/// </para>
/// <para>
/// Baseline (what the CURRENT gate does per case) is measured by driving the real engine through
/// <see cref="FakeGeminiChannel"/> — never assumed. The candidate phrase guard is implemented ONLY in
/// this test project (<see cref="StartsWithPhrase"/> layered on the measured baseline). The measured
/// numbers are the evidence; the investigation document records them and the human applies the
/// three-outcome decision gate: Ship / Reject / Insufficient evidence.
/// </para>
/// <para>
/// This investigation stays separate from the closed v0.5.40 study + matrix
/// (<see cref="SegmentationGuardMatrixTests"/>, commit <c>d9e24ab</c>).
/// </para>
/// </remarks>
public sealed class PhraseGuardCorpusValidationTests
{
    private const string ApiKey = "test-api-key";
    private const string Model = "models/gemini-3.5-live-translate-preview";
    private const string Target = "tl";

    private readonly ITestOutputHelper _output;

    public PhraseGuardCorpusValidationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Annotated semantic boundary for a corpus case.</summary>
    public enum Truth { Continuation, NewSentence }

    /// <summary>The candidate phrase guard (name + leading tokens, matched case-insensitively).</summary>
    public sealed record CandidatePhrase(string Name, string[] Tokens);

    private static readonly CandidatePhrase[] CandidatePhrases =
    {
        new("At pagkatapos", new[] { "at", "pagkatapos" }),
        new("At makinig", new[] { "at", "makinig" }),
        new("Kaya kailangan", new[] { "kaya", "kailangan" }),
        new("Sige, gawin", new[] { "sige", "gawin" }),
        new("Pero pagkatapos", new[] { "pero", "pagkatapos" }),
        new("Dahil dito", new[] { "dahil", "dito" }),
        new("Hindi <fragment>", new[] { "hindi" }),
        new("And then (en)", new[] { "and", "then" }),
        new("So we need (en)", new[] { "so", "we", "need" }),
        new("But then (en)", new[] { "but", "then" }),
        new("Not (en)", new[] { "not" }),
    };

    /// <summary>Negative control: the unsafe bare-starter allowlist the matrix already rejected.</summary>
    private static readonly string[] BareWordControl = { "at", "kaya", "sige", "hindi" };

    private static readonly string[] ObservedCat2Ids =
    {
        "P-C2-01", "P-C2-02", "P-C2-03", "P-C2-04", "P-C2-05", "P-C2-06", "P-C2-07",
    };

    private static readonly string[] Cat3PairIds =
    {
        "C3a-01", "C3a-02", "C3b-01", "C3b-02", "C3c-01", "C3c-02", "C3d-01", "C3d-02",
    };

    public sealed record CorpusCase(
        string Id,
        string CandidateName,
        string Accumulator,
        string Fragment,
        Truth Truth,
        string Source,
        string Note);

    private static readonly CorpusCase[] Corpus =
    {
        // ----- Observed Cat 2 false splits (REAL evidence) → the cases the guard must fix -----
        new("P-C2-01", "Hindi <fragment>", "Tandaan, ang deadline ay Biyernes.", " Hindi Lunes.",
            Truth.Continuation, "REAL 6/10-run split", "short fragment len 12 (<15)"),
        new("P-C2-02", "At pagkatapos", "Bago tayo magsimula, hayaan niyo akong i-review ang plano.", " At pagkatapos ay maaari tayong magtanong.",
            Truth.Continuation, "REAL 5/10-run split", "phrase idiom 'At pagkatapos'"),
        new("P-C2-03", "At makinig", "Kailangan nating maging malinaw sa lahat.", " At makinig nang mabuti bago tayo magpatuloy.",
            Truth.Continuation, "REAL primary 2/10-run split", "'At makinig' continuation"),
        new("P-C2-04", "Kaya kailangan", "Kailangan nating tapusin ito ngayon.", " Kaya kailangan nating magmadali.",
            Truth.Continuation, "matrix idiom", "'Kaya kailangan' continuation"),
        new("P-C2-05", "Sige, gawin", "Maaari na tayong magpatuloy.", " Sige, gawin natin iyon ngayon.",
            Truth.Continuation, "matrix idiom", "'Sige, gawin natin' continuation"),
        new("P-C2-06", "Pero pagkatapos", "Dapat tayong maghintay ng kaunti.", " Pero pagkatapos, titingnan natin ito.",
            Truth.Continuation, "matrix idiom", "'Pero pagkatapos' contrastive continuation"),
        new("P-C2-07", "Dahil dito", "Nahuli tayo sa trapiko kanina.", " Dahil dito, hindi tayo nakarating.",
            Truth.Continuation, "matrix idiom", "'Dahil dito' causal continuation"),

        // ----- Unseen variants of the same construction (→ APPEND) -----
        new("P-VAR-01", "At pagkatapos", "Tapusin natin ang unang bahagi.", " At pagkatapos ay gagawin natin ang pangalawa.",
            Truth.Continuation, "constructed", "unseen variant"),
        new("P-VAR-02", "At makinig", "Pakinggan muna ang mga patakaran.", " At makinig sa bawat detalye.",
            Truth.Continuation, "constructed", "unseen variant"),
        new("P-VAR-03", "Kaya kailangan", "Malapit na ang deadline.", " Kaya kailangan nating magsimula na.",
            Truth.Continuation, "constructed", "unseen variant"),
        new("P-VAR-04", "Sige, gawin", "Tama na ang paghihintay.", " Sige, gawin natin ang plano.",
            Truth.Continuation, "constructed", "unseen variant"),
        new("P-VAR-05", "Pero pagkatapos", "Una, suriin natin ang datos.", " Pero pagkatapos, tatalakayin natin ito.",
            Truth.Continuation, "constructed", "unseen variant"),
        new("P-VAR-06", "Dahil dito", "May aberya sa sistema.", " Dahil dito, naantala ang lahat.",
            Truth.Continuation, "constructed", "unseen variant"),
        new("P-VAR-07", "Hindi <fragment>", "Ang pulong ay sa Miyerkules.", " Hindi Huwebes.",
            Truth.Continuation, "constructed", "unseen variant"),

        // ----- Genuine sentence starts with the SAME idiom (→ FLUSH: the over-join the guard must not cause) -----
        new("P-NEW-01", "At pagkatapos", "Tapos na ang pulong.", " At pagkatapos, umalis na kami.",
            Truth.NewSentence, "constructed", "genuine new-sentence reading: comma + new subject"),
        new("P-NEW-02", "At pagkatapos", "Tapos na ang talumpati.", " At pagkatapos, nagsimula na ang mga tanong.",
            Truth.NewSentence, "constructed", "genuine new-sentence reading"),
        new("P-NEW-03", "Kaya kailangan", "Mahalaga ang proyektong ito.", " Kaya kailangan nating magmadali.",
            Truth.NewSentence, "constructed", "genuine new-sentence reading — IDENTICAL fragment to P-C2-04, different accumulator"),
        new("P-NEW-04", "Sige, gawin", "Tapos na ang talakayan.", " Sige, gawin natin ang susunod na hakbang.",
            Truth.NewSentence, "constructed", "imperative response start (the over-join risk)"),
        new("P-NEW-05", "Pero pagkatapos", "Natapos na ang pagsubok.", " Pero pagkatapos, may bagong problema.",
            Truth.NewSentence, "constructed", "contrastive new sentence"),
        new("P-NEW-06", "Dahil dito", "Tandaan ang nangyari.", " Dahil dito, mag-ingat tayo sa susunod.",
            Truth.NewSentence, "constructed", "causal new sentence"),
        new("P-NEW-07", "At makinig", "Tapos na ang paunang pagtalakay.", " At makinig, may sasabihin ako sa inyo.",
            Truth.NewSentence, "constructed", "imperative new utterance"),
        new("P-NEW-08", "Hindi <fragment>", "Dapat sumagot tayo ngayon.", " Hindi natin kaya.",
            Truth.NewSentence, "constructed", "negation new-sentence reading"),

        // ----- Cat 3 bare-starter pairs (MANDATORY negatives, from the matrix) -----
        new("C3a-01", "At (bare control)", "Natapos na ang plano.", " At bukas magsisimula tayo.",
            Truth.NewSentence, "matrix Cat 3a", "'At' new-sentence reading — must NOT match the 'At pagkatapos' guard"),
        new("C3a-02", "At (bare control)", "Natapos na ang plano.", " At pagkatapos ay umalis tayo.",
            Truth.Continuation, "matrix Cat 3a", "'At' continuation reading"),
        new("C3b-01", "Kaya (bare control)", "Kailangan nating tapusin ito ngayon.", " Kaya sinimulan namin ang bagong proyekto.",
            Truth.NewSentence, "matrix Cat 3b", "'Kaya' new-sentence reading — must NOT match the 'Kaya kailangan' guard"),
        new("C3b-02", "Kaya (bare control)", "Kailangan nating tapusin ito ngayon.", " Kaya narito tayo ngayon.",
            Truth.Continuation, "matrix Cat 3b", "'Kaya' continuation reading"),
        new("C3c-01", "Sige, (bare control)", "Tapos na ang pulong.", " Sige, magsisimula na tayo sa susunod.",
            Truth.NewSentence, "matrix Cat 3c", "'Sige' new-utterance reading — must NOT match the 'Sige, gawin' guard"),
        new("C3c-02", "Sige, (bare control)", "Tapos na ang pulong.", " Sige, gawin natin iyon mamaya.",
            Truth.Continuation, "matrix Cat 3c", "'Sige' continuation reading"),
        new("C3d-01", "Hindi <fragment>", "Nakita ko na ang dokumento.", " Hindi ko alam kung saan ito.",
            Truth.NewSentence, "matrix Cat 3d", "'Hindi' new-sentence reading — IDENTICAL prefix to P-C2-01"),
        new("C3d-02", "Hindi <fragment>", "Nakita ko na ang dokumento.", " Hindi ito ang tamang bersyon.",
            Truth.Continuation, "matrix Cat 3d", "'Hindi' continuation reading"),

        // ----- Punctuation / capitalization / length axes -----
        new("PUNC-01", "At pagkatapos", "Bago tayo magsimula, hayaan niyo akong i-review ang plano.", " At pagkatapos ay maaari tayong magtanong,",
            Truth.Continuation, "constructed", "trailing comma after the fragment"),
        new("PUNC-02", "At pagkatapos", "Bago tayo magsimula, hayaan niyo akong i-review ang plano.", " At pagkatapos, ay magtatanong tayo.",
            Truth.Continuation, "constructed", "comma inside the phrase"),
        new("CAP-01", "At pagkatapos", "Bago tayo magsimula, hayaan niyo akong i-review ang plano.", " AT PAGKATAPOS AY MAGTATANONG TAYO.",
            Truth.Continuation, "constructed", "all-caps — case-insensitive phrase match expected"),
        new("SHORT-NEW", "At pagkatapos", "Tapos na ang pulong.", " Umalis na kami.",
            Truth.NewSentence, "constructed", "short genuine new sentence — must stay FLUSH"),
        new("LC-01", "At pagkatapos", "Bago tayo magsimula, hayaan niyo akong i-review ang plano.", " at pagkatapos ay magtatanong tayo.",
            Truth.Continuation, "constructed", "lowercase continuation — gate already APPENDs (Cat 1); guard must not regress it"),

        // ----- English equivalents (en target path) -----
        new("EN-01", "And then (en)", "Hello everyone, thanks for joining.", " And then we can take questions.",
            Truth.Continuation, "real english_boundary transcript", "en analog of 'At pagkatapos'"),
        new("EN-02", "And then (en)", "The meeting is over.", " And then we all went home.",
            Truth.NewSentence, "constructed", "genuine new sentence"),
        new("EN-03", "So we need (en)", "We must finish today.", " So we need to hurry.",
            Truth.Continuation, "constructed", "en analog of 'Kaya kailangan'"),
        new("EN-04", "So we need (en)", "The plan is clear.", " So we need a bigger team.",
            Truth.NewSentence, "constructed", "new-sentence reading"),
        new("EN-05", "But then (en)", "Wait a moment.", " But then we will check it.",
            Truth.Continuation, "constructed", "en analog of 'Pero pagkatapos'"),
        new("EN-06", "But then (en)", "The test finished.", " But then there was a new problem.",
            Truth.NewSentence, "constructed", "new-sentence reading"),
        new("EN-07", "Not (en)", "Remember, the deadline is Friday.", " Not Monday.",
            Truth.Continuation, "real english boundary", "en analog of 'Hindi Lunes.' (len 11)"),
        new("EN-08", "Not (en)", "I saw the document.", " Not sure where it is.",
            Truth.NewSentence, "constructed", "new-sentence reading"),
    };

    /// <summary>Every candidate phrase must have BOTH a continuation example and a genuine new-sentence
    /// example in the corpus — otherwise the false-split-reduction vs over-join trade-off cannot be
    /// measured for it.</summary>
    [Fact]
    public void Corpus_EveryCandidatePhrase_HasContinuationAndNewSentenceExamples()
    {
        foreach (CandidatePhrase phrase in CandidatePhrases)
        {
            CorpusCase[] tagged = Corpus.Where(c => c.CandidateName == phrase.Name).ToArray();
            Assert.True(
                tagged.Any(c => c.Truth == Truth.Continuation),
                $"{phrase.Name}: corpus has no continuation example — cannot measure reduction.");
            Assert.True(
                tagged.Any(c => c.Truth == Truth.NewSentence),
                $"{phrase.Name}: corpus has no genuine sentence-start example — cannot measure over-join cost.");
        }
    }

    /// <summary>The corpus must contain all 7 observed Cat 2 false splits and all 8 Cat 3 ambiguous
    /// pairs from the matrix — the observed evidence is the floor, never dropped.</summary>
    [Fact]
    public void Corpus_ContainsThe7ObservedCat2And8Cat3Evidence()
    {
        Assert.All(ObservedCat2Ids, id => Assert.Contains(Corpus, c => c.Id == id));
        Assert.All(Cat3PairIds, id => Assert.Contains(Corpus, c => c.Id == id));
    }

    /// <summary>The phrase matcher is exact-token: it must match the multi-word idiom but NOT a bare
    /// starter with the same first word (e.g. <c>At bukas…</c> must never be caught by the
    /// <c>At pagkatapos</c> guard). This is precisely what makes it safer than the rejected bare
    /// <c>At|Kaya|Sige|Hindi</c> allowlist.</summary>
    [Theory]
    [InlineData(" At pagkatapos ay umalis tayo.", "At pagkatapos", true)]
    [InlineData(" At bukas magsisimula tayo.", "At pagkatapos", false)]
    [InlineData(" Sige, gawin natin iyon mamaya.", "Sige, gawin", true)]
    [InlineData(" Sige, magsisimula na tayo sa susunod.", "Sige, gawin", false)]
    [InlineData(" Kaya narito tayo ngayon.", "Kaya kailangan", false)]
    [InlineData(" Hindi Lunes.", "Hindi <fragment>", true)]
    [InlineData(" Hindi ko alam kung saan ito.", "Hindi <fragment>", true)]
    public void PhraseMatcher_MatchesOnlyTheExactPhrase(string fragment, string phraseName, bool expected)
    {
        string[] tokens = CandidatePhrases.First(p => p.Name == phraseName).Tokens;
        Assert.Equal(expected, StartsWithPhrase(fragment, tokens));
    }

    /// <summary>
    /// The irreducible-ambiguity pin: P-C2-04 and P-NEW-03 share the IDENTICAL fragment
    /// (<c> Kaya kailangan nating magmadali.</c>), once annotated a continuation (after "…tapusin ito
    /// ngayon.") and once a genuine new sentence (after "…proyektong ito."). A lexical phrase guard sees
    /// the same input in both — it provably cannot separate them. The same holds for the <c>Hindi</c>
    /// prefix (C3d-01 vs P-C2-01) and the <c>And then</c> pair (EN-01 vs EN-02).
    /// </summary>
    [Fact]
    public void SameSurfaceFragments_ProveTheGuardCannotSeparateReadings()
    {
        var cont = Corpus.First(c => c.Id == "P-C2-04");
        var news = Corpus.First(c => c.Id == "P-NEW-03");
        Assert.Equal(cont.Fragment, news.Fragment);
        Assert.Equal(Truth.Continuation, cont.Truth);
        Assert.Equal(Truth.NewSentence, news.Truth);
        Assert.True(StartsWithPhrase(cont.Fragment, Phrase("Kaya kailangan")), "guard must match the shared fragment (else it fixes nothing)");

        var hindiCont = Corpus.First(c => c.Id == "P-C2-01");
        var hindiNews = Corpus.First(c => c.Id == "C3d-01");
        Assert.Equal(FirstWord(hindiCont.Fragment), FirstWord(hindiNews.Fragment));
        Assert.True(StartsWithPhrase(hindiCont.Fragment, Phrase("Hindi <fragment>")));
        Assert.True(StartsWithPhrase(hindiNews.Fragment, Phrase("Hindi <fragment>")));
    }

    /// <summary>
    /// THE measurement: for every candidate phrase, drive the CURRENT engine gate (baseline, measured —
    /// never assumed), then layer the test-side phrase guard and compute
    /// <b>reduction (continuations fixed) − over-join (genuine sentences wrongly joined)</b>. The table
    /// is emitted to output; the numbers are the evidence for the Ship / Reject / Insufficient decision.
    /// </summary>
    [Fact]
    public async Task PhraseGuard_Metrics_ReductionMinusOverJoin_IsMeasured()
    {
        var baselines = new List<(CorpusCase Case, string Baseline)>();
        foreach (CorpusCase c in Corpus)
        {
            baselines.Add((c, await RunGateAsync(c)));
        }

        _output.WriteLine("=== Baseline: current v0.5.40 gate per corpus case (measured) ===");
        foreach (var (c, b) in baselines)
        {
            _output.WriteLine($"{c.Id} [{c.Truth}] {b} {c.Note}");
        }

        _output.WriteLine(string.Empty);
        _output.WriteLine("=== Metric: phrase guard = current gate + phrase override ===");
        _output.WriteLine("phrase            | reduction | over-join | net | over-joined ids");
        foreach (CandidatePhrase phrase in CandidatePhrases)
        {
            (int reduction, int overJoin, string[] overIds) = Measure(phrase.Tokens, baselines);
            _output.WriteLine(
                $"{phrase.Name,-17} | {reduction,9} | {overJoin,9} | {reduction - overJoin,3} | {string.Join(", ", overIds)}");
        }

        int controlOverJoin = baselines.Count(x =>
            x.Baseline == "Flush"
            && x.Case.Truth == Truth.NewSentence
            && BareWordControl.Contains(FirstWord(x.Case.Fragment).ToLowerInvariant()));

        _output.WriteLine(string.Empty);
        _output.WriteLine($"=== Negative control: unsafe bare-starter allowlist ({string.Join("|", BareWordControl)}) ===");
        _output.WriteLine($"bare-starter over-joins = {controlOverJoin} (must be >= every phrase guard's over-join)");

        // The observed 7 Cat 2 false splits must all FLUSH under the current gate — the gap the guard
        // is being validated against. This pins the baseline to the real engine measurement.
        Assert.All(ObservedCat2Ids, id =>
        {
            (CorpusCase c, string b) = baselines.First(x => x.Case.Id == id);
            Assert.True(b == "Flush", $"{id}: expected current gate to FLUSH (the known gap), measured {b}");
        });

        // Lowercase continuations must remain APPEND (no regression).
        Assert.True(baselines.First(x => x.Case.Id == "LC-01").Baseline == "Append", "LC-01: lowercase continuation must stay APPEND");

        // The negative control must dominate every candidate's over-join: the phrase guard is strictly
        // more conservative than the bare-starter allowlist the matrix already rejected.
        int maxPhraseOverJoin = CandidatePhrases.Max(p => Measure(p.Tokens, baselines).overJoin);
        Assert.True(
            controlOverJoin >= maxPhraseOverJoin,
            $"bare-starter allowlist ({controlOverJoin}) should over-join at least as much as any phrase guard ({maxPhraseOverJoin}).");
    }

    // ----- Measurement -----

    private (int reduction, int overJoin, string[] overJoinIds) Measure(
        string[] phraseTokens,
        List<(CorpusCase Case, string Baseline)> baselines)
    {
        int reduction = 0;
        int overJoin = 0;
        var overIds = new List<string>();
        foreach (var (c, b) in baselines)
        {
            // Only cases the current gate FLUShes can be affected (lowercase continuations are already
            // APPEND; the phrase guard is a no-op there and must not regress them).
            if (b != "Flush" || !StartsWithPhrase(c.Fragment, phraseTokens))
            {
                continue;
            }

            if (c.Truth == Truth.Continuation)
            {
                reduction++;
            }
            else
            {
                overJoin++;
                overIds.Add(c.Id);
            }
        }

        return (reduction, overJoin, overIds.ToArray());
    }

    // ----- Engine drive (mirrors SegmentationGuardMatrixTests.RunGateAsync) -----

    private static async Task<string> RunGateAsync(CorpusCase @case)
    {
        var channel = new FakeGeminiChannel();
        var options = new GeminiLiveTranslateEngineOptions
        {
            ApiKey = ApiKey,
            Model = Model,
            TargetLanguage = Target,
            CommitIdleTimeout = TimeSpan.Zero, // the ONLY final source is the flush gate
        };
        await using var engine = new GeminiLiveTranslateEngine(options, channel);
        var finals = new List<FinalTranslation>();
        var partials = new List<PartialTranslation>();
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);
        engine.PartialTranslationAvailable += (_, p) => partials.Add(p);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        channel.QueueServerFrame(BuildServerContent(@case.Accumulator, partial: true, turnComplete: false));
        await WaitForAsync(() => partials.Count == 1);

        channel.QueueServerFrame(BuildServerContent(@case.Fragment, partial: true, turnComplete: false));
        await WaitForAsync(() => finals.Count == 1 || partials.Count == 2, timeoutMs: 2000);

        return finals.Count == 1 ? "Flush" : "Append";
    }

    // ----- Helpers -----

    private static string[] Phrase(string name) => CandidatePhrases.First(p => p.Name == name).Tokens;

    private static bool StartsWithPhrase(string fragment, string[] phraseTokens)
    {
        string[] tokens = fragment
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim(',', '.', '!', '?', ';', ':'))
            .Where(t => t.Length > 0)
            .ToArray();
        if (tokens.Length < phraseTokens.Length)
        {
            return false;
        }

        for (int i = 0; i < phraseTokens.Length; i++)
        {
            if (!tokens[i].Equals(phraseTokens[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string FirstWord(string text) => text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    private static string BuildServerContent(string? text, bool partial, bool turnComplete)
    {
        string textNode = text is null
            ? string.Empty
            : $"\"text\":\"{text.Replace("\"", "\\\"")}\"";
        string partialNode = partial ? "\"partial\":true," : string.Empty;
        return $$"""
        {
          "serverContent": {
            "outputTranscription": { {{textNode}} },
            {{partialNode}}
            "turnComplete": {{(turnComplete ? "true" : "false")}}
          }
        }
        """;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        int elapsed = 0;
        const int stepMs = 10;
        while (!condition() && elapsed < timeoutMs)
        {
            await Task.Delay(stepMs);
            elapsed += stepMs;
        }
    }
}
