# Project History (condensed close-outs)

Sprint/slice close-out summaries moved out of `CLAUDE.md` to keep the agent-context file lean.
Authoritative detail lives in `docs/implementation/CHANGELOG.md`, `docs/implementation/PROJECT_STATUS.md`,
`docs/implementation/ROADMAP.md`, `docs/reports/BENCHMARK_REPORT.md`, `docs/reports/TEST_REPORT.md`,
and `docs/implementation/investigations/`.

## Recent completed investigations

- **Gemini streaming-caption segmentation (COMPLETE 2026-08-14, measurement only)** — 20-run study;
  root cause: the v0.5.40 lowercase-only continuation guard misses capitalized continuations; app
  pipeline adds zero latency; **no production change**; next gate = segmentation-guard unit-test
  matrix. See `investigations/gemini-segmentation.md`.
- **Runtime Gemini-toggle latency (PASS 2026-08-12, measurement only)** — Whisper STT FINAL latency
  identical with Gemini OFF vs ON (11.8 s vs 11.4 s mean); Gemini fully detached when translation is
  OFF. See `investigations/latency-study.md`.

## Core done

- **Final real-world acceptance — PASS (2026-08-06, project core-done).** Production default
  (`fasterwhisper-native` + live partials, `UC_NATIVE_THREADS=4`) validated in continuous use:
  Release App + VLC + real WASAPI loopback, 300 s legs, per-poll CIM CPU + UIA snapshots.
  Leg 1 (Tagalog, translation OFF): STT worker 31.8% of machine (max 37.6%), App 0.9%, first caption
  3.27 s, 95 snapshots, 0 orphans. Leg 2 (English + en→tl): STT 33.5% + Argos 4.2%, App 1.3%, first
  caption 3.23 s, 129 snapshots, 0 orphans. Overlay verified (growing partials, FINAL freeze into
  bounded history, `EN || TL` badge, real Tagalog, Stop retains history). Clean Stop/Exit ~5 s.
  382/382 tests. Evidence: TEST_REPORT, CHANGELOG v0.5.25, `acceptance_*` artifacts (untracked).

## Entries

- **Entry 16 — CPU optimization: decode-thread cap (complete 2026-08-06).** `UC_NATIVE_THREADS` env
  knob → `FasterWhisperEngineOptions.Threads`, production default 4 (clamped [1, ProcessorCount]);
  worker `--threads` stays the single decode-thread control; `sttnative` gains `--threads`. Formal
  `sttnative` gate (12t vs 4t): WER 33.2% both, realtime 1.18× both, FINAL stream 100% text-identical.
  Real-App CPU probe: STT worker system mean 77.4% → 31.6%; App ~1%; first caption 3.72 s.
  **Decision: PASS — production default `Threads = 4`.** 382/382 tests. CHANGELOG v0.5.24.
- **Entry 15 — overlay live-line integration (complete 2026-08-06).** `CaptionOverlayWindow`
  paints the live partial stream in one mutable `_activeBlock`, rewritten in place on later partials,
  removed when `ActiveLine` is null. Pre-slice gap: commit `7d1c057` ("temporary diagnostic tracer")
  had replaced Slice 7's active-line painting. Real-App smoke PASS: first visible partial ≈5.6 s;
  en→tl live-translated active line before commit, no raw-English flash. 374/374 tests.
  CHANGELOG v0.5.23.
- **Entry 14 — production default promotion (complete 2026-08-05, ADR-0008).** Production STT
  default = `fasterwhisper-native` + live partials via testable `SpeechEngineFactory`;
  `UC_STT_ENGINE=ggml-base` = explicit fallback (original local-Whisper engine);
  `UC_STT_ENGINE=fasterwhisper` = unchanged windowed engine. **No automatic runtime fallback**
  (ADR-0003 no-silent-switch). Why: Slice 12 PASS + materially better Tagalog (WER ~33% vs 51.2%) +
  no 20–40 s backlog; costs (~5% wall, ~8 s tail emit-lag, Python-worker dependency) accepted.
  372/372 tests. CHANGELOG v0.5.22.
- **Entry 13 / Entry 12 / Entry 8 / Entry 7** — Slice-support entries; details in CHANGELOG.

