# Gemini Streaming-Caption Segmentation Study

Status: **COMPLETE (2026-08-14, measurement only).** Root cause identified; segmentation-guard
unit-test matrix executed (48 runs); **decision: production gate unchanged**; phrase-level idiom
guard remains a **candidate** pending a broader corpus-driven validation. No production code changed.

## One-paragraph result

20 real-Gemini runs (10 primary on `english_sustained_90s.wav` ×120 s, 10 secondary on
`english_boundary_30s.wav` ×60 s). Gemini streams ~1 fragment/s (median gap 1000 ms, p90 1244 ms);
the app pipeline adds **zero** latency (FINAL→COMMIT→RENDER all 0 ms median / 1 ms p90); first
visible caption median **8.72 s** (primary) / **9.71 s** (secondary), dominated by STT first-FINAL +
Gemini first-token. FINAL via: sentence 89 %, idle 9.4 %, tail 1.6 %.

## Root cause of residual false splits

The v0.5.40 guard `terminal && !restate && !lowercase` (`GeminiLiveTranslateEngine.cs`, flush gate)
only catches **lowercase** continuations. Capitalized mid-sentence continuations (`Hindi Lunes.`,
`At pagkatapos`, `Sige.`) still split:

- Same-audio "…Friday, not Monday" split in **6/10 runs**.
- "…plan. At pagkatapos…" split in **5/10**.
- Fragmentary captions (len<15) rise to **9.8 %** on the boundary-stress clip (vs 2.2 % primary).
- First caption starts mid-sentence in 5/10 secondary runs.

**Under-segmentation (two real sentences joined) also occurs**, so a more aggressive guard is NOT an
automatic win.

## Segmentation-guard unit-test matrix (executed 2026-08-14, 48 runs: 41 PASS / 7 FAIL)

The agreed decision-gate suite (`tests/UniversalCaptions.Speech.Gemini.Tests/
SegmentationGuardMatrixTests.cs`, measurement only — no production code changed) drives the CURRENT
flush gate with 24 annotated cases across capitalization × fragment length (<15 / 15–30 / >30) ×
boundary truth. Results:

- **Cat 1 — lowercase continuation → APPEND (3 cases): PASS.** The v0.5.40 lowercase guard holds.
- **Cat 2 — capitalized continuation idiom → APPEND (7 cases): RED (the measured gap).** The gate
  FLUSHes all seven: `Hindi Lunes.` (len 12, the retained real 6/10-run regression), `At pagkatapos…`
  (real 5/10-run), `At makinig…` (real primary 2/10-run), `Kaya kailangan…`, `Sige, gawin…`,
  `Pero pagkatapos…`, `Dahil dito…`.
- **Cat 3 — bare capitalized starter, deliberately ambiguous (8 cases, 4 pairs): PASS.** Both members
  of each starter pair (`At` / `Kaya` / `Sige,` / `Hindi`) produce the **identical** gate decision
  (FLUSH) — a bare-starter allowlist would flip the new-sentence reading of each pair, i.e.
  **over-join**. The fragment alone cannot decide these; they are provably indistinguishable.
- **Cat 4 — genuine new sentence → FLUSH (6 cases): PASS.** No over-join on content-word or
  connector-word sentence starts.

**Conclusion (decision, user-approved):** the dangerous axis is not capitalization — it is
**insufficient context**. A simple `At|Kaya|Sige|Hindi → APPEND` allowlist is **unsafe** (Cat 3). The
seven Cat 2 cases are **known defects with a candidate mitigation** (a phrase-level idiom guard such
as `At pagkatapos` / `Kaya kailangan` / `Sige, gawin` / `Pero pagkatapos` / `Dahil dito` is technically
viable for exactly those observed patterns), but they are **not sufficient evidence to ship the
mitigation**. **The production gate stays unchanged.** A second, smaller **corpus-driven validation**
(observed idioms → APPEND; same idioms in genuine sentence-start contexts → FLUSH; unseen variants;
short fragments incl. `Hindi Lunes.`; punctuation/capitalization variations; English equivalents;
negative over-join cases) must establish **false-split reduction − over-join cost** before any guard
touches production.

