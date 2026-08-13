using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Translation;
using UniversalCaptions.Speech.Gemini;
using Xunit.Abstractions;

namespace UniversalCaptions.Speech.Gemini.Tests;

/// <summary>
/// Segmentation-guard decision matrix (v0.5.40 follow-up, agreed 2026-08-14). This is a
/// DECISION-GATE measurement suite, not a guard implementation. Each case drives the CURRENT
/// <see cref="GeminiLiveTranslateEngine"/> flush gate
/// (<c>flushBoundary = terminal &amp;&amp; !restate &amp;&amp; !lowercase</c>,
/// <c>GeminiLiveTranslateEngine.HandleServerContent</c>) with an annotated semantic boundary and
/// records the gate's actual decision.
/// </summary>
/// <remarks>
/// <para>
/// The suite is split into two concepts, both of which must stay GREEN (repo rule: <c>dotnet test</c>
/// must pass after every change):
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       <b>Contract tests</b> (<see cref="Status.Contract"/>) assert the behavior the current
///       production implementation is supposed to guarantee: Cat 1 lowercase continuation → APPEND,
///       Cat 3 bare-starter ambiguity → current FLUSH (both members identical), Cat 4 genuine
///       sentence → FLUSH.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Known-gap evidence tests</b> (<see cref="Status.KnownGap"/>, the 7 Cat 2 cases) assert the
///       CURRENT gate behavior as a regression pin (<c>CurrentExpected</c>), while the metadata records
///       the desired future behavior (<c>DesiredExpected</c>) and that the production gate has
///       deliberately NOT been changed yet. These are intentionally passing tests that document a
///       known segmentation limitation — NOT skipped, NOT failing.
///     </description>
///   </item>
/// </list>
/// <para>
/// Decision (2026-08-14, user-approved): <b>production gate unchanged</b>. A bare
/// <c>At|Kaya|Sige|Hindi → APPEND</c> allowlist is unsafe (Cat 3 demonstrates a starter can be either a
/// continuation or a new sentence — the fragment alone cannot decide). The seven Cat 2 cases are known
/// defects with a <b>candidate</b> mitigation (phrase-level idiom guard) but not sufficient evidence to
/// ship it. When the phrase-level guard is eventually implemented, flip the 7 Cat 2
/// <c>CurrentExpected</c> from FLUSH to APPEND (they then become ordinary regression tests). Cat 3 must
/// remain in the suite unchanged — it protects against the dangerous bare-starter allowlist.
/// </para>
/// <para>
/// Length is a constraint, never the decision: the rule under test is "short + continuation evidence →
/// APPEND", not "short → APPEND". <c>Hindi Lunes.</c> (len 12) is retained as a regression case
/// directly from the real 20-run failure evidence.
/// </para>
/// </remarks>
public sealed class SegmentationGuardMatrixTests
{
    private const string ApiKey = "test-api-key";
    private const string Model = "models/gemini-3.5-live-translate-preview";
    private const string Target = "tl";

    private readonly ITestOutputHelper _output;

    public SegmentationGuardMatrixTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Annotated semantic boundary for a matrix case.</summary>
    public enum Boundary { Continuation, NewSentence }

    /// <summary>Whether the fragment text alone carries enough signal for a lexical guard to decide.</summary>
    public enum Signal { Separable, Ambiguous }

    /// <summary>The gate's possible decisions.</summary>
    public enum GateDecision { Append, Flush }

    /// <summary>
    /// Contract: the behavior the current production implementation is supposed to guarantee.
    /// KnownGap: current behavior deliberately kept while <see cref="MatrixCase.DesiredExpected"/>
    /// records the desired future behavior (the gate has not been changed yet).
    /// </summary>
    public enum Status { Contract, KnownGap }

    public sealed record MatrixCase(
        string Id,
        string Category,
        string Accumulator,
        string Fragment,
        Boundary BoundaryTruth,
        Signal Signal,
        GateDecision CurrentExpected,
        GateDecision? DesiredExpected,
        Status Status,
        string Evidence);

