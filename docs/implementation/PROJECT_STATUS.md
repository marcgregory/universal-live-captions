# Universal Live Captions Project Status

Last updated: 2026-08-07

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Provide a current snapshot of project state, sprint progress, and blockers |
| Scope | Current sprint status, build status, blockers, and next milestone |
| Audience | Engineering |
| Owner | Engineering |
| Status | Active |
| Related Documents | [BUILD_PLAN.md](BUILD_PLAN.md), [ROADMAP.md](ROADMAP.md), [CHANGELOG.md](CHANGELOG.md), [RELEASE_PLAN.md](RELEASE_PLAN.md), [TECHNICAL_DEBT.md](TECHNICAL_DEBT.md) |

---

This document is a snapshot. It is not a changelog.

## Current Sprint

**Small-model Tagalog naturalizer — quality probe FAILED (2026-08-07).** Per the user's next
experiment, tested whether a small permissive instruction-following model can naturalize Argos
en→tl output (contract: improve naturalness while preserving meaning; guardrails enforced in the
prompt). **Qwen/Qwen2.5-1.5B-Instruct** (Apache-2.0, ungated, 1.5B) given the Argos Tagalog line
only, greedy deterministic decode, on the same 16 unseen lines vs 4 columns (Argos / Argos + frozen
13 rules / Argos + small model / Gemini reference). **DECISIVE FAIL at the quality gate (user's
rule: stop if not visibly better):** 15/16 lines are invalid Tagalog or meaning-destroyed; #7
violates the output contract (English + added explanation); inference ~11 s/line mean. Frozen-rule
column parity-verified against all 13 C# test vectors (0/16 rewrites, consistent with 0/23 unseen).
**No production change** — baseline remains `Whisper → Argos → frozen 13-rule naturalizer →
Caption`. The naturalization gap now has three independent failure lines: deterministic rules
(0/23 unseen recall), small instruction-following model (15/16 worse), M2M family (0/16). Remaining
untested options would need a materially larger permissive LLM (contra the user's "very small
model" preference) or a dedicated Tagalog-rewrite fine-tune (new training experiment). Evidence:
BENCHMARK_REPORT (Small-Model Tagalog Naturalizer section), `naturalizer_qwen2.5-1.5b_instruct_
2026-08-07.json`, commits `100fbae`.

**Translation research phase — offline model-selection investigation CLOSED (2026-08-07).** User
decision: **stop searching for another offline MT model.** Evidence chain (all recorded in
`BENCHMARK_REPORT.md`): Argos/OPUS-MT en→tl (production offline baseline, frozen, ~0.11 s/line),
NLLB-200-distilled-600M (quality ceiling but CC-BY-NC → not production-eligible), MADLAD-400-3B-MT
(rejected 2026-08-06: slow/verbose/2.8 GB), M2M-100-418M (rejected 2026-08-07: lost 0/16 unseen
lines to Argos, ~2.76 s/line mean), Gemini Live Translate (experimental quality/realtime reference —
cloud/privacy/cost tradeoff), frozen 13-rule naturalizer (fixes known Argos artifacts, ~0 unseen-set
recall). **Three-track conclusion:** (1) keep Argos + naturalizer as the production offline baseline
(`Whisper → Argos/OPUS-MT en→tl → frozen 13-rule naturalizer → Caption`); (2) keep Gemini as the
experimental reference (naturalness + realtime vs offline + privacy + cost); (3) stop the offline-
model hunt unless a new candidate materially changes the constraints. **Next experiment (user
direction): small-model Tagalog naturalization** — whether a small, permissively-licensed,
instruction-following/rewriting model can act as a Tagalog naturalization/correction layer over
Argos (a different experiment from another MT sweep). The second blind scorer of the unseen
worksheet is supporting evidence only and no longer blocks the direction. Evidence: BENCHMARK_REPORT
(Unseen-set generalization test + M2M probe + Final Decision), commits `98ab405`/`100fbae`.

**v0.5.26 — Core + Installer + Phase 2 app validation DONE (2026-08-06).** App-by-app validation of
the installed v0.5.26 bundle (`launcher.cmd`, `%LocalAppData%\UniversalCaptions`, no repo/admin/dev
env) against real apps, real WASAPI loopback, real en→tl. **Chrome / YouTube — PASS:** local-media
first caption ≈2.5 s; YouTube playback first real caption ≈14 s after Start, live partials translate
in place, `EN || TL` badge, committed translated Tagalog, 0 orphans. **VLC — PASS:** first caption
≈4.6 s, live partials + committed translated Tagalog, loop repeats, POSTSTOP history retained,
0 orphans. **Zoom — NOT VALIDATED (⚠️ limited evidence):** Zoom Workplace 7.0.6 is Chromium-based
with no UIAutomation surface and no available meeting/account — recorded as an environment limitation,
NOT an app defect and NOT upgraded to PASS (the run included no live meeting interaction). **Teams —
N/A** (desktop client not installed). Worker cmdlines installed-only throughout; no production-code or
installer changes. Evidence: TEST_REPORT §App-by-app validation — Phase 2, CHANGELOG v0.5.26,
`appval_*.{log,csv,txt}` (untracked).

**Installer & distribution — Entry 17 CLOSED as PASS (2026-08-06), post-core-done.** The frozen
v0.5.25 core is now
deployable to a clean Windows 10 machine with **no repo, no admin, no network**: Inno Setup (per-user)
+ self-contained .NET 8 win-x64 publish + a bundled pruned Python runtime + bundled faster-whisper
small model + bundled pruned Argos `en→tl` packages, wired by `packaging/launcher.cmd`
(process-scoped env only). One approved additive production seam: **`UC_FW_MODEL`** in
`SpeechEngineFactory.CreateNative` (unset → `"small"`, unchanged; set → worker `--model <path>`),
covered by two new tests. Setup.exe **795.5 MB**; installed **1,634.5 MB** at
`%LocalAppData%\UniversalCaptions` (flattened layout = `MAX_PATH` fix for the torch license tree that
rolled back the first install). **Installed-bundle acceptance PASS** (real audio via WASAPI loopback,
real en→tl): worker cmdlines are installed-only
(`py\python.exe … faster_whisper_worker.py --model <install>\models\faster-whisper-small --compute
int8 --threads 4 --beam-size 5`; Argos server on the same bundled python), first caption ≈4.1–4.7 s
warm, live partials + committed translated Tagalog (`EN || TL` badge), settings persist, clean
Start/Stop/Exit with 0 orphans, clean uninstall (exit 0) leaving only the app's own `settings.json`
(`PYTHONDONTWRITEBYTECODE=1` prevents `.pyc` leftovers). **384/384 tests**, Release 0 warnings/0
errors, `dotnet format` clean. Evidence: `docs/reports/INSTALLER_DISCOVERY.md` (§8 decisions, §9
build + acceptance), CHANGELOG v0.5.26, `packaging/` (`.iss`, `launcher.cmd`, `build-package.ps1`,
`output/UniversalCaptions-Setup-0.5.25.exe`), `installer_acceptance*.{ps1,log,csv,txt}` (untracked).
**Caveat (recorded, non-blocking):** installer acceptance passed using the final staged package; the
reproducible `build-package.ps1` path remains an optional follow-up validation because the final
installer was built successfully through the underlying Inno Setup process. Next meaningful test
before distribution is a **truly clean Windows machine**. No further installer changes.

**Final real-world acceptance — PASS (2026-08-06), project core-done.** Per user direction ("stop
optimizing CPU; run the final real-world acceptance session"), the production default
(`fasterwhisper-native` + live partials, `UC_NATIVE_THREADS=4`) was validated in continuous normal use
through `acceptance.ps1` (untracked): Release App + VLC + real WASAPI loopback, per-poll CIM CPU, UIA
overlay snapshots, 300 s legs. **Leg 1 Tagalog, translation OFF** (`uc_video_full.m4a`): STT worker
31.8% of machine (max 37.6%), App 0.9%, first caption 3.27 s, 95 snapshots, max 33 lines, clean exit,
0 orphans. **Leg 2 English + en→tl** (`english_sustained_90s.wav` looped): STT 33.5% (max 37.1%) +
Argos 4.2% (max 21.6%), App 1.3%, first caption 3.23 s, 129 snapshots, max 54 lines, clean exit,
0 orphans. Overlay evidence: live partials grow in place, FINALs freeze into bounded history with the
`EN || TL` badge, committed lines real Tagalog, Stop retains history with no stale partial. Clean exit
measured ~5 s (`WM_CLOSE`); harness close budget 25 s. **382/382 tests**, Release 0 warnings/0 errors,
`dotnet format` clean. Evidence: TEST_REPORT (final real-world acceptance), CHANGELOG v0.5.25,
`acceptance_summary.csv`/`acceptance_*.csv`/`acceptance_*_captions.txt` (untracked).

**Entry 16 — CPU optimization: decode-thread cap (COMPLETE 2026-08-06).** The promoted path
(`fasterwhisper-native` + live partials) sustained **77.4% of the machine** in the STT worker: every
partial and FINAL decode used all 12 cores (`FasterWhisperEngineOptions.Threads` defaulted to
`Environment.ProcessorCount`; the App passed all 12 to `--threads`). Fix (code-behind only, no engine/
protocol/segmentation/partial/overlay/translation change): **`UC_NATIVE_THREADS` env knob, production
default `Threads = 4`** (clamped [1, ProcessorCount]) in `SpeechEngineFactory.CreateNative`; worker
args extracted to `LineProtocolFasterWhisperProcess.BuildWorkerArguments` (unchanged behavior);
`sttnative` gains `--threads`. **382/382 tests** (8 new: factory default 4 / override / invalid
fallback + worker-arg propagation), Release 0 warnings/0 errors, `dotnet format` clean. **Formal
`sttnative` gate (12t vs 4t, real video audio, small int8 tl, partials 1/4 s, max segment 8 s):**
WER **33.2% both**, realtime **1.18× both**, first FINAL 17.98 vs 18.12 s, emit-lag comparable,
**FINAL stream text 100% identical (0 diffs)**. **Real-App CPU probe (default, speech + partials):**
STT worker system mean **77.4% → 31.6%** (max 88.2% → 37.6%), App ~1%, first caption 3.72 s, overlay
producing (max 16 lines); speech + translation run STT 26.6% + Argos 3.4%. **Decision: PASS — cap
production default at 4.** Evidence: Entry 16, TEST_REPORT (Entry 16 close-out),
BENCHMARK_REPORT (Entry 16 gate), CHANGELOG v0.5.24.

**Entry 15 — overlay live-line integration (COMPLETE 2026-08-06).** The WPF overlay previously painted
**committed FINALs only** — commit `7d1c057` ("temporary diagnostic tracer", 2026-08-03) had replaced
Slice 7's active-line painting and `_activeBlock` was never assigned. Now `CaptionOverlayWindow`
`UpdateCaptionItems` creates one mutable `_activeBlock`, rewrites it in place on later partials, and
removes it when `model.ActiveLine` is null (committed/stopped/hidden-while-translating); `ReconcileHistory`
reuse-by-sequence and the `shouldUpdate` gate (no source flash during translation-pending) unchanged.
`CaptionRenderIdentityTests` rewritten 4→6. **374/374 tests** (App 89), Release 0 warnings/0 errors,
`dotnet format` clean. **Real-App smoke PASS** (Entry 14 checklist + overlay AC-1..AC-8): first visible
partial ≈5.6 s after capture start; one growing active line (`meeting sum` → `Meeting someone.`); FINAL
freezes into history with no churn; Stop/Dispose leaves no stale partial (POSTSTOP_1..3 stable); App CPU
~0“66% variable / worker ~0%; **en→tl Argos verified** — live-translated Tagalog active line painted before
commit, no raw-English flash; tl→en confirmed as the documented-unsupported direction (stanza SBD) with
graceful degradation. Evidence: Entry 15, TEST_REPORT.md, CHANGELOG v0.5.23.

**Entry 14 — production default promotion (COMPLETE 2026-08-05, ADR-0008).** Product decision
(user-approved): the production STT default is now **`fasterwhisper-native` + live partials**; ggml-base
is preserved as the explicit fallback (`UC_STT_ENGINE=ggml-base`). Engine selection extracted into the
testable `SpeechEngineFactory` (default/native → native + partials with interval 1 s / window 4 s / 8 s
segment cap frozen; `ggml-base` → original local Whisper; `fasterwhisper` → windowed engine). No
automatic runtime fallback (deliberate, ADR-0003 no-silent-switch). Worker protocol, windowed engine,
ADR-0007, TD-002, TD-005 untouched. **372/372 tests** (5 new factory selection tests), Release
0 warnings/0 errors. Decision records: ADR-0008, Entry 14, CHANGELOG v0.5.22.

**Slice 12 — faster-whisper native-streaming live partials (COMPLETE 2026-08-05: benchmark PASS).** Chrome-Live-Caption-style live partials on the opt-in `fasterwhisper-native` engine: incremental partial text while the speaker is still talking, one FINAL per completed segment (unchanged), no wire-protocol change, translation OFF, ggml-base untouched. **Implementation (additive, knobs default off):** `SpeechSegmentDetector.TryGetPartial` (bounded trailing-window snapshot, refused while idle/hangover/after close), `FasterWhisperEngineOptions.PartialDecodeInterval` (default 0 = Slice 10/11 FINAL-only preserved) + `PartialDecodeWindow` (4 s), `FasterWhisperNativeStreamingEngine` cadence dispatch with at most one partial decode in flight/queued (no backlog), `PartialTranscriptAvailable` event; App knobs `UC_NATIVE_PARTIAL_INTERVAL` (1 s) / `UC_NATIVE_PARTIAL_WINDOW` (4 s); `sttnative` benchmark `--partial-interval`/`--partial-window` + partial metrics + CSV partial table. **367/367 tests** (10 new), Release 0 warnings/0 errors, format clean. **Controlled real-audio benchmark PASS (2026-08-05):** small int8, tl, hangover 0.7 s, max 8 s, realtime feed, translation OFF, `--partial-interval 1 --partial-window 4` on `uc_video_full_16k.wav` (288.79 s) vs the `fil-orig` reference — first visible partial **5.59 s after speech onset** (vs first FINAL 15.0 s), **19.5 partials/120 s** (~3 s updates during speech), active line increments ("Magandang" → … → full sentence), FINAL stream **text-identical to Slice 11** (no accuracy regression, WER 33.19% in-harness), FINAL ~6 s after segment close, backlog **bounded** (plateau ~50 s vs 43 s FINAL-only; one 17.5 s machine-contention spike), realtime-safe 1.18×, nothing dropped/reordered. **Decision: PASS — ggml-base stays the production default; partials default off, so production behavior is unchanged unless opted in.** Documented tradeoffs: ~5 % wall + ~8 s tail-latency cost, expected rolling-4 s-window behavior (FINAL reveals earlier words). Scoped in `docs/CHANGE_IMPACT_ANALYSIS.md` Entry 13; evidence in `BENCHMARK_REPORT.md`/`TEST_REPORT.md` (Slice 12), CHANGELOG v0.5.21.

**Slice 11 — native-streaming segment-boundary tuning (COMPLETE 2026-08-05: decision recorded — keep `MaxSegmentDuration = 8 s`).** Per-user follow-up to the Slice 10 PASS: tune the opt-in `fasterwhisper-native` segment boundaries (test 8/10/12 s, measure mid-sentence splits, confirm bounded latency/backlog, keep `SilenceHangover = 0.7 s`, no worker-protocol / ggml-base / windowed-engine changes). **Additive benchmark improvements:** `timeBeginPeriod(1)`/`timeEndPeriod(1)` around the `sttnative` realtime feed (fixes the ~1.57× `Thread.Sleep` pacing artifact → valid ~1.1× controlled pacing) and a mid-sentence-split metric (unterminated FINALs + short fragments, in gate table + CSV). **Controlled sweep (2026-08-05) — max-segment 8/10/12 s** on the actual video audio vs the `fil-orig` reference (small int8, tl, hangover 0.7 s fixed): WER 32.6%/33.2%/30.0%, cadence 13.3/10.8/9.1 FINALs/120 s, mid-sentence splits 31%/42%/45%, 0 partials, latency/backlog bounded at all three caps (~5 s behind segment end). **Longer segments do NOT reduce mid-sentence splits, cost ~46% responsiveness at 12 s, and add end-of-audio cap hallucinations (10 s `Pag-pag-pag…` stutter, 12 s truncated `tunog`); the 12 s WER gain is a boundary artifact.** **Decision: keep 8 s — no production or knob-default change** (real-App 8 s latency/backlog evidence already exists from Slice 10). **357/357 tests**, Release build 0 warnings/0 errors, format clean. Scoped in `docs/CHANGE_IMPACT_ANALYSIS.md` Entry 12; evidence in `BENCHMARK_REPORT.md`/`TEST_REPORT.md` (Slice 11), CHANGELOG v0.5.20.

**Slice 10 — faster-whisper native streaming (COMPLETE 2026-08-05: deterministic phase + benchmark/real-App validation PASSED).** New `FasterWhisperNativeStreamingEngine` behind `UC_STT_ENGINE=fasterwhisper-native` (additive; `fasterwhisper` keeps the windowed engine; ggml-base stays the frozen production default). C# owns VAD/segment detection + buffering + when-to-decode; the existing faster-whisper worker wire protocol is unchanged. One FINAL per completed speech segment (no live partials). **Deterministic phase DONE:** engine + internal `SpeechSegmentDetector` implemented, App selector branch added, **357/357 tests** (21 new, no Python), Release build 0 warnings/0 errors, format clean, no vulnerable packages; fresh-context review PASSED with fixes. **Validation PASSED (2026-08-05):** additive `sttnative` benchmark mode + real-App run with `fasterwhisper-native` (small int8, tl) on the actual video audio vs the `fil-orig` reference — committed WER **32.6%** (ggml-base 51.2%), **0 partials (FINAL-only)**, commit cadence **13.3 FINALs/120 s** (windowed faster-whisper 2/120 s), first real-App caption **15.2 s**, STT latency 11.6“12.9 s from segment start ≈ ~4 s behind segment end with no growing backlog, no recurring `(Song)`/`(Subscribe)` hallucinations, no dropped final at Stop. **Decision gate recorded: Slice 10's question is answered — segment-based native streaming preserves faster-whisper's accuracy while eliminating the stale 20“40 s commit backlog.** faster-whisper stays opt-in; the ggml-base production default is unchanged (frozen). Documented tradeoff: the 8 s segment cap can split sentences mid-word (tunable via `UC_NATIVE_MAX_SEGMENT`). Scoped in `docs/CHANGE_IMPACT_ANALYSIS.md` Entry 11; evidence in `TEST_REPORT.md` Slice 10, CHANGELOG v0.5.19.

**Slices 1“6 complete (close-out 2026-08-01).** Slice 5 (WPF overlay + control window) + Entry 7 (live active-line translation + Chrome-style overlay) closed out 2026-08-01; Slice 6 (E2E latency + OFAT baseline) closed out 2026-08-01. **Argos pre-warm closed out 2026-08-02** (v0.5.9): first-caption latency ~23“30 s → ~3.8“6.85 s. **Slice 7 — caption overlay layout & stable incremental rendering (in progress, 2026-08-02)**: full-viewport width verified via a layout probe; the render path now mutates only the live block on a Partial and reuses history blocks by identity, with bottom scroll/re-anchor limited to when a new block is inserted and content overflows. **All MVP slices (0“6) complete; Phase 2 real-app validation deferred per user.**

**Post-close-out refinement (2026-08-01):** live **active-line translation** + **Chrome-style overlay redesign** landed on top of Slice 5 (change-impact Entry 7): the in-progress caption line is now translated in the target language while the speaker is still talking (single in-flight slot, instance-identity stale-guard, disabled-mid-flight results discarded); the overlay is an auto-sized translucent pill with white text, a target-language badge, expand/collapse chevron, and a hide button; the control window adds "Show Captions". Implementation + unit tests **complete (224/224)**; **manual verification with real audio + real Argos completed 2026-08-01** — Tagalog appears on the in-progress overlay line before commit, `TL` badge, chevron expand/collapse, close-hide, "Show Captions" re-show, and pipeline-continues-while-hidden all verified (evidence in `TEST_REPORT.md`). **Entry 7 closed out 2026-08-01.**

## Current Progress

Slice 1 (Audio Capture Spike), Slice 2 (STT Spike), Slice 3 (Translation Spike), and **Slice 4 (Caption Service) are complete and verified.** Slice 4 close-out approved 2026-08-01: `ICaptionService`, `CaptionLine`, `CaptionState`, and `CaptionServiceOptions` in `UniversalCaptions.Core.Captions` (contracts in Core so `UniversalCaptions.Captions` depends only on Core, per the ADR-0003/0006 precedent); `src/UniversalCaptions.Captions` implements `CaptionService`: partials replace the active line, finals commit to a bounded sequence-ordered history, optional background translation (failure preserves the source caption; stale results matched by line identity are dropped), cancellation of in-flight translations on stop/reset/dispose, and events raised outside the serialization gate with snapshot `History`. Verified with deterministic `StubTranslationEngine`/`GatedTranslationEngine` fakes — 40 tests, build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, no vulnerable packages. Fresh-context review completed; findings fixed (snapshot history reads, deferred CTS disposal, atomic translation-start token, stale-translation identity guard, event-raising moved out of the translation catch, target normalization).

**Slice 5 (WPF overlay + control window) is complete (close-out 2026-08-01).** `src/UniversalCaptions.App` (new WPF project, `net8.0-windows`, `UseWPF`, PerMonitorV2 manifest) is the DI composition root: `IOverlayService` (visibility/position/opacity/font size/click-through), `CaptionOverlayWindow` (borderless/transparent/always-on-top, history + active line, drag/resize, click-through via `WS_EX_TRANSPARENT`), `CaptionPipeline` (wiring capture → processor → STT → `CaptionService` via `Func` factories, idempotent Start/Stop/Dispose, `StatusChanged`/`LatencyUpdated` events, error handling, teardown ordering), `ControlWindow` (audio source/language, translation on/off + target, status/latency, overlay sliders, Start/Stop), `AudioSourceLoader` (device enumeration with preferred default), `TranslationGuard` (source-equals-target rejection), and `App.xaml.cs` (DI registration + `ShutdownMode.OnMainWindowClose`). The deferred Q1 display policy is resolved: the active caption renders verbatim from the latest partial (`CaptionState.ActiveLine`); committed finals render as bounded history; translated text replaces the source on a committed line only when translation completes (PRD FR-5/FR-14). Verified with `UniversalCaptions.App.Tests` — `CaptionDisplayPolicyTests` (8) + `CaptionPipelineTests` (20) + `AudioSourceLoaderTests` (4) + `TranslationGuardTests` (4). **Manual overlay/device verification completed 2026-08-01** on this Windows 10 machine: real system audio → Whisper `ggml-base` → live overlay captions; always-on-top/transparency; drag/resize/click-through; stop/restart; rapid Stop→close (clean ~2 s exit); model-not-found and source-equals-target error paths (evidence in `TEST_REPORT.md` Slice 5). **Real-Argos wiring verified end-to-end through the App 2026-08-01**: with the dev Argos venv recreated (`argostranslate==1.11.0` + en→tl/tl→en/ja→en/en→ja under a short 8.3 temp path per TD-011), the App spawned the Argos child process and committed overlay lines displayed real translated Tagalog (`tamad aso.`, `Ang mabilis na kayumangging sorra ay tumatalon sa ibabaw ng eruplano`) with `IsTranslated = True`; this also exercised the `ControlWindow.ApplyTranslationSettings` guard fix (toggle stays ON + target combo enabled on a guard error so a valid target can be selected). Total test count: **209/209 passing** (66 Audio + 45 Captions + 41 Speech + 21 Translation + 36 App). All Slice 5 Definition-of-Done items are satisfied.

The `ITranslationEngine` contract (with `TranslationResult`, `TranslationErrorKind`, `TranslationException`) lives in `UniversalCaptions.Core.Translation`; it is verified with a deterministic `FakeTranslationEngine` (8 tests); `ArgosTranslationEngine` (child Python process over a newline-delimited JSON line protocol, bundled `argos_translate_server.py`) is verified with a fake process seam (13 tests, incl. restart-after-fatal-error) and against real Argos 1.11.0 end-to-end (direct pairs `en→tl`, `ja→en`, `en→ja`; pivoting `ja→tl` via `en`). The translation benchmark is recorded (load/first latency, steady-state distinct-text latency, identical-input cache, throughput, Argos working set, finals-stream ordering, char-similarity quality). Fresh-context review completed; findings fixed (stale-process recovery, unwrapped exceptions, Python crash path, options validation, `ArgumentList`, UTF-8 pinning) with remaining items in TD-013“TD-015.

## Slice 6 Baseline Defaults (validated 2026-08-01)

> **Superseded 2026-08-05 (Entry 14 / ADR-0008):** the production STT default is now
> `fasterwhisper-native` + live partials (see Current Sprint). This Slice 6 block remains as the
> historical record of the former ggml-base baseline, now the explicit fallback
> (`UC_STT_ENGINE=ggml-base`) with its frozen settings:

```text
Model:            ggml-base (unchanged, ADR-0003)
WindowDuration:   8 s (unchanged)
DecodeInterval:   1 s (unchanged)
StabilityWindow:  3 → 2 (promoted)

Evidence:         OFAT sweep (Phase 1b) + App-level SAPI E2E validation (Phase 1c:
                  real WASAPI loopback → Whisper → Argos en→tl → WPF overlay,
                  baseline + shortlist × 3 runs each, every run publishing real
                  translated Tagalog) — docs/reports/BENCHMARK_REPORT.md +
                  docs/reports/TEST_REPORT.md

Status:           Validated baseline for the current release (one authoritative
                  configuration shared by App + benchmark). Real-application
                  validation (YouTube/Chrome, VLC, Zoom) is deferred per user;
                  defaults may be revisited after Phase 2.
```

## Current Focus

**Slice 11 — native-streaming segment-boundary tuning (COMPLETE 2026-08-05: decision recorded — keep 8 s).** Additive `sttnative` benchmark improvements (`timeBeginPeriod(1)` realtime-feed pacing fix → valid ~1.1× controlled pacing; mid-sentence-split metric in gate table + CSV) + controlled sweep at max-segment 8/10/12 s (small int8, tl, hangover 0.7 s fixed) on the actual video audio vs the `fil-orig` reference. Results: WER 32.6%/33.2%/30.0%, cadence 13.3/10.8/9.1 FINALs/120 s, splits 31%/42%/45%, 0 partials; latency/backlog bounded at all three caps (~5 s behind segment end). **Longer segments do NOT reduce mid-sentence splits; they cost responsiveness and add end-of-audio cap hallucinations (10 s `Pag-pag-pag…`, 12 s truncated `tunog`); the 12 s WER gain is a boundary artifact.** **Decision: keep `MaxSegmentDuration = 8 s` — no production or knob-default change** (real-App 8 s evidence from Slice 10 stands). Worker protocol / ggml-base / windowed engine untouched. 357/357 tests, Release build 0 warnings/0 errors, format clean. Scoped in `docs/CHANGE_IMPACT_ANALYSIS.md` Entry 12; evidence in `BENCHMARK_REPORT.md`/`TEST_REPORT.md` (Slice 11), CHANGELOG v0.5.20.

**All MVP slices (0“6) remain complete.** **Argos pre-warm landed 2026-08-02** (v0.5.9): background pre-warm warms one shared Argos process/model off the real-caption path, so the first caption drops from ~23“30 s cold start to ~3.8“6.85 s (warm translation ~0.46 s), verified live through the real App (Cases A + B: single process spawn + single model init, no duplicate init, no lost first caption; 260/260 tests). **Slice 7 — caption overlay layout & stable incremental rendering (2026-08-02)**: a layout probe confirmed the caption `TextBlock` already uses the full ~522 px viewport width correctly (short lines stay one line; long text wraps only on width exhaustion — the reported "whole text re-flows" is not a width bug), and the render path now does scope-stable incremental rendering (a Partial only rewrites the live block's text in place; history blocks reused by identity, never rebuilt) with bottom scroll re-anchoring limited to when a new block is inserted and content overflows.

**ADR-0007 Option B — boundary-preserving fallback (2026-08-04, in progress toward acceptance):** the streaming commit path was the last quality gap (premature `At gusto ko` / `Kaya` / `country can do for` fragments). Implemented + unit-tested (**284/284**) and validated live against **JFK (controlled English verification, PASS)** — single + continuous runs through the real App no longer emit the pre-fix `country can do for` interior fragment and Stop drain preserves finals. **The original Tagalog recording scenario (`At gusto ko` / `Kaya` / `artipisyal na katalinuhan`) is the remaining acceptance evidence and is Pending** — the original operator recording is not available in the workspace; per user, no substitute Tagalog sample may be used to claim acceptance. Implementation frozen; ADR-0007 stays `Proposed` until that live evidence exists. **Phase 2 real-app validation (YouTube/Chrome, VLC, Zoom) stays deferred per user.**

**Slice 8 — Tagalog STT-vs-Committer isolation + model selection (2026-08-04, recorded):** the reported Tagalog defects were isolated to the STT layer, not the committer. RAW Whisper segments already contain the misrecognitions (`Kung usta?`, `Ikao.`, `Salaman.`), hallucinated `1.` segments, and short fragment boundaries; the committer aggregates them faithfully. Real-App comparison across all three local models on the same Tagalog slice (STT `tl`, frozen config): **base ~3.1 s** STT latency but weak accuracy; **tiny ~1.75 s** fastest but no accuracy gain and worst fragmentation; **small** best Tagalog accuracy (`Kumusta`/`Ikaw`/`Salamat`/`Juan` all correct, no `1.` hallucination) but **16.9“21.9 s** latency — cannot keep real-time pace. **No available whisper.cpp local model gives both Tagalog quality and responsiveness.** ADR-0007 is NOT implicated (this is model-selection, not commit/boundary behavior). Evidence: `artifacts/samples/raw_vs_committed_tagalog.log` + `realapp_{tiny,base,small}_tagalog.log`; findings in `BENCHMARK_REPORT.md` (Slice 8) + `TEST_REPORT.md`.

**Faster-whisper selectable STT engine (2026-08-04, recorded):** the Slice 8 gap was closed by adding a **parallel faster-whisper STT path** (`UC_STT_ENGINE=fasterwhisper`) behind the same streaming engine boundary — the whisper.cpp decode portion was extracted to the `ISTTDecoder` seam with **zero behavior change** to the default `ggml-base` path (293/293 tests). A persistent binary-framed Python worker (`Server/faster_whisper_worker.py`, model loaded once, `small` int8, 8 threads, beam 5, `condition_on_previous_text=False`) drives `FasterWhisperDecoder`. **Real-App validation (same 90 s Tagalog slice, STT `tl`, frozen config st2/8 s/0.5 s/0.5 s) confirmed the target: whisper-small-level Tagalog accuracy with no `1.`/`one` hallucination at lower latency than whisper.cpp small** — STT latency 10.7“11.7 s (vs small 16.9“21.9 s), first final 16.5“29.9 s, 3“4 clean bilingual finals. A 1.5 s-interval variant gave the cleanest complete sentences (first final 16.5 s ≈ base 17.5 s). Two wire-protocol bugs (`0x46574355` magic endianness; 16→20-byte segment header) were found and fixed during the real-App run (unit-test fakes did not exercise the wire format). Evidence: `artifacts/samples/realapp_fasterwhisper_small_tagalog.log` (+ `_int1_5_` variant); findings in `BENCHMARK_REPORT.md` + `TEST_REPORT.md`.

**Faster-whisper default-selection decision-gate — CLOSED: NOT promoted (2026-08-04).** The promotion candidate was measured against the frozen default on startup + steady-state latency. Worker cold start: spawn 0.006 s + Python import/model load **2.6 s** + first 8 s-window decode 2.5 s. Real-App (same 90 s Tagalog slice, STT `tl`): faster-whisper `small` first caption **16.5“17.4 s** (better than ggml-base's measured 25.0 s) but steady-state STT latency **13.7“15.8 s** vs ggml-base **2.4“3.7 s**. Window/interval tuning does not close the steady-state gap (1.0 s interval ≈ no change; 1.5 s worse at 24.2 s; 4 s window produced no captions) — the frozen 8 s/0.5 s config is already near-optimal for the faster-whisper path. Pre-warm would save only ~2.6 s. **Decision per user: `ggml-base` stays the production default; faster-whisper `small` int8 remains opt-in (`UC_STT_ENGINE=fasterwhisper`) until its steady-state latency can be materially reduced.** Accuracy winner: faster-whisper; responsiveness winner: ggml-base. Clean close: no production change, no forced promotion; the Tagalog accuracy gap on the ggml-base default remains acknowledged as open. Evidence: `artifacts/samples/firstcaption_{fw_small,i1_fw_small,base,w4_fw_small}.log`; findings in `BENCHMARK_REPORT.md` (Slice 9 decision-gate) + `TEST_REPORT.md`.

**TD-005 — settings persistence CLOSED (2026-08-05).** The user-facing preferences now survive restart:
per-user JSON at `%LocalAppData%\UniversalCaptions\settings.json` (in-box `System.Text.Json`, atomic
`.tmp` → `File.Move(overwrite)`, unknown fields ignored, missing/malformed → safe defaults). The six
persisted categories: audio source device, speech language, translation on/off + target, overlay
appearance (opacity/font/click-through), overlay placement, overlay view state. Engine/env knobs
(`UC_STT_*`, Argos/Python, model) stay env-driven — never persisted. `App.xaml.cs` loads before window
construction; `ControlWindow` applies + coalesced-dispatcher-saves (close flush); `CaptionOverlayWindow`
seeds and saves placement/view state. **335/335 tests passing** (6 new `SettingsStoreTests`), Release
build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean. **TD-002 stays frozen/Open** until
the real hotplug acceptance test can be run; no change to ADR-0007 / model selection / the resampler.

## Architecture Status

Approved: .NET 8 + WPF + NAudio + local Whisper behind streaming `ISpeechToTextEngine` (ADRs 0001“0005) + Argos Translate behind `ITranslationEngine` (ADR-0006, refined: contracts in Core, `UniversalCaptions.Translation` owns the engine + process seam; pair/protocol selection resolved by Slice 3 benchmark). Pipeline layers per `ARCHITECTURE.md`.

## Platform Status

Windows 10 target (build 17763+). Development environment: Windows with .NET SDK 8/10. NAudio 2.2.1 restored. Whisper.net 1.9.1 + Whisper.net.Runtime (CPU). No VB-CABLE. Whisper models cached in git-ignored `artifacts/models/` (tiny/base/small). Argos 1.11.0 in a dedicated Python 3.11 venv + `en/ja/tl` language packages under `artifacts/argos/` (git-ignored; dev machine venv created under the temp dir with the short 8.3 path to avoid Windows MAX_PATH limits during torch install).

## Current Blockers

**Original Tagalog recording for ADR-0007 acceptance** — the live evidence for the `"At gusto ko"` / `"Kaya"` / `"artipisyal na katalinuhan"` regression requires the original operator recording, which is unavailable in the workspace; per user, no substitute sample qualifies. ADR-0007 remains `Proposed` until it is supplied and validated through the real App (fragmentation, duplicates, missing words, Stop drain).

## Next Milestone

**Core is done (per user criterion, 2026-08-06):** the final real-world acceptance run passed at the
production default — stable ~32“33% STT + ~1% App CPU over 300 s continuous media, first caption ~3.2 s,
live partials on the overlay, live en→tl translation, bounded history, clean exit, 0 orphans. No further
CPU optimization. Remaining work is feature-level / product-level, not core architecture: **Phase 2
real-app validation (YouTube/Chrome, VLC, Zoom) stays deferred per user**; ADR-0007 stays `Proposed`
until the original operator Tagalog recording is supplied; TD-002 stays **frozen/Open** until the real
hotplug acceptance test can be run.

**Slice 11 — native-streaming segment-boundary tuning (COMPLETE 2026-08-05: decision recorded — keep 8 s).** Additive `sttnative` benchmark improvements (`timeBeginPeriod(1)` realtime-feed pacing fix → valid ~1.1× controlled pacing; mid-sentence-split metric in gate table + CSV) + controlled sweep at max-segment 8/10/12 s (small int8, tl, hangover 0.7 s fixed) on the actual video audio vs the `fil-orig` reference: WER 32.6%/33.2%/30.0%, cadence 13.3/10.8/9.1 FINALs/120 s, splits 31%/42%/45%, 0 partials; latency/backlog bounded at all three caps (~5 s behind segment end). **Longer segments do NOT reduce mid-sentence splits, cost ~46% responsiveness at 12 s, and add end-of-audio cap hallucinations (10 s `Pag-pag-pag…` stutter, 12 s truncated `tunog`); the 12 s WER gain is a boundary artifact. Decision: keep 8 s — no production or knob-default change** (real-App 8 s latency/backlog evidence from Slice 10 stands). 357/357 tests, Release build 0 warnings/0 errors, format clean. Scoped in Entry 12; evidence in BENCHMARK_REPORT/TEST_REPORT (Slice 11), CHANGELOG v0.5.20. **Slice 10 is complete (close-out 2026-08-05)** — `FasterWhisperNativeStreamingEngine` + `SpeechSegmentDetector` behind `UC_STT_ENGINE=fasterwhisper-native`, benchmark + real-App validation PASSED (WER 32.6%, 13.3 FINALs/120 s, first caption 15.2 s, ~4 s behind segment end); faster-whisper stays opt-in, ggml-base default unchanged. **Slice 6 is complete (close-out 2026-08-01)** (E2E metric, OFAT sweep + shortlist in `BENCHMARK_REPORT.md`, App-level SAPI E2E validation; baseline `base/8/1/st2` promoted to the App default — `StabilityWindow` 3→2, model `ggml-base` unchanged). **All MVP slices (0“6) are complete.** **Argos pre-warm closed out 2026-08-02** (v0.5.9) — first-caption latency ~23“30 s → ~3.8“6.85 s, verified live. **Slice 7 (caption overlay layout & stable incremental rendering) closed out 2026-08-02** — tests 267/267 (see CHANGELOG v0.5.10). **ADR-0007 Option B implemented + unit-tested (284/284) + live JFK verification passed (2026-08-04); final acceptance gated on the original Tagalog recording (Pending).** Next work after acceptance is from the roadmap Future list and the deferred Phase 2 real-app validation (YouTube/VLC/Zoom) reassessment per user. See `docs/implementation/BUILD_PLAN.md` and `docs/implementation/ROADMAP.md`.

## Last Build

2026-08-06 — `dotnet build UniversalCaptions.slnx` succeeded, 0 warnings, 0 errors. `dotnet test UniversalCaptions.slnx` passed **382/382** (77 Audio + 72 Captions + 111 Speech + 27 Translation + 95 App). **Final real-world acceptance PASS (2026-08-06):** continuous VLC media through the default device at the production default (`fasterwhisper-native` + live partials, `UC_NATIVE_THREADS=4`). Leg 1 Tagalog/translation-OFF (`uc_video_full.m4a`, 288.79 s): STT worker 31.8% system mean (max 37.6%), App 0.9%, first caption 3.27 s, clean exit, 0 orphans. Leg 2 English/en→tl (`english_sustained_90s.wav` looped): STT 33.5% (max 37.1%) + Argos 4.2% (max 21.6%), App 1.3%, first caption 3.23 s, clean exit, 0 orphans. Overlay verified live (growing partials, FINAL freeze into bounded history, `EN || TL` badge, real Tagalog, Stop retains history). **Entry 16 COMPLETE (2026-08-06):** `UC_NATIVE_THREADS` knob, production default `Threads = 4`; formal 12t-vs-4t gate WER 33.2% both, realtime 1.18× both, FINAL stream 100% text-identical; real-App STT worker system mean 77.4% → 31.6%. **Entry 15 COMPLETE (2026-08-06):** overlay live-line integration (partials painted in a single mutable active block). **Entry 14 COMPLETE (2026-08-05):** production default promoted to `fasterwhisper-native` + live partials (ADR-0008). See CHANGELOG v0.5.22–v0.5.25, TEST_REPORT (Entry 15/16 + final acceptance), BENCHMARK_REPORT (Entry 16 gate). Prior (2026-08-05): TD-005 settings persistence 335/335; Slice 11 segment-boundary tuning (keep 8 s) 357/357; Slice 10 faster-whisper native streaming PASS; TD-016 protocol-contract suite 302/302; TD-001 resampler benchmark closed.