Recommended state: **investigation COMPLETE → matrix COMPLETE → root cause confirmed → production
gate unchanged → phrase-level guard remains a candidate pending broader corpus validation.**

## Origin (the v0.5.40 defect being investigated)

Gemini streaming segmentation can emit a mid-sentence fragment right after a `FINAL`, e.g.
`FINAL "Nabasa mo na ang job description."` → `FRAG init " at halos tugma"` → `FINAL "at halos tugma
ito."`. Diagnosis (evidence-based, traced against `GeminiLiveTranslateEngine`):

1. **Gemini (primary, non-deterministic):** run 1 delivered `" description."` as a fragment carrying
   a mid-sentence period, then streamed the true continuation `" at halos tugma"` (leading whitespace
   + lowercase) as a new `ServerContent` fragment; the **same audio in run 2** produced one clean
   FINAL, confirming Gemini's segmentation is not stable across runs.
2. **Our engine (secondary):** the flush gate commits a FINAL whenever a new fragment arrives while
   the accumulator ends in punctuation — no continuation heuristic (only cumulative restatements are
   rejected); the premature flush happens before the fragment reaches `Accumulate`/
   `IsCumulativeRestatement`, so classification is not the cause.
3. **Idle timer: not responsible** — the 1.5 s ARM-IDLE armed at 2.448 s would fire ~3.95 s; the new
   fragment arrived at 3.701 s and the flush was `reason=sentence-boundary`. Run 1 has 0 idle-timeout
   FINALs without terminal punctuation.

The v0.5.40 lowercase-continuation guard (Option A) fixed the lowercase case; the capitalized
continuations above are the measured residual.

## Methodology

- Telemetry via temporary `UC_GEMINI_SEG_TRACE=1` chunk events (FRAG/BOUNDARY/PARTIAL/FINAL/ACTIVE/
  COMMIT/RENDER) added to `GeminiLiveTranslateEngine`/`CaptionPipeline`/`CaptionOverlayWindow`
  (`GeminiSegmentTrace`, fully removed after the study).
- UIA first-caption anchors give the first *visible* caption time.
- Harness: `uc_gemini_seg_study.ps1` (untracked). Analyzer: `gemini_seg_study\uc_gemini_seg_analyze.ps1`.
- Analysis CSVs (untracked): `gemini_seg_study\study_summary.csv`, `analysis_runs.csv`,
  `analysis_finals.csv`, per-run `*_trace.log`/`*_stderr.log`.

## Timeline detail (app pipeline adds zero latency)

- Fragment cadence: median 1000 ms, p90 1244 ms, mean 1019 ms (n=1816).
- Segment produce (first FRAG→FINAL): median ~2000 ms, p90 ~5005 ms.
- FINAL→COMMIT: 0 ms median / 1 ms p90. COMMIT→RENDER(history): 0 ms median / 1 ms p90.
- Idle finals commit ~1.5 s after last fragment (median produce 1512 ms); sentence finals commit
  2 ms (p90 3 ms) after the triggering fragment.
- First visible caption: primary median 8.72 s (p90 9.83 s); secondary median 9.71 s (p90 9.99 s).

## Evidence

- `docs/reports/BENCHMARK_REPORT.md` — "Gemini Streaming-Caption Segmentation Study" section
  (methodology, results tables, key findings, attribution, recommendation).
- `docs/implementation/ROADMAP.md` — v0.5.40 entry (CLOSED) + unit-test matrix (CLOSED, decision).
- `docs/implementation/PROJECT_STATUS.md` — v0.5.40 investigation + matrix COMPLETE entry.
- `tests/UniversalCaptions.Speech.Gemini.Tests/SegmentationGuardMatrixTests.cs` — the 48-run
  decision-gate matrix suite (measurement only, committed).
- `gemini_seg_study\` — untracked evidence (traces, CSVs, analyzer script).
- `gemini_seg_trace.log` / `gemini_seg_trace_run2.log` / `gemini_seg_app_stderr.log` /
  `acceptance-gemini-seg-trace.ps1` — early repro evidence (untracked).
- Gate at close: 651/651 tests, Release 0 warnings/0 errors, `dotnet format` clean.
