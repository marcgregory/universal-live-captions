# Universal Live Captions Roadmap

Last updated: 2026-08-14

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define the product roadmap and backlog — answers "What should be built?" |
| Scope | Completed work, in-progress work, sprint queue, future work, and blocked items |
| Audience | Engineering, Product |
| Owner | Engineering |
| Status | Active |
| Related Documents | [PRD.md](../PRD.md), [BUILD_PLAN.md](BUILD_PLAN.md), [PROJECT_STATUS.md](PROJECT_STATUS.md), [RELEASE_PLAN.md](RELEASE_PLAN.md) |

---

## Completed

- **Slice 0 — Repository baseline.** Repository bootstrapped with the enterprise-project-bootstrap framework (v3.0.0): governance, MVP documentation, ADRs, and project scaffolding in place. Build verified.
- **Slice 1 — Audio capture spike.** WASAPI loopback → PCM frames → diagnostic meter. Verified: build green (0 warnings), 66/66 tests pass, real-device capture recorded in `docs/reports/TEST_REPORT.md`. Success criterion met (detects and receives Windows system audio without VB-CABLE).
- **Slice 2 — STT spike.** Audio processor → streaming `ISpeechToTextEngine` → partial + final transcripts via local Whisper. Verified: 107/107 tests pass, build 0 warnings, real-model benchmark on four samples (clean/noisy/long/conversational) records streaming finals and discriminates model quality (OSR WER: base 4.9% vs tiny 16.0%); default model user-approved (**ggml-base**, tiny as low-resource fallback) in ADR-0003. Streaming finals resolve the Slice 1→2 handoff for the caption service.
- **Slice 3 — Translation spike.** Source transcript → `ITranslationEngine` → translated transcript. Verified: 128/128 tests pass (8 fake-engine + 13 fake-process + 7 contract), build 0 warnings, real Argos 1.11.0 end-to-end (direct `en→tl`/`ja→en`/`en→ja`, pivot `ja→tl` via `en`, offline/local), benchmark recorded (load/first, steady latency, throughput, working set, finals-stream ordering, quality), fresh-context review completed with fixes (stale-process recovery, unwrapped exceptions, Python crash path) and remaining items logged TD-013–TD-015. ADR-0006 pair/protocol selection resolved.
- **Slice 4 — Caption service.** `ICaptionService`, `CaptionLine`, `CaptionState`, `CaptionServiceOptions` in `UniversalCaptions.Core.Captions`; `CaptionService` in `UniversalCaptions.Captions` (Core-only, no WPF). Partial→active→final→committed transitions, optional background translation (failure preserves the source caption), ordering, duplicate prevention, bounded history, session lifecycle, cancellation. Verified: 168/168 tests pass (40 Captions with deterministic fake translation engines), build 0 warnings, format clean, no vulnerable packages; fresh-context review completed and findings fixed (snapshot history, deferred CTS disposal, atomic translation-start token, stale-translation identity guard, event-raising moved out of the translation catch). **Close-out approved 2026-08-01 — Slice 4 Completed.**
- **Slice 5 — Overlay + control window.** Always-on-top WPF caption overlay rendering `CaptionState` (active line + bounded history + translated caption) and a minimal control UI (translation on/off, source/target languages), consuming `ICaptionService` events via the dispatcher. `UniversalCaptions.App` is the DI composition root with `IOverlayService`, `CaptionOverlayWindow`, `CaptionPipeline`, `ControlWindow`, `AudioSourceLoader`, `TranslationGuard`; **209/209 tests passing** (36 new App tests), build 0 warnings, format clean, no vulnerable packages. Q1 display policy **resolved** (verbatim latest partial as the active line; committed finals as history; translated text replaces source when completed — PRD FR-5/FR-14). **Manual overlay/device verification completed 2026-08-01** (real capture → Whisper → overlay, interaction, lifecycle, error paths). **Real-Argos wiring verified end-to-end through the App 2026-08-01**: with the dev Argos venv recreated, the App spawned the Argos child process and committed overlay lines displayed real translated Tagalog (`tamad aso.`, `Ang mabilis na kayumangging sorra ay tumatalon sa ibabaw ng eruplano`, `IsTranslated = True`); also exercised the `ApplyTranslationSettings` guard fix. **Close-out 2026-08-01 — Slice 5 Completed.**
- **Slice 6 — End-to-end latency/accuracy baseline.** **Close-out 2026-08-01 — Slice 6 Completed.** Phase 1a (E2E latency metric + tests: `CaptionLine` translation timestamps + `CaptionPipeline.EndToEndLatencyUpdated` partial/final samples, **238/238 tests**), Phase 1b (parameterized STT benchmark + OFAT sweep of window/interval/`StabilityWindow` × base/tiny × jfk/OSR + shortlist), and Phase 1c (App-level SAPI E2E validation — baseline + shortlist × 3 runs each through the real App, loopback → Whisper → Argos en→tl → overlay, every run publishing real translated Tagalog) are all complete. Findings in `docs/reports/BENCHMARK_REPORT.md`, evidence in `docs/reports/TEST_REPORT.md`. **The validated baseline `base 8 s/1 s/st2` was promoted to the App default** (`StabilityWindow` 3→2 via `WhisperEngineOptions` + App + benchmark, one authoritative config; model default `ggml-base` unchanged per ADR-0003). Fresh-context review of the Phase 1a E2E metric code completed clean (close-out 2026-08-01). Latency winner `tiny/8/1/st2` (E2E final median 16.25 s incl. Argos cold start; warm last-final 7.45 s; STT 3.61 s; 18 translated finals). **Phase 2 real-app validation (YouTube/Chrome, VLC, Zoom) is deferred per user — a future reassessment pass, not a prerequisite for the defaults.**
- **Slice 10 — faster-whisper native streaming (C# VAD segment commit).** **Close-out 2026-08-05 — benchmark + real-App gate PASS.** New `FasterWhisperNativeStreamingEngine` behind `UC_STT_ENGINE=fasterwhisper-native` (additive; `fasterwhisper` keeps the windowed engine; ggml-base stays the production default). C# owns VAD/segment detection + buffering + when-to-decode; the existing faster-whisper worker wire protocol is unchanged. One FINAL per completed speech segment (no live partials). Committed WER **32.6%** vs the `fil-orig` reference (ggml-base 51.2%, faster-whisper full-file 31.1%); **FINAL-only (0 partials)**; **13.3 FINALs/120 s** (windowed faster-whisper 2/120 s); first real-App caption **15.2 s**; STT latency ~4 s behind segment end with no growing backlog; no `(Song)`/`(Subscribe)` hallucinations; last sentence committed at 285 s before the 289 s audio end (nothing dropped at Stop). **Decision gate: PASS — but NOT promoted to production default** (8 s segment cap can split sentences mid-word; ~4 s behind-segment-end latency worth tuning). faster-whisper stays **opt-in** (`fasterwhisper-native` = new native-streaming mode; `fasterwhisper` = existing windowed mode); ggml-base production default unchanged (frozen). Evidence: `docs/reports/TEST_REPORT.md` (Slice 10), CHANGELOG v0.5.19, Entry 11.

- **Slice 11 — native-streaming segment-boundary tuning.** **Close-out 2026-08-05 — decision: keep 8 s.** Controlled `sttnative` sweep at `MaxSegmentDuration` 8/10/12 s on the actual video audio vs the `fil-orig` reference (small int8, tl, hangover 0.7 s fixed, realtime feed with a timer-granularity fix): WER 32.6%/33.2%/30.0%, cadence 13.3/10.8/9.1 FINALs/120 s, mid-sentence splits 10/32 (31%) / 11/26 (42%) / 10/22 (45%). **Longer segments do NOT reduce mid-sentence splits** (fraction worsens; the cap still force-closes mid-sentence, each cut discards more content), cost ~46% responsiveness at 12 s, and add end-of-audio cap hallucinations (a `Pag-pag-pag…` stutter at 10 s, a truncated `tunog` at 12 s). Latency/backlog stays bounded at all three caps (~5 s behind segment end, decode < segment length). **Native engine's 8 s `MaxSegmentDuration` default is kept unchanged; no production or knob-default change; worker protocol / ggml-base / windowed engine untouched.** Real-App evidence for the kept default is the Slice 10 real-App run. Evidence: `docs/reports/BENCHMARK_REPORT.md` (Slice 11), `docs/reports/TEST_REPORT.md` (Slice 11), CHANGELOG v0.5.20, Entry 12.

- **Slice 12 — faster-whisper native-streaming live partials.** **Close-out 2026-08-05 — benchmark PASS.** Additive Chrome-Live-Caption-style live partials on the opt-in `fasterwhisper-native` engine: `SpeechSegmentDetector.TryGetPartial` (bounded trailing-window snapshot) + `FasterWhisperEngineOptions.PartialDecodeInterval` (default 0 = Slice 10/11 FINAL-only preserved) / `PartialDecodeWindow` (4 s) + cadence dispatch with at most one partial decode in flight/queued (`PartialTranscriptAvailable`); App knobs `UC_NATIVE_PARTIAL_INTERVAL` (1 s) / `UC_NATIVE_PARTIAL_WINDOW` (4 s); `sttnative` benchmark partial metrics + CSV partial table. **367/367 tests** (10 new), Release 0 warnings/0 errors. Controlled real-audio run (small int8, tl, hangover 0.7 s, max 8 s, translation OFF, `--partial-interval 1 --partial-window 4` on `uc_video_full_16k.wav` (288.79 s) vs `fil-orig`): first visible partial **5.59 s after speech onset** (vs first FINAL 15.0 s), **19.5 partials/120 s**, active line increments while speaking, FINAL stream **text-identical to Slice 11** (no accuracy regression, WER 33.19% in-harness), backlog **bounded** (plateau ~50 s vs 43 s FINAL-only), realtime-safe 1.18×. **Decision: PASS — ggml-base stays the production default; partials default off** (`PartialDecodeInterval = 0`), so production behavior is unchanged unless opted in. Tradeoffs: ~5 % wall + ~8 s tail-latency; rolling-4 s-window means the FINAL reveals earlier words. Evidence: `docs/reports/BENCHMARK_REPORT.md` (Slice 12), `docs/reports/TEST_REPORT.md` (Slice 12), CHANGELOG v0.5.21, Entry 13.

- **Entry 14 — production default promotion.** **Close-out 2026-08-05 — ADR-0008.** Product decision (user-approved): the production STT default is now **`fasterwhisper-native` + live partials** (partial interval 1 s, window 4 s, `MaxSegmentDuration` 8 s — the Slice 11 frozen cap). Engine selection extracted into the testable `SpeechEngineFactory` (default / `fasterwhisper-native` → native + partials; `ggml-base` → the original local-Whisper engine as the explicit fallback; `fasterwhisper` → the unchanged windowed engine). **No automatic runtime fallback** (deliberate — ADR-0003 no-silent-switch). Faster-whisper worker protocol, the windowed engine, ADR-0007, TD-002, and TD-005 untouched. 5 new `SpeechEngineFactoryTests`; full suite **372/372**, Release 0 warnings/0 errors, `dotnet format` clean. Rationale: Slice 12 PASS (Chrome-like partials, first visible 5.59 s, FINAL stream identical) + materially better Tagalog recognition (committed WER ~33% vs ggml-base 51.2%) + no 20–40 s backlog; documented costs (~5 % wall, ~8 s tail emit-lag, Python-worker dependency) accepted. Evidence: ADR-0008, Entry 14, CHANGELOG v0.5.22.

- **Entry 15 — overlay live-line integration.** **Complete 2026-08-06 — ADR-0008 follow-up.** The WPF overlay previously painted committed FINALs only (commit `7d1c057` "temporary diagnostic tracer" replaced Slice 7's active-line painting; `_activeBlock` never assigned). Now `CaptionOverlayWindow.UpdateCaptionItems` creates one mutable `_activeBlock`, rewrites it in place on later partials, removes it when `model.ActiveLine` is null; `ReconcileHistory` reuse-by-sequence and the `shouldUpdate` gate (no source flash while translation is pending) unchanged. `CaptionRenderIdentityTests` rewritten 4→6; full suite **374/374** (App 89), Release 0 warnings/0 errors, `dotnet format` clean. **Real-App smoke PASS** (Entry 14 checklist + overlay AC-1..AC-8): first visible partial ≈5.6 s after capture start; active line grows in place; FINAL freezes into history with no churn; Stop/Dispose leaves no stale partial; App CPU ~0–66% / worker ~0%; **en→tl Argos live-translated active line painted before commit** (no raw-English flash); tl→en confirmed documented-unsupported (stanza SBD) with graceful degradation. Evidence: Entry 15, `docs/reports/TEST_REPORT.md` (Entry 15), CHANGELOG v0.5.23.

- **Entry 16 — CPU optimization: decode-thread cap.** **Complete 2026-08-06.** The promoted path
  sustained **77.4% of the machine** in the STT worker (every partial/FINAL decode used all 12 cores).
  `UC_NATIVE_THREADS` env knob with **production default `Threads` = 4** (clamped [1, ProcessorCount])
  via `SpeechEngineFactory.CreateNative`; worker `--threads` wiring verified by test; `sttnative`
  gains `--threads`. Formal gate (12t vs 4t, real video audio, small int8 tl, partials 1/4 s): WER
  **33.2% both**, realtime **1.18× both**, **FINAL stream 100% text-identical**, latency/backlog
  comparable. Real-App CPU probe (default, speech + partials): STT worker system mean **77.4% → 31.6%**
  (max 88.2% → 37.6%), App ~1%, first caption 3.72 s, overlay producing. **382/382 tests** (8 new),
  Release 0 warnings/0 errors, `dotnet format` clean. Engine selection, worker protocol,
  segmentation/8 s cap, partials, overlay, translation untouched. Evidence: Entry 16, TEST_REPORT
  (Entry 16 close-out), BENCHMARK_REPORT (Entry 16 gate), CHANGELOG v0.5.24.

- **Segmentation-guard unit-test matrix — CLOSED 2026-08-14 (decision: production gate unchanged).**
  The agreed decision-gate suite (`SegmentationGuardMatrixTests.cs`, 48 runs: **41 PASS / 7 FAIL**,
  measurement only, no production code changed) drove the current flush gate with 24 annotated cases.
  Cat 1 lowercase continuation (3) → APPEND ✓ PASS; **Cat 2 capitalized continuation idiom (7) →
  FLUSH ✗ RED** (the measured v0.5.40 gap, incl. the retained `Hindi Lunes.` len-12 regression);
  Cat 3 bare-starter pairs (8) → both members identical (provably ambiguous, a bare
  `At|Kaya|Sige|Hindi → APPEND` allowlist is **unsafe** — it would over-join the new-sentence reading
  of each pair); Cat 4 genuine new sentence (6) → FLUSH ✓ PASS. **Decision:** the dangerous axis is
  insufficient context, not capitalization. The seven Cat 2 cases are known defects with a **candidate**
  mitigation (phrase-level idiom guard), but are not sufficient evidence to ship it. **Production gate
  stays unchanged.** A second, smaller **corpus-driven validation** must establish false-split reduction
  − over-join cost before any guard touches production. Evidence: investigations/gemini-segmentation.md,
  PROJECT_STATUS, TEST_REPORT.

## In Progress

- None currently. (The segmentation-guard unit-test matrix CLOSED 2026-08-14 — see Completed. The
  corpus-driven phrase-guard validation is a Future candidate, gated on a documented decision.)

## Completed (core done)

- **Final real-world acceptance — PASS (2026-08-06), project core-done.** Per user direction, the
  production default (`fasterwhisper-native` + live partials, `UC_NATIVE_THREADS=4`) was validated in
  continuous normal use: Release App + VLC + real WASAPI loopback, 300 s legs, per-poll CIM CPU + UIA
  overlay snapshots. Leg 1 Tagalog/translation-OFF: STT worker 31.8% of machine (max 37.6%), App 0.9%,
  first caption 3.27 s, 95 snapshots, clean exit, 0 orphans. Leg 2 English/en→tl looped: STT 33.5% (max
  37.1%) + Argos 4.2% (max 21.6%), App 1.3%, first caption 3.23 s, 129 snapshots, clean exit, 0 orphans.
  Overlay verified live (growing partials, FINAL freeze into bounded history, `EN || TL` badge, real
  Tagalog, Stop retains history). **382/382 tests**, Release 0 warnings/0 errors, `dotnet format` clean.
  Evidence: TEST_REPORT (final real-world acceptance), CHANGELOG v0.5.25, `acceptance_*` artifacts
  (untracked).

## Sprint Queue

- None currently. The segmentation-guard unit-test matrix is **CLOSED — decision recorded, production
  gate unchanged** (2026-08-14); the corpus-driven phrase-guard validation is a Future candidate (see
  Future).

---

### v0.5.40 — Gemini streaming-caption segmentation investigation + matrix (CLOSED 2026-08-14, no code change)

Investigation **complete**: root cause identified, 20-run real-Gemini study executed, evidence
recorded, tracer removed, tree clean. **Segmentation-guard unit-test matrix executed (48 runs:
41 PASS / 7 FAIL); decision: production gate unchanged.** No production change.

Separately tracked from the resolved v0.5.39 `goAway`/session-lifecycle fix (which is closed and
released — this is NOT a v0.5.39 defect). **Issue:** Gemini streaming segmentation can emit a
mid-sentence fragment right after a `FINAL`, e.g. `FINAL "Nabasa mo na ang job description."` →
`FRAG init " at halos tugma"` → `FINAL "at halos tugma ito."`. **Diagnosis (evidence-based, traced
against `GeminiLiveTranslateEngine`):**
- **Gemini (primary, non-deterministic):** in run 1 the service delivered `" description."` as a
  fragment carrying a **mid-sentence period**, then streamed the true continuation `" at halos
  tugma"` as a new `ServerContent` fragment (leading whitespace + lowercase = grammatical
  continuation, not a new sentence). The **same audio in run 2** produced one clean FINAL
  (`"Nabuod mo na ang job description at halos tugma ka nang perpekto."`), confirming Gemini's
  segmentation is not stable across runs.
- **Our engine (secondary):** `HandleServerContent`'s flush gate (GeminiLiveTranslateEngine.cs:434–440)
  flushes the accumulator to a FINAL whenever a new fragment arrives while the accumulator ends in
  punctuation — it has **no continuation heuristic** (only rejects cumulative restatements, not
  sentence continuations). The premature flush happens **before** the new fragment reaches
  `Accumulate`/`IsCumulativeRestatement`, so classification is not the cause.
- **Idle timer: not responsible** — the 1.5 s ARM-IDLE armed at 2.448 s would have fired ~3.95 s;
  the new fragment arrived at 3.701 s and the flush was `reason=sentence-boundary`. Run 1 has 0
  idle-timeout FINALs without terminal punctuation.
- **Reproduction evidence:** `gemini_seg_trace.log` lines 112–131 (cut) vs `gemini_seg_app_stderr.log`
  lines 1844–1862 (clean same-audio run). Evidence preserved untracked: `gemini_seg_trace.log` /
  `gemini_seg_trace_run2.log` / `gemini_seg_app_stderr.log` / `acceptance-gemini-seg-trace.ps1`.
**Candidate fixes considered (not implemented):** **Option A** continuation guard (accumulator ends
in punctuation but incoming fragment begins with whitespace + lowercase → append, not flush —
implemented as the v0.5.40 fix, which closed the lowercase case); **Option B** stronger linguistic
continuation heuristic (case-insensitive + conjunction/preposition set — the open candidate, gated
by the unit-test matrix); **Option C** remove the punctuation-based immediate flush and rely on
Gemini's explicit final/idle behavior (not preferred — punctuation gives useful responsiveness).

**Measured 2026-08-14 (20-run real-Gemini study, tracer removed after):** Gemini streams ~1
fragment/second (median gap 1000 ms, p90 1244 ms); the app pipeline adds zero latency (FINAL→COMMIT→
RENDER all 0 ms median / 1 ms p90); first visible caption median 8.72 s (primary) / 9.71 s
(secondary), dominated by STT first-FINAL + Gemini first-token — **no app-side latency to optimize.**
The v0.5.40 lowercase guard only catches lowercase continuations — capitalized mid-sentence
continuations (`Hindi Lunes.`, `At pagkatapos`, `Sige.`) still split: same-audio "…Friday, not
Monday" split in **6/10 runs**, "…plan. At pagkatapos…" in **5/10**. Fragmentary captions (len<15)
rise to **9.8 %** on the boundary-stress clip (vs 2.2 % primary). **Under-segmentation (two real
sentences joined) also occurs** — so a more aggressive guard is NOT an automatic win; the unit-test
matrix must prove false-split reduction without unacceptable over-joins. Evidence + attribution in
BENCHMARK_REPORT.md (Gemini Streaming-Caption Segmentation Study), evidence CSVs untracked in
`gemini_seg_study\`. Gate: 651/651 tests, Release 0 warnings/0 errors, `dotnet format` clean.

## Future

**Post-core product work (agreed priority order, 2026-08-06, after the core freeze at v0.5.25).** All
work below is product-level and must not silently modify the frozen core (STT model selection,
segmentation, partial cadence, resampler, CPU threading, overlay architecture, worker protocol, Argos):
1. **Tagalog accuracy** — only if real users find it unacceptable.
2. **Translation provider experiments** — benchmark alternatives to Argos without disturbing the core.
3. **UI/UX polish** — make the overlay closer to Chrome's look/behavior.
4. **Installer/distribution** — make it usable on another Windows machine.
5. **App-by-app validation** — YouTube, VLC, Zoom, Teams, etc.

- **Phase 2 — real-application validation (YouTube/Chrome, VLC, Zoom).** Deferred per user; a reassessment/validation pass over the Slice 6 baseline defaults, not an optimization sweep.
- **Corpus-driven phrase-guard validation (candidate, gated on a documented decision).** Per the
  segmentation-matrix decision (2026-08-14), a second smaller corpus-driven suite must establish
  **false-split reduction − over-join cost** before any phrase-level idiom guard touches production:
  observed continuation idioms → expected APPEND; the same idioms in genuine sentence-start contexts →
  expected FLUSH; unseen variants of the same construction; short fragments (esp. `Hindi Lunes.`);
  punctuation variations; capitalization variations; English equivalents where applicable; negative
  cases designed specifically to expose over-joining. No production change is authorized by the
  matrix alone.
- Latency display refinement and settings persistence (file-based configuration)
- Optional VB-CABLE input behind `IAudioCapture` (post-MVP)
- Optional cloud STT engine behind `ISpeechToTextEngine` with explicit disclosure
- Optional cloud translation engine behind `ITranslationEngine` with explicit disclosure
- Microphone capture (requires explicit enablement decision)
- System tray, global hotkeys, per-app audio source selection
- Signed installer and update channel
- Transcript recording/export
- Per-monitor overlay positioning presets

## Blocked

- None currently.
