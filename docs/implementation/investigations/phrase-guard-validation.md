# Corpus-Driven Phrase-Guard Validation (Gemini segmentation)

Status: **COMPLETE (2026-08-14) — decision: INSUFFICIENT EVIDENCE (do not ship).** No production code
changes; v0.5.40 gate untouched; no v0.5.41; the 49 matrix tests unchanged. Follows the closed v0.5.40
study + matrix (commit `d9e24ab`); this was a separate investigation with its own evidence and decision
gate.

## Objective

Determine whether a **phrase-level continuation guard** can reduce the 7 observed Cat 2 false splits
(capitalized mid-sentence continuations like `Hindi Lunes.` / `At pagkatapos…`) **without introducing
unacceptable over-joins**, before any production code changes. The single metric that decides:

> **false-split reduction − over-join cost**

## Hard constraints

- **No production code changes.**
- Do **not** modify the current v0.5.40 gate (`terminal && !restate && !lowercase`).
- Do **not** create v0.5.41 yet.
- Existing **49 matrix tests must remain green** (`SegmentationGuardMatrixTests.cs`).
- `dotnet test` must remain green.
- The corpus is **validation evidence**, not a way to force an implementation.
- Cat 3 ambiguity cases stay as **mandatory negative cases** (a bare starter can be either a
  continuation or a new sentence — never allowlist a bare `At|Kaya|Sige|Hindi`).

## Candidate phrase patterns under test

| # | Phrase | Observed Cat 2 source |
|---|---|---|
| 1 | `At pagkatapos` | real 5/10-run split |
| 2 | `At makinig` | real primary 2/10-run split |
| 3 | `Kaya kailangan` | user matrix idiom |
| 4 | `Sige, gawin` | user matrix idiom |
| 5 | `Pero pagkatapos` | user matrix idiom |
| 6 | `Dahil dito` | user matrix idiom |
| 7 | `Hindi <fragment>` | real 6/10-run split (`Hindi Lunes.`, len 12) — special: bare `Hindi` is ambiguous |

## What the corpus must answer

For **every** candidate, the corpus must provide BOTH:

- **Continuation examples → expected APPEND** (the false split the guard would fix), and
- **genuine sentence-start examples → expected FLUSH** (the over-join the guard must not cause),

including short fragments such as **`Hindi Lunes.`**. Corpus axes (per the agreed matrix scope):

- observed continuation idioms → APPEND
- same idiom in genuine sentence-start contexts → FLUSH
- unseen variants of the same construction
- short fragments (esp. `Hindi Lunes.`)
- punctuation variations
- capitalization variations
- English equivalents where applicable
- negative over-join cases designed to expose over-joining

## Method (measurement only)

1. A **test-side** candidate guard (`PhraseGuardCorpusValidationTests.cs`) implements the phrase
   logic in the test project — NOT in production. It is evaluated against a labeled corpus.
2. Each corpus case is annotated with the semantic boundary truth (continuation / genuine new
   sentence) derived from the real study evidence + the agreed matrix.
3. For each candidate phrase, the suite measures on the corpus:
   - **false-split reduction** = continuation cases the guard would correctly APPEND
   - **over-join cost** = genuine sentence-start cases the guard would wrongly APPEND
   - **net** = reduction − cost
4. The 7 Cat 2 observed cases and the 8 Cat 3 ambiguous pairs must be included; the suite must stay
   green (the corpus is evidence, not an implementation).

## Decision gate

At the end, only three legitimate outcomes:

1. **Ship candidate** — evidence shows meaningful false-split reduction with acceptable over-join rate.
   ONLY this outcome authorizes a production implementation.
2. **Reject candidate** — over-joining is too costly.
3. **Insufficient evidence** — corpus needs expansion before a decision.

## Results (measured 2026-08-14)

`tests/UniversalCaptions.Speech.Gemini.Tests/PhraseGuardCorpusValidationTests.cs` — 11 tests, corpus of 43
cases. Baseline = real engine gate driven per case (measured, never assumed); candidate guard = test-side
only. Full suite 711/711 green (700 + 11 new); the 49 matrix tests stay green.

### Baseline (current v0.5.40 gate, measured per case)

- All 7 observed Cat 2 continuations FLUSH → **the gap is real** (regression pins in the suite).
- All 8 Cat 3 ambiguous pairs FLUSH both members → lexical separation impossible at the starter word.
- All genuine new-sentence cases FLUSH; the lowercase continuation (LC-01) still APPENDs → no regression.
- Observed: `Hindi Lunes.` (len 12) FLUSH, `At pagkatapos…` FLUSH, `At makinig…` FLUSH.

### Phrase-guard metric (reduction − over-join), per candidate