    private static readonly MatrixCase[] Matrix = new MatrixCase[]
    {
        // ----- Cat 1: lowercase continuation → APPEND (Contract; gate handles today) -----
        new("C1-01", "Cat 1 lowercase continuation", "Hello world.", " and next...", Boundary.Continuation,
            Signal.Separable, GateDecision.Append, GateDecision.Append, Status.Contract,
            "Option-A matrix: lowercase continuation"),
        new("C1-02", "Cat 1 lowercase continuation", "Hello world.", " at halos tugma", Boundary.Continuation,
            Signal.Separable, GateDecision.Append, GateDecision.Append, Status.Contract,
            "v0.5.40 repro fragment (lowercase)"),
        new("C1-03", "Cat 1 lowercase continuation", "Bago tayo magsimula, hayaan niyo akong i-review ang plano.", " at pagkatapos",
            Boundary.Continuation, Signal.Separable, GateDecision.Append, GateDecision.Append, Status.Contract,
            "real trace secondary-run01: lowercase append, gate correct"),

        // ----- Cat 2: capitalized continuation idiom → current FLUSH is a KNOWN GAP (Desired: APPEND) -----
        new("C2-01", "Cat 2 capitalized continuation (KNOWN GAP)", "Tandaan, ang deadline ay Biyernes.", " Hindi Lunes.",
            Boundary.Continuation, Signal.Separable, GateDecision.Flush, GateDecision.Append, Status.KnownGap,
            "REAL 6/10-run false split; fragment len 12 (<15) — regression"),
        new("C2-02", "Cat 2 capitalized continuation (KNOWN GAP)", "Bago tayo magsimula, hayaan niyo akong i-review ang plano.", " At pagkatapos ay maaari tayong magtanong.",
            Boundary.Continuation, Signal.Separable, GateDecision.Flush, GateDecision.Append, Status.KnownGap,
            "REAL 5/10-run false split; phrase idiom 'At pagkatapos'"),
        new("C2-03", "Cat 2 capitalized continuation (KNOWN GAP)", "Kailangan nating maging malinaw sa lahat.", " At makinig nang mabuti bago tayo magpatuloy.",
            Boundary.Continuation, Signal.Separable, GateDecision.Flush, GateDecision.Append, Status.KnownGap,
            "REAL primary 2/10-run split; 'At makinig' continuation"),
        new("C2-04", "Cat 2 capitalized continuation (KNOWN GAP)", "Kailangan nating tapusin ito ngayon.", " Kaya kailangan nating magmadali.",
            Boundary.Continuation, Signal.Separable, GateDecision.Flush, GateDecision.Append, Status.KnownGap,
            "user matrix: 'Kaya kailangan' continuation idiom"),
        new("C2-05", "Cat 2 capitalized continuation (KNOWN GAP)", "Maaari na tayong magpatuloy.", " Sige, gawin natin iyon ngayon.",
            Boundary.Continuation, Signal.Separable, GateDecision.Flush, GateDecision.Append, Status.KnownGap,
            "user matrix: 'Sige, gawin natin' continuation idiom"),
        new("C2-06", "Cat 2 capitalized continuation (KNOWN GAP)", "Dapat tayong maghintay ng kaunti.", " Pero pagkatapos, titingnan natin ito.",
            Boundary.Continuation, Signal.Separable, GateDecision.Flush, GateDecision.Append, Status.KnownGap,
            "user matrix: 'Pero pagkatapos' contrastive continuation"),
        new("C2-07", "Cat 2 capitalized continuation (KNOWN GAP)", "Nahuli tayo sa trapiko kanina.", " Dahil dito, hindi tayo nakarating.",
            Boundary.Continuation, Signal.Separable, GateDecision.Flush, GateDecision.Append, Status.KnownGap,
            "user matrix: 'Dahil dito' causal continuation"),

        // ----- Cat 3: bare capitalized starter — deliberately ambiguous (Contract: current FLUSH; desired context-dependent) -----
        new("C3a-01", "Cat 3 bare-starter ambiguity", "Natapos na ang plano.", " At bukas magsisimula tayo.",
            Boundary.NewSentence, Signal.Ambiguous, GateDecision.Flush, null, Status.Contract,
            "user matrix: 'At' new-sentence reading"),
        new("C3a-02", "Cat 3 bare-starter ambiguity", "Natapos na ang plano.", " At pagkatapos ay umalis tayo.",
            Boundary.Continuation, Signal.Ambiguous, GateDecision.Flush, null, Status.Contract,
            "user matrix: 'At' continuation reading — same starter, different context"),
        new("C3b-01", "Cat 3 bare-starter ambiguity", "Kailangan nating tapusin ito ngayon.", " Kaya sinimulan namin ang bagong proyekto.",
            Boundary.NewSentence, Signal.Ambiguous, GateDecision.Flush, null, Status.Contract,
            "user matrix: 'Kaya' new-sentence reading"),
        new("C3b-02", "Cat 3 bare-starter ambiguity", "Kailangan nating tapusin ito ngayon.", " Kaya narito tayo ngayon.",
            Boundary.Continuation, Signal.Ambiguous, GateDecision.Flush, null, Status.Contract,
            "user matrix: 'Kaya' continuation reading — same starter, different context"),
        new("C3c-01", "Cat 3 bare-starter ambiguity", "Tapos na ang pulong.", " Sige, magsisimula na tayo sa susunod.",
            Boundary.NewSentence, Signal.Ambiguous, GateDecision.Flush, null, Status.Contract,
            "user matrix: 'Sige' new-utterance reading"),
        new("C3c-02", "Cat 3 bare-starter ambiguity", "Tapos na ang pulong.", " Sige, gawin natin iyon mamaya.",
            Boundary.Continuation, Signal.Ambiguous, GateDecision.Flush, null, Status.Contract,
            "user matrix: 'Sige' continuation reading — same starter, different context"),
        new("C3d-01", "Cat 3 bare-starter ambiguity", "Nakita ko na ang dokumento.", " Hindi ko alam kung saan ito.",
            Boundary.NewSentence, Signal.Ambiguous, GateDecision.Flush, null, Status.Contract,
            "user matrix: 'Hindi' new-sentence reading"),
        new("C3d-02", "Cat 3 bare-starter ambiguity", "Nakita ko na ang dokumento.", " Hindi ito ang tamang bersyon.",
            Boundary.Continuation, Signal.Ambiguous, GateDecision.Flush, null, Status.Contract,
            "user matrix: 'Hindi' continuation reading — same starter, different context"),

        // ----- Cat 4: genuine new sentence → FLUSH (Contract; gate handles today) -----
        new("C4-01", "Cat 4 genuine new sentence", "Hello world.", " This is new...", Boundary.NewSentence,
            Signal.Separable, GateDecision.Flush, GateDecision.Flush, Status.Contract,
            "Option-A matrix: genuine new sentence"),
        new("C4-02", "Cat 4 genuine new sentence", "Hello world.", " Nabasa ko...", Boundary.NewSentence,
            Signal.Separable, GateDecision.Flush, GateDecision.Flush, Status.Contract,
            "Option-A matrix: genuine new sentence"),
        new("C4-03", "Cat 4 genuine new sentence", "Hello world.", " The next step is to begin.", Boundary.NewSentence,
            Signal.Separable, GateDecision.Flush, GateDecision.Flush, Status.Contract,
            "user matrix: content-word start"),
        new("C4-04", "Cat 4 genuine new sentence", "Hello world.", " Yesterday we finished the plan.", Boundary.NewSentence,
            Signal.Separable, GateDecision.Flush, GateDecision.Flush, Status.Contract,
            "user matrix: content-word start despite connector-like word"),
        new("C4-05", "Cat 4 genuine new sentence", "Magandang umaga sa lahat.", " Kumusta ka", Boundary.NewSentence,
            Signal.Separable, GateDecision.Flush, GateDecision.Flush, Status.Contract,
            "existing engine test analog"),
        new("C4-06", "Cat 4 genuine new sentence", "Bago tayo magsimula, hayaan niyo akong i-review ang plano.", " Tandaan, ang deadline ay Biyernes.",
            Boundary.NewSentence, Signal.Separable, GateDecision.Flush, GateDecision.Flush, Status.Contract,
            "REAL secondary-run02 correct new-sentence flush (' Tandaan,' → flush)"),
    };