## Slices

- **Slice 12 — faster-whisper native-streaming live partials (complete 2026-08-05: benchmark PASS).**
  `SpeechSegmentDetector.TryGetPartial` (bounded trailing-window snapshot) + `PartialDecodeInterval`
  (default 0 = FINAL-only preserved) / `PartialDecodeWindow` (4 s) + cadence dispatch (single
  in-flight/queued). Controlled gate: first visible partial 5.59 s after speech onset (vs first FINAL
  15.0 s), 19.5 partials/120 s, FINAL stream text-identical (WER 33.19%), backlog bounded, realtime-
  safe 1.18×. **Decision (at the time): PASS — partials default off** — superseded by Entry 14.
  367/367 tests. CHANGELOG v0.5.21.
- **Slice 11 — native-streaming segment-boundary tuning (complete 2026-08-05: keep 8 s).** Additive
  `sttnative` improvements (`timeBeginPeriod(1)` realtime-feed pacing fix → valid ~1.1× pacing;
  mid-sentence-split metric). Sweep 8/10/12 s: WER 32.6%/33.2%/30.0%, cadence 13.3/10.8/9.1 FINALs/
  120 s, splits 31%/42%/45%. **Longer segments do NOT reduce mid-sentence splits, cost ~46%
  responsiveness at 12 s, add end-of-audio cap hallucinations. Keep `MaxSegmentDuration = 8 s` — no
  production or knob-default change.** 357/357 tests. CHANGELOG v0.5.20.
- **Slice 10 — faster-whisper native streaming (PASSED 2026-08-05, not promoted).** New
  `FasterWhisperNativeStreamingEngine` behind `UC_STT_ENGINE=fasterwhisper-native`; C# owns
  VAD/segment detection; one FINAL per completed segment. WER 32.6%, 13.3 FINALs/120 s, first caption
  15.2 s, ~4 s behind segment end, no backlog. **PASS but NOT promoted** (8 s cap can split sentences;
  ~4 s tail). faster-whisper stays opt-in.
- **Slices 8–9 — faster-whisper selectable engine (2026-08-04).** `UC_STT_ENGINE=fasterwhisper`
  (persistent Python worker, `small` int8) validated end-to-end. **Decision-gate closed: NOT promoted**
  — steady-state STT latency 13.7–15.8 s vs ggml-base 2.4–3.7 s is a live-caption responsiveness
  regression; first caption 16.5 s vs 25.0 s; pre-warm ~2.6 s doesn't compensate. Tagalog accuracy
  gap on the `ggml-base` default acknowledged as open. 293/293 tests.
- **Slice 6 (Entry 8) — E2E latency/accuracy (complete 2026-08-01).** Phase 1a (E2E latency metric,
  238/238), Phase 1b (OFAT sweep → shortlist base 8/1/st2, tiny 8/1/st2, base 8/1/st3), Phase 1c
  (App-level SAPI E2E — every run publishing real translated Tagalog). **Baseline `base/8/1/st2`
  promoted to App default: `StabilityWindow` 3→2, model `ggml-base` unchanged.** Streamed-finals WER
  is a commit-rate proxy, not accuracy (TD-006/007).
- **Slice 5 + Entry 7 — WPF overlay + control window + live active-line translation (complete
  2026-08-01).** `CaptionOverlayWindow` (auto-size pill, chevron, hide button, `TL` badge) + live-
  translated active line (Tagalog before commit). 224/224 tests. Real-Argos E2E verified.
- **Slices 0–4 — core pipeline** (capture, processing/VAD, Whisper STT, Argos translation, caption
  service). All MVP slices complete.

## Closed technical debt

- **TD-001 (2026-08-05)** — resampler benchmark: WDL ≈30× faster but STT-equivalent (0.0% WER clean +
  noisy); keep sinc.
- **TD-005 (2026-08-05)** — settings persistence (`UserSettings`/`ISettingsStore`/`SettingsStore`).
- **TD-016 (2026-08-04)** — faster-whisper protocol-contract suite
  (`LineProtocolFasterWhisperProcessProtocolTests`, 9 tests).