| Phrase | reduction | over-join | net | over-joined cases |
|---|---|---|---|---|
| `At pagkatapos` | 6 | 2 | **+4** | P-NEW-01, P-NEW-02 |
| `Sige, gawin` | 3 | 1 | **+2** | P-NEW-04 |
| `At makinig` | 2 | 1 | **+1** | P-NEW-07 |
| `Kaya kailangan` | 2 | 1 | **+1** | P-NEW-03 (IDENTICAL surface to the fix) |
| `Pero pagkatapos` | 2 | 1 | **+1** | P-NEW-05 |
| `Dahil dito` | 2 | 1 | **+1** | P-NEW-06 |
| `Hindi <fragment>` | 3 | 2 | **+1** | P-NEW-08, C3d-01 (same prefix as the fix) |
| `And then` (en) | 1 | 1 | **0** | EN-02 |
| `So we need` (en) | 1 | 1 | **0** | EN-04 |
| `But then` (en) | 1 | 1 | **0** | EN-06 |
| `Not` (en) | 1 | 1 | **0** | EN-08 |

Negative control: the rejected bare `at|kaya|sige|hindi` allowlist over-joins **8** genuine new
sentences — strictly worse than every phrase guard (max 2). The phrase guard is the safer direction.

### Reading of the numbers

- Every Tagalog phrase guard has **positive net** on this corpus; `At pagkatapos` is the strongest (+4).
- The multi-word guards are **specific**: `At bukas…`, `Kaya narito…`, `Sige, magsisimula…` are NOT caught
  (unit-pinned in `PhraseMatcher_MatchesOnlyTheExactPhrase`) — this is the concrete improvement over the
  bare-starter allowlist.
- Two irreducible-ambiguity pairs are proven: `Kaya kailangan nating magmadali.` is both the continuation
  fix (P-C2-04) and a genuine new-sentence over-join (P-NEW-03) with **identical surface**; the `Hindi`
  prefix fixes `Hindi Lunes.` but over-joins `Hindi ko alam kung saan ito.`. A lexical guard provably
  cannot separate these — the cost is real even where net is +1.
- **English equivalents all net 0**: on the en side the idiom's reduction exactly cancels its over-join,
  i.e. en continuations and en sentence-starts with the same idiom are equally frequent in this corpus.

### Decision — INSUFFICIENT EVIDENCE, do not ship (2026-08-14, user decision gate)

**The phrase guard is NOT shipped.** The validation proved the guard's *mechanics* but did **not**
establish the real-world over-join cost — and the two same-surface ambiguities show lexical matching can
produce the wrong result even when the phrase looks strong. The corpus has **constructed negative
cases**, so an aggregate reduction/over-join number is not enough to authorize shipping.

**Kept unchanged:** current production gate; the 49 matrix tests; 711/711 green; no v0.5.41. The phrase
guard remains a **candidate**, not an approved implementation.

#### Conclusions the evidence DID establish

| Claim | Verdict |
|---|---|
| Bare-word allowlist (`at\|kaya\|sige\|hindi → APPEND`) | **Reject — demonstrably unsafe** (over-joins 8 genuine sentence starts). |
| English equivalents (`And then`/`So we need`/`But then`/`Not`) | **No net benefit** (all net 0). |
| Phrase-level guard | **Technically reduces** the observed Cat 2 failures (+4 best for `At pagkatapos` on the constructed corpus). |
| Same-surface ambiguity (`Kaya kailangan nating magmadali.`, `Hindi` prefix) | **Real and irreducible with lexical information alone.** |
| Frequency-weighted real-world cost | **Unknown** — the deciding unknown. |

**Do not keep expanding the lexical phrase list** until the frequency question is answered — that would
optimize the heuristic before knowing whether the heuristic is appropriate.

#### What would justify Ship (the next evidence pass)

A **naturally occurring annotated corpus**, not more hand-constructed examples. Per candidate phrase,
measure the rate metrics:

```text
false splits prevented
-----------------------
total applicable continuation boundaries

and

false joins introduced
-----------------------
total applicable sentence boundaries
```

and report the **frequency-weighted cost**, not merely the count of examples. Example of the trap: if
`At pagkatapos` occurs 100 times naturally — 70 genuine continuations, 30 genuine sentence starts — and
the guard appends all 100, the apparent continuation win is misleading because it creates **30
over-joins**. `At pagkatapos`'s constructed +4 net survives real sentence-start frequency **only if**
genuine sentence starts beginning with that idiom are rare in natural usage.

## Evidence

- `tests/UniversalCaptions.Speech.Gemini.Tests/PhraseGuardCorpusValidationTests.cs` — corpus + metrics.
- `docs/implementation/investigations/gemini-segmentation.md` — prior study + 48-run matrix decision.
- `docs/reports/BENCHMARK_REPORT.md` — Gemini Streaming-Caption Segmentation Study section.