    public static IEnumerable<object[]> MatrixData => Matrix.Select(c => new object[] { c });

    /// <summary>
    /// Drives the current flush gate with <paramref name="case"/> and returns the gate's decision:
    /// FLUSH when a FINAL was committed immediately, APPEND otherwise (the fragment joined the same
    /// accumulator).
    /// </summary>
    private static async Task<GateDecision> RunGateAsync(MatrixCase @case)
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

        return finals.Count == 1 ? GateDecision.Flush : GateDecision.Append;
    }

    /// <summary>
    /// Every matrix case asserts the gate produces <see cref="MatrixCase.CurrentExpected"/>. For
    /// Contract cases that is the guaranteed behavior (Cat 1 APPEND, Cat 3 current FLUSH, Cat 4 FLUSH).
    /// For the 7 Cat 2 KnownGap cases the current gate FLUSHes — a deliberate regression pin that keeps
    /// the suite GREEN while the production gate stays unchanged. The gap metadata (DesiredExpected =
    /// APPEND) is the record; see <see cref="KnownGapCases_RecordDesiredFutureBehavior"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(MatrixData))]
    public async Task Cases_CurrentGate_ProducesCurrentExpected(MatrixCase @case)
    {
        GateDecision actual = await RunGateAsync(@case);

        _output.WriteLine(
            $"[{@case.Id}] {FirstCharKind(@case.Fragment)} len={@case.Fragment.Trim().Length} " +
            $"annotation={@case.BoundaryTruth} currentExpected={@case.CurrentExpected} " +
            $"desiredExpected={(object?)@case.DesiredExpected ?? "context-dependent"} " +
            $"status={@case.Status} actual={actual} evidence={@case.Evidence}");

        Assert.True(
            actual == @case.CurrentExpected,
            $"{@case.Id}: current gate produced {actual}, expected current behavior {@case.CurrentExpected}. " +
            $"Desired future behavior: {@case.DesiredExpected?.ToString() ?? "context-dependent"}. {@case.Evidence}");
    }

    /// <summary>
    /// The 7 Cat 2 KnownGap cases must record a desired future behavior that DIFFERS from the current
    /// expected (FLUSH today, APPEND desired). This is the explicit "the suite knows this is wrong from
    /// the UX/segmentation perspective, but the production gate has deliberately not been changed yet"
    /// statement.
    /// </summary>
    [Fact]
    public void KnownGapCases_RecordDesiredFutureBehavior()
    {
        MatrixCase[] gaps = Matrix.Where(c => c.Status == Status.KnownGap).ToArray();

        Assert.Equal(7, gaps.Length);
        Assert.All(gaps, c =>
        {
            Assert.True(
                c.DesiredExpected is GateDecision desired && desired != c.CurrentExpected,
                $"{c.Id} ({c.Evidence}): KnownGap must record a DesiredExpected that differs from CurrentExpected.");
        });
    }

    /// <summary>
    /// Cat 3 bare-starter pairs: both members (the new-sentence and the continuation reading of the
    /// same starter) must produce the SAME gate decision today — proving the current lexical guard
    /// cannot distinguish them. This is the protection against the dangerous bare-starter allowlist
    /// (<c>At|Kaya|Sige|Hindi → always APPEND</c>), which would over-join the new-sentence reading.
    /// Cat 3 must remain in the suite unchanged.
    /// </summary>
    [Theory]
    [MemberData(nameof(MatrixData))]
    public async Task AmbiguousPairs_CurrentGate_ProducesSameDecisionForBothMembers(MatrixCase @case)
    {
        if (@case.Signal != Signal.Ambiguous)
        {
            return;
        }

        GateDecision actual = await RunGateAsync(@case);
        _output.WriteLine(
            $"[{@case.Id}] {FirstCharKind(@case.Fragment)} len={@case.Fragment.Trim().Length} " +
            $"annotation={@case.BoundaryTruth} actual={actual} signal={@case.Signal} evidence={@case.Evidence}");

        // The member with the OPPOSITE boundary truth shares the same first word. Assert the gate
        // returns the identical decision for both — i.e. it is provably unable to separate them.
        var pair = Matrix.First(m =>
            m.Id != @case.Id
            && m.Signal == Signal.Ambiguous
            && FirstWord(m.Fragment).Equals(FirstWord(@case.Fragment), StringComparison.Ordinal)
            && m.BoundaryTruth != @case.BoundaryTruth);

        GateDecision pairActual = await RunGateAsync(pair);
        Assert.True(
            pairActual == actual,
            $"{@case.Id} ({@case.BoundaryTruth}) vs {pair.Id} ({pair.BoundaryTruth}): the lexical guard " +
            $"distinguished a bare-starter pair — annotate how, or it will over-join/under-join.");
    }

    // ----- Helpers (mirror GeminiLiveTranslateEngineTests) -----

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

    private static string FirstCharKind(string text)
    {
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            return char.IsLower(c) ? "lowercase" : char.IsUpper(c) ? "UPPERCASE" : "other";
        }

        return "empty";
    }

    private static string FirstWord(string text) => text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

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
