# Universal Live Captions Changelog

Last updated: 2026-08-06

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Document versioned project history — answers "What has changed?" |
| Scope | All notable additions, changes, fixes, and removals across releases |
| Audience | Engineering |
| Owner | Engineering |
| Status | Active |
| Related Documents | [BUILD_PLAN.md](BUILD_PLAN.md), [ROADMAP.md](ROADMAP.md), [PROJECT_STATUS.md](PROJECT_STATUS.md), [RELEASE_PLAN.md](RELEASE_PLAN.md) |

---

All notable project changes should be documented here. Keep this file versioned and historical; do not use it as a current status report.

## v0.5.29 - 2026-08-07

### Translation research CLOSED (no production-code changes)

- **Translation & naturalizer investigation closed by user (2026-08-07).** Evidence is conclusive
  and the user stops both the offline-MT search and the naturalizer-model search. Three independent
  failure lines: deterministic 13 rules (0/23 unseen recall), M2M-100-418M (0/16 + 20–40× slower),
  Qwen2.5-1.5B-Instruct small-LLM naturalizer (15/16 worse + contract violation); NLLB-600M is
  quality-excellent but CC-BY-NC (not production-eligible); Gemini Live is the cloud quality/realtime
  reference. **Production path frozen (no code changes):** `WASAPI → Whisper → Argos OPUS-MT en→tl →
  13-rule deterministic naturalizer → Caption overlay`. Optional experimental path: `Audio → Gemini
  Live Translate` (user's own API key). A larger LLM or a dedicated Tagalog-rewrite fine-tune is
  explicitly deferred as a future research/training project, not an MVP optimization. **Next phase:
  release/landing-page work.** Evidence: BENCHMARK_REPORT (Final Decision — Translation &
  Naturalizer Investigation CLOSED), entries v0.5.27–v0.5.28.

## v0.5.28 - 2026-08-07

### Translation research (no production-code changes)

- **Small-model Tagalog naturalizer quality probe (2026-08-07).** Per the user's next experiment,
  tested whether a small permissive instruction-following model can naturalize Argos en→tl output
  (contract: "given an existing Tagalog translation, improve naturalness while preserving meaning";
  guardrails: no name/number/information changes, no explanations, output only the corrected Tagalog).
  **Qwen/Qwen2.5-1.5B-Instruct** (Apache-2.0, ungated) given the Argos Tagalog line only, greedy
  deterministic decode, on the same 16 unseen lines vs 4 columns (Argos / Argos + frozen 13 rules /
  Argos + small model / Gemini reference). **DECISIVE FAIL at the quality gate (user's rule: stop
  if not visibly better):** 15/16 lines are invalid Tagalog or meaning-destroyed, and #7 violates
  the output contract (English + added explanation). Inference ~11 s/line mean (fp32, 12-core) —
  quality fails before performance would matter. The frozen-rule column was parity-verified against
  all 13 C# unit-test vectors and rewrote 0/16 (consistent with the 0/23 unseen finding). **No
  production change**; baseline remains `Whisper → Argos → frozen 13-rule naturalizer → Caption`.
  Evidence committed `100fbae` + this entry; report section + raw JSON in `BENCHMARK_REPORT.md` and
  `artifacts/reports/translatelive/naturalizer_qwen2.5-1.5b_instruct_2026-08-07.json`.

## v0.5.27 - 2026-08-07

### Translation research (no production-code changes)

- **Unseen-set generalization test of the frozen 13-rule `TagalogNaturalizer` (2026-08-07).** New
  unseen conversational en→tl sample (`gen_english_unseen_wav.ps1` + `artifacts/samples/
  english_unseen_90s_16k.wav`, 92.85 s, 16 lines that avoid all 13 naturalizer triggers) run through
  the real App pipeline (Whisper → Argos → naturalizer) and a Gemini Live Translate leg
  (`translatelive`). Naturalizer coverage on unseen content: **0/23 rewrites**. Blinded per-line
  human eval (worksheet + key in `artifacts/reports/translatelive/`): decoded Argos Nat 2.69 /
  Meaning 3.56 / 1 win vs Gemini Nat 4.44 / Meaning 4.81 / 14 wins + 1 tie — **decisive Gemini
  victory**; the 13 rules stay frozen, no promotion. Recorded in `BENCHMARK_REPORT.md`; evidence
  committed `98ab405`.
- **M2M-100-418M offline en→tl quality probe (2026-08-07).** Translation-only comparison of
  facebook/m2m100_418M (MIT, permissive) vs the bundled Argos/OPUS-MT baseline on the exact same 16
  unseen English lines, native/untuned per the user's measurement rule (`m2m_probe_unseen.py` +
  `argos_corpus_unseen.py`, raw rows in `artifacts/reports/translatelive/`). **M2M lost 0/16 lines**:
  whole-utterance English passthrough (#10), word-sense catastrophes ("here you go" → `pag-ibig`,
  "nine thirty" → `nakaraang taon`), English leakage throughout, and ~20–40× slower inference
  (mean 2.76 s/line vs Argos ~0.11 s/line). Rejected at the quality gate (user's rule: no streaming
  benchmark when not clearly better). Evidence committed `100fbae`.
- **Offline model-selection investigation CLOSED (2026-08-07, user decision).** Stop the offline-MT
  hunt; no candidate (NLLB-600M CC-BY-NC, MADLAD-400-3B, M2M-100-418M) beats the frozen
  Argos+naturalizer offline baseline while meeting permissive-license + realtime constraints.
  Three-track conclusion: (1) Argos+naturalizer stays the production offline baseline; (2) Gemini
  remains the experimental quality/realtime reference (naturalness + realtime vs offline + privacy +
  cost); (3) next experiment is **small-model Tagalog naturalization** (an instruction-following/
  rewriting layer over Argos), not another MT sweep. Second blind scorer is supporting evidence only,
  no longer blocking. See `BENCHMARK_REPORT.md` "Final Decision".

## v0.5.26 - 2026-08-06

### Added (offline installer — UC_FW_MODEL + packaging)

- **`UC_FW_MODEL` env knob (approved additive production seam).** `SpeechEngineFactory.CreateNative`
  reads `UC_FW_MODEL` → `FasterWhisperEngineOptions.Model`; unset → default `"small"` (behavior
  identical to today); set → the worker gets `--model <path-or-name>` verbatim (faster-whisper accepts a
  local directory, fully offline). Process-scoped only — set by the launcher, never a global/user env
  variable. Tests: `NativeModel_Unset_DefaultsToSmall`, `NativeModel_Override_IsRespected`. Nothing else
  in the frozen v0.5.25 core changed.
- **Offline, no-repo, no-admin installer** (Inno Setup 6.7.3, per-user). `packaging/launcher.cmd`
  sets `UC_FW_PYTHON`/`UC_ARGOS_PYTHON` → bundled `py\python.exe`, `UC_FW_MODEL` → bundled
  `models\faster-whisper-small`, `UC_STT_MODEL_PATH` → bundled `ggml-base.bin`, `ARGOS_PACKAGES_DIR` →
  bundled `argos-packages` (pruned `en→tl` closure, 79.1 MB), `HF_HOME` + `HF_HUB_OFFLINE=1` +
  `TRANSFORMERS_OFFLINE=1`, `PYTHONDONTWRITEBYTECODE=1` (keeps the installed tree static so uninstall is
  clean). `UC_STT_ENGINE` stays unset → production default (`fasterwhisper-native` + live partials,
  `UC_NATIVE_THREADS=4`) applies unchanged.
- **Bundled runtime**: relocatable uv standalone cpython-3.11 (74 MB) + merged, import-verified pruned
  fwv/argosv site-packages. torch (494 MB) is REQUIRED (stanza SBD imports it unconditionally); torch
  `include`/`share`/`distributed` extras, `functorch`, pip/setuptools/pygments/rich, onnxruntime
  `tools`, deep third-party license trees, and all `__pycache__` dropped.
- **Sizes**: Setup.exe **795.5 MB** (lzma2/ultra); installed **1,634.5 MB** at
  `%LocalAppData%\UniversalCaptions` (flattened layout keeps every path ≤172 chars — `MAX_PATH` fix for
  the torch `dist-info\licenses` tree that caused the first install to roll back exit 5).
- **Reproducible packaging**: `packaging/build-package.ps1` (publish → trim → runtime merge/prune →
  stage → `manifest.txt` → ISCC); `packaging/UniversalCaptions.iss`.

### Verified (installed-bundle acceptance, real audio, real en→tl)

- **Install/launch**: clean install exit 0, launch via `launcher.cmd`, exe from the installed path.
- **Worker paths are installed-only**: STT
  `py\python.exe … faster_whisper_worker.py --model <install>\models\faster-whisper-small --compute
  int8 --threads 4 --beam-size 5`; Argos `py\python.exe … argos_translate_server.py`; no
  `%TEMP%\fwv`/`%TEMP%\argosv`/`huggingface`/`artifacts\`/repo references in any cmdline.
- **Captions**: first caption ≈4.1–4.7 s (warm), live partials grow in place, committed translated
  Tagalog history (`EN || TL` badge, real lines e.g. `Ang pangalan ko ay Maria.`, `Ano ang pangalan
  mo?`), looped corpus repeats, settings persist.
- **Lifecycle**: clean Start/Stop/Exit, 0 orphaned workers; clean uninstall exit 0 leaving only the
  app's own `settings.json` (user data preserved; `PYTHONDONTWRITEBYTECODE` verified to prevent `.pyc`
  leftovers). No UAC/admin (`asInvoker`). Full suite still **384/384**, Release 0 warnings/0 errors,
  `dotnet format` clean. Evidence: `docs/reports/INSTALLER_DISCOVERY.md` (§8, §9),
  `installer_acceptance*.{ps1,log,csv,txt}` (untracked), `packaging/output/`.

### Verified (Phase 2 — app-by-app validation, real-world apps)

- **Chrome / YouTube — PASS.** Installed v0.5.26 bundle + real WASAPI loopback. Chrome media playback
  (local WAV via `file://`): first caption ≈2.5 s, live partials + committed translated Tagalog.
  YouTube playback (`youtube.com/watch?v=dQw4w9WgXcQ`, network allowed for the media source only):
  first real caption ≈14 s after Start, live partials translate in place, `EN || TL` badge, committed
  translated Tagalog history, clean exit, 0 orphans. Worker cmdlines installed-only.
- **VLC — PASS.** Fresh run (`english_sustained_90s.wav` looped 60 s): first caption ≈4.6 s, live
  partials grow in place, committed translated Tagalog, loop repeats, POSTSTOP history retained, clean
  exit, 0 orphans. Corroborated by the installer acceptance and the final real-world acceptance.
- **Zoom — NOT VALIDATED (⚠️ limited evidence).** No PASS/FAIL claim. Zoom Workplace 7.0.6 is
  Chromium-based and exposes **no UIAutomation surface**; `zoommtg://` join with the documented
  test-meeting ID failed; no real meeting/account available to emit speech. Environment limitation —
  the WASAPI capture path is identical to the VLC/Chrome legs (both PASS). Manual-assist follow-up
  declined (a live meeting would not change the frozen-build conclusion).
- **Teams — N/A** (desktop client not installed).
- Full evidence: TEST_REPORT §App-by-app validation — Phase 2; artifacts `appval_*.{log,csv,txt}`
  (untracked). No production-code or installer changes in this phase.

### Close-out (2026-08-06)

**Entry 17 / installer distribution CLOSED as PASS.** Caveat recorded: installer acceptance passed
using the final staged package; the reproducible `build-package.ps1` path remains an optional
follow-up validation because the final installer was built successfully through the underlying Inno
Setup process. Next meaningful test before distributing to others is a truly clean Windows machine
(this machine retains dev/runtime state that could mask a missing dependency). No further installer
changes.

## v0.5.25 - 2026-08-06

### Verified (final real-world acceptance at production default)

- **Continuous media playback through the default device passes** (Entry 16 close-out evidence, per user
  direction — "stop optimizing CPU; run the final real-world acceptance session"). `acceptance.ps1`
  (untracked) drives the Release App + VLC + real WASAPI loopback with per-poll CIM CPU sampling and UIA
  overlay snapshots over 300 s runs.
- **Leg 1 — Tagalog YouTube-style content, translation OFF** (`uc_video_full.m4a`, 288.79 s single pass):
  STT worker **31.8%** of the machine (max 37.6%), App 0.9%, first caption **3.27 s**, 95 caption
  snapshots, overlay max 33 lines, **clean exit, 0 orphaned workers**.
- **Leg 2 — English + en→tl translation ON** (`english_sustained_90s.wav`, looped 300 s): STT **33.5%**
  (max 37.1%) + Argos **4.2%** (max 21.6%, bursty single-slot), App 1.3%, first caption **3.23 s**, 129
  snapshots, overlay max 54 lines, **clean exit, 0 orphaned workers**.
- **Overlay evidence:** live partials grow in place (`Hello at malugod na tanggapin ang` → full line),
  FINALs freeze into bounded history with the `EN || TL` badge, committed lines are real Tagalog
  (`Ano ang pangalan mo?`, `Magandang umaga lahat.`), looped corpus repeats correctly, Stop retains
  history with no stale partial.
- **Clean Stop/Exit is real:** app exits on `WM_CLOSE` in ~5 s (measured); one earlier 10 s-timeout flake
  was the harness close budget while the 289 s video's final flush was in flight (budget raised to 25 s).
- **Decision: PASS — the production default behaves acceptably in continuous daily use; project core-done**
  (no further CPU optimization). Evidence: `docs/reports/TEST_REPORT.md` (final real-world acceptance),
  `acceptance_summary.csv` + `acceptance_*.csv` + `acceptance_*_captions.txt` (untracked). Full suite
  still **382/382**, Release 0 warnings/0 errors, `dotnet format` clean.

## v0.5.24 - 2026-08-06

### Changed (Entry 16 - CPU optimization: decode-thread cap)

- **The promoted path no longer saturates the machine.** Root cause: `FasterWhisperEngineOptions.Threads`
  defaults to `Environment.ProcessorCount` (12) and the App passed all 12 to every partial AND FINAL
  decode, so the STT worker sustained ~9-10 cores = **77.4% of the whole machine** during speech (App
  itself only ~12%). Decode wall is thread-count-invariant for real speech, so capping threads costs
  nothing perceptible.
- **Production default `Threads = 4`** via the new `UC_NATIVE_THREADS` env knob in
  `SpeechEngineFactory.CreateNative` (default 4, clamped to [1, ProcessorCount]; unparseable/
  out-of-range falls back to 4). `--threads` remains the single worker decode-thread control
  (`LineProtocolFasterWhisperProcess.BuildWorkerArguments` extracted for tests). ggml-base and the
  windowed faster-whisper engine are untouched (their own options).
- `sttnative` benchmark gains `--threads` so the gate can sweep decode threads.
- Tests: `SpeechEngineFactoryTests` (default 4 / override 6 / invalid abc,0,-1,99 -> 4) +
  `LineProtocolFasterWhisperProcessProtocolTests` (worker args carry `--threads`) + internal `Options`
  seam on the native engine. Full suite **382/382** (App 95), Release 0 warnings/0 errors,
  `dotnet format` clean.

### Verified (formal benchmark gate + real-App CPU probe)

- **`sttnative` gate, threads=12 vs 4** on `uc_video_full_16k.wav` (288.79 s) vs the `fil-orig`
  reference, identical Slice 12 composition (small int8, tl, hangover 0.7 s, max segment 8 s, partials
  1/4 s, realtime): WER **33.2% both**, realtime **1.18x both**, first FINAL 17.98 vs 18.12 s, first
  partial 13.27 vs 13.30 s, emit-lag comparable, **FINAL stream text 100% identical (0 diffs, all 32)**,
  same 10/32 split points.
- **Real-App CPU probe (speech + partials, translation OFF, default Threads=4):** STT worker single-core
  mean **929.1% -> 379.6%** = system mean **77.4% -> 31.6%** (max 88.2% -> 37.6%); App ~1% system; first
  caption 3.72 s; overlay producing (max 16 lines). Speech + translation run: STT 26.6% + Argos 3.4%
  system mean. Captions still flow with live partials exactly as before.

## v0.5.23 - 2026-08-06

### Added (Entry 15 — overlay live-line integration)

- **The overlay now paints the live partial stream.** The WPF overlay previously showed committed FINALs
  only: commit `7d1c057` ("temporary diagnostic tracer", 2026-08-03) had replaced Slice 7's active-line
  painting and `_activeBlock` was never assigned. `CaptionOverlayWindow.UpdateCaptionItems` now creates one
  mutable `_activeBlock`, rewrites it in place on later partials, and removes it when `model.ActiveLine` is
  null (committed/stopped/hidden-while-translating). `ReconcileHistory` reuse-by-sequence and the
  `shouldUpdate` gate (holds during translation-pending → no source-language flash) are unchanged.
- So the promoted default (`fasterwhisper-native` + live partials, Entry 14) is finally visible as it
  happens: first visible partial ≈5.6 s after capture start, one growing active line, FINAL freezes into
  history, Stop leaves no stale partial.
- Tests: `CaptionRenderIdentityTests` rewritten 4→6 (partial rewrites same block identity; growing stream
  paints one block with no history churn; no partial ever enters committed history; FINAL freezes active
  into history; cleared active line removes block keeps history; finalized blocks keep text instances and
  order). Full suite **374/374** (App 89), Release 0 warnings/0 errors, `dotnet format` clean.

### Verified (real-App smoke, Entry 14 nine-point checklist + overlay AC-1..AC-8)

- UIA-driven smoke of the Release App with the promoted default now records per-sample
  `app%|wkr%|n` (App CPU / faster-whisper worker CPU / overlay text-element count) and POSTSTOP_1..3
  stability probes. `promoted` (tl): first Partial 5.035 s, first Final 7.573 s, 18 FINALs, `Salamat.
  Ikaw.` preserved across Stop; Tagalog materially better than ggml-base; two `one`-for-`ako` quirks remain.
- `liveoverlay`: `meeting sum` → `Meeting someone.` → FINAL freeze, single block throughout; App CPU
  variable (~0–66%), worker CPU ~0%.
- `stopmid`: Stop mid-speech drains 2 FINALs, stable at 4 elements across 3 × 1.2 s — no stale partial.
- `transen` (en→tl): **live-translated Tagalog active line** painted before commit (first Partial 3.604 s,
  first translation request 3.610 s, first translation result 6.847 s, first translated caption ≈11.5 s);
  no raw-English flash.
- `trans` (tl→en): documented-unsupported Argos direction (stanza no `tl` SBD) → graceful degradation to
  source text, no crash, no fallback. ADR-0006 unchanged.

## v0.5.22 - 2026-08-05

### Changed (Entry 14 — production default promotion, product decision)

- **The production STT default is now `fasterwhisper-native` + live partials** (ADR-0008,
  user-approved). The App's engine selection moved into a testable `SpeechEngineFactory`:
  - default / `UC_STT_ENGINE=fasterwhisper-native` → native streaming engine, partials ON
    (`UC_NATIVE_PARTIAL_INTERVAL` default 1 s, `UC_NATIVE_PARTIAL_WINDOW` default 4 s,
    `MaxSegmentDuration` 8 s — the Slice 11 frozen cap)
  - `UC_STT_ENGINE=ggml-base` → the original local-Whisper engine (explicit fallback, preserved)
  - `UC_STT_ENGINE=fasterwhisper` → the windowed faster-whisper engine (unchanged opt-in)
- **No automatic runtime fallback** (deliberate — silent engine switches violate ADR-0003); a worker
  failure stops the session with a caption error and the user can restart with `ggml-base`.
- Faster-whisper worker protocol, the windowed engine, ADR-0007, TD-002, and TD-005 are untouched.
- **Why:** Slice 12 PASS (Chrome-like live partials, first visible 5.59 s, FINAL stream identical to
  Slice 11), materially better Tagalog recognition (committed WER ~33% vs ggml-base 51.2%), and no
  20–40 s backlog — the measured costs (~5 % wall, ~8 s tail emit-lag, Python-worker dependency) are
  documented in ADR-0008 and Entry 14.
- Tests: 5 new `SpeechEngineFactoryTests` (default/fallback/opt-in selection); full suite **372/372**,
  Release 0 warnings/0 errors.

## v0.5.21 - 2026-08-05

### Added (Slice 12 — faster-whisper native-streaming live partials, Chrome-Live-Caption-style)

- **Additive live-partial emission on the opt-in `fasterwhisper-native` engine** (default behavior
  unchanged; knobs default off):
  - `SpeechSegmentDetector.TryGetPartial(maxSamples, out samples, out capturedAtUtc)` — bounded
    trailing-window snapshot of the in-progress segment (refused while idle / during hangover / after
    the segment completes; capture time = window start).
  - `FasterWhisperEngineOptions.PartialDecodeInterval` (default `TimeSpan.Zero` = disabled → Slice
    10/11 FINAL-only preserved) + `PartialDecodeWindow` (default 4 s, bounds each partial decode).
  - `FasterWhisperNativeStreamingEngine` cadence dispatch with **at most one partial decode in
    flight/queued** (no growing backlog; ticks deferred, not queued), partials cleared on FINAL,
    shared session guard, new `PartialTranscriptAvailable` event (replaces the CS0067 pragma),
    internal `Segment` → `WorkItem(..., IsPartial)` rename, static `ToPcm` helper.
  - App knobs `UC_NATIVE_PARTIAL_INTERVAL` (default 1 s) / `UC_NATIVE_PARTIAL_WINDOW` (default 4 s)
    for `fasterwhisper-native`; interval 0 restores FINAL-only.
  - Benchmark `sttnative`: `--partial-interval`/`--partial-window` args, first-partial/first-caption-
    lag (T4)/partial-cadence/lag-distribution metrics, CSV partial table + summary columns.
- **Slice 12 controlled benchmark (2026-08-05)** — one real-audio run (small int8, `tl`, hangover
  0.7 s, max segment 8 s, realtime feed, translation OFF) on `uc_video_full_16k.wav` (288.79 s) vs
  the `fil-orig` reference with `--partial-interval 1 --partial-window 4`:

  | Metric | Result |
  |---|---|
  | First visible partial (from feed start) | 9.19 s (speech onset 3.60 s) |
  | **First caption lag T4 (onset → first partial)** | **5.59 s** (vs first FINAL 15.0 s) |
  | Partial update cadence | 19.5 partials/120 s (~3 s apart during speech) |
  | FINALs | 32 — text-identical to the Slice 11 8 s run (no accuracy regression) |
  | WER (in-harness) | 33.19% (= Slice 11; the report 32.6% uses `stt_compare.py` normalization) |
  | Backlog | bounded: plateau ~50 s (Slice 11 FINAL-only ~43 s), flat, not growing |
  | Realtime factor | 1.18× (Slice 11 1.13×; partial decodes add ~5 % wall) |

- **Decision (2026-08-05): PASS — Slice 12 closes out; `ggml-base` stays the production default.**
  The partial knobs default off, so production behavior is unchanged unless a user opts in via
  `UC_STT_ENGINE=fasterwhisper-native` + `UC_NATIVE_PARTIAL_INTERVAL=1`. Documented tradeoffs: ~5 %
  wall + ~8 s tail-latency cost of partial decodes, and the expected rolling-4 s-window behavior (the
  FINAL reveals the earlier words not shown by the last partial). No worker protocol / ggml-base /
  windowed-engine changes. Full suite **367/367**, Release 0 warnings/0 errors. Evidence:
  `BENCHMARK_REPORT.md`/`TEST_REPORT.md` (Slice 12), Entry 13; raw log
  `%TEMP%\opencode\sttnative_partials_slice12.log` (+ `.csv`).

## v0.5.20 - 2026-08-05

### Added (Slice 11 — native-streaming segment-boundary tuning, decision recorded)

- **Additive `sttnative` benchmark improvements** (`NativeStreamingBenchmark.cs`):
  - **Realtime-feed timer-resolution fix:** `timeBeginPeriod(1)`/`timeEndPeriod(1)` around the feed
    loop so `Thread.Sleep(10)` actually paces ~10 ms/chunk — controlled-run latencies are now valid
    (~1.1× realtime instead of the ~1.57× `Thread.Sleep`-granularity artifact from Slice 10).
  - **Mid-sentence-split metric:** the gate table now counts FINALs that end without terminal
    punctuation (unterminated = forced boundary cuts) and short fragments (≤2 words and unterminated),
    with split-point indices and CSV summary columns (`unterminated`, `short_fragments`).
- **Slice 11 sweep (2026-08-05)** — `sttnative` at `MaxSegmentDuration` 8/10/12 s on the actual video
  audio vs the `fil-orig` reference (small int8, `tl`, hangover 0.7 s fixed):

  | MaxSegment | FINALs | Cadence | WER (norm) | Mid-sentence splits | Fragments | End-of-audio cap |
  |---|---|---|---|---|---|---|
  | 8 s | 32 | 13.3/120 s | 32.6% | 10/32 (31%) | 0 | clean (last speech segment committed before the music tail) |
  | 10 s | 26 | 10.8/120 s | 33.2% | 11/26 (42%) | 1 | capped segment spanning the music tail decoded as a `Pag-pag-pag…` stutter |
  | 12 s | 22 | 9.1/120 s | 30.0% | 10/22 (45%) | 1 | capped segment decoded as a truncated `tunog` fragment |

- **Decision (2026-08-05): keep `MaxSegmentDuration = 8 s`** for the opt-in `fasterwhisper-native`
  engine — no production or knob-default change. Findings: longer segments do **not** reduce
  mid-sentence splits (fraction worsens 31% → 42% → 45%; the cap still force-closes mid-sentence,
  each cut discards more in-flight content), cost ~46% responsiveness at 12 s (9.1 vs 13.3
  FINALs/120 s), and add end-of-audio cap risk (music-tail hallucinations at 10 s/12 s). The small
  12 s WER gain (30.0% vs 32.6%) is a boundary artifact, not a decoding gain. Latency/backlog stays
  bounded at all three caps (emit ~5 s behind segment end; worst decode ~8 s for a capped 12 s segment
  < segment length). 8 s reproduces the Slice 10 WER exactly, confirming the timer fix did not alter
  accuracy. Worker protocol / ggml-base / windowed-engine paths untouched. Evidence: `BENCHMARK_REPORT.md`
  (Slice 11), `TEST_REPORT.md` (Slice 11), `CHANGE_IMPACT_ANALYSIS.md` Entry 12.

## v0.5.19 - 2026-08-05

### Added (Slice 10 — benchmark + real-App validation PASSED)

- **Additive `sttnative` benchmark mode** (`NativeStreamingBenchmark.cs`, `UniversalCaptions.Benchmarks`)
  drives the real `FasterWhisperNativeStreamingEngine` exactly as the App composes it (same
  `EnergyVad(0.008, 1, 2)` + 0.3 s / 0.7 s / 8 s segment knobs, `small` int8, `tl`) from a recorded
  16 kHz mono WAV and records the gate table (first FINAL, FINAL timeline, FINAL-only check, stop flush,
  commit cadence, WER vs a reference, emit-lag proxy, CPU/realtime factor, optional CSV). Dispatch added
  to `Program.cs`; the existing `stt` / `translate` / `resample` modes are untouched.
- **Slice 10 validation record (2026-08-05)** — controlled benchmark + real-App run with
  `UC_STT_ENGINE=fasterwhisper-native` on the actual video audio (`uc_video_full.m4a` /
  `uc_video_full_16k.wav`, 288.79 s) vs the `fil-orig` reference:
  - **Accuracy preserved:** committed WER **32.6%** (same `stt_compare.py` normalization; ggml-base
    full-file 51.2%, faster-whisper full-file 31.1%).
  - **Stale backlog eliminated:** commit cadence **13.3 FINALs/120 s** (windowed faster-whisper:
    2/120 s, ~40 s apart); one fresh FINAL per ~8.2 s segment, ~4 s behind the segment's speech end
    (STT latency 11.6–12.9 s from segment start), no growing backlog. Note: the controlled run's
    absolute latencies are a `Thread.Sleep(10)` ≈ 15.6 ms Windows-timer feed artifact (~1.57× wall),
    not engine behavior; the real-App run (true WASAPI realtime) is the authoritative latency.
  - **FINAL-only:** 0 partials in both runs; captions appear atomically (no revision churn).
  - **First real-App caption 15.2 s** after playback start (vs ggml-base 14.8–21.0 s, windowed
    faster-whisper 27.1 s); ~30 committed captions in 289 s.
  - **No recurring `(Song)`/`(Subscribe)` hallucinations**; music gaps produce no captions (the
    repeated "Paano kung…" is in the source audio). Rare small fragments (`ba ba i?`, `Usa atin!`).
  - **No dropped final at Stop:** the last sentence ("…maliit na unit ng tunog.") committed at 285 s
    before the 289 s audio end; Stop flush verified in the deterministic tests.
- **Documented tradeoff:** the 8 s `MaxSegmentDuration` cap can split a sentence mid-word (e.g.
  "…instruction ng wea" / "Ang Filipino…") — inherent to segment-based streaming, tunable via
  `UC_NATIVE_MAX_SEGMENT`.
- **Decision gate (2026-08-05):** Slice 10's research question is answered — segment-based native
  streaming preserves faster-whisper small's accuracy advantage (32.6% vs 51.2%) while eliminating the
  stale 20–40 s commit backlog. faster-whisper stays **opt-in**
  (`UC_STT_ENGINE=fasterwhisper-native`); the ggml-base production default is unchanged (frozen).
  Promotion to default is out of scope (freeze) and would be a separate decision.
- Evidence: `TEST_REPORT.md` (Slice 10 section), `CHANGE_IMPACT_ANALYSIS.md` Entry 11, BUILD_PLAN
  Slice 10. Raw logs: `artifacts/samples/realapp_native_streaming.log`,
  `%TEMP%\opencode\sttnative_small_realtime.log` (+ `.csv`, `hyp_sttnative_small.txt`).

## v0.5.18 - 2026-08-05

### Added (Slice 10 — faster-whisper native streaming, deterministic phase)

- **`FasterWhisperNativeStreamingEngine`** (`UC_STT_ENGINE=fasterwhisper-native`) — a segment-based
  `ISpeechToTextEngine`: C#-side VAD segment detection commits **one coherent FINAL per completed
  speech segment** decoded once through the existing faster-whisper worker wire protocol. No live
  partials in this experiment. Composes the Core `IVoiceActivityDetector` contract (Speech does not
  reference Audio); `EnergyVad` is wired at the App composition root. Isolated, additive: the ggml-base
  production default, the windowed `fasterwhisper` engine, `IFasterWhisperProcess` /
  `LineProtocolFasterWhisperProcess` / `faster_whisper_worker.py`, `StreamingTranscriptCommitter`, and
  ADR-0007 are all untouched. Rollback = unset `UC_STT_ENGINE`.
- **Internal `SpeechSegmentDetector`** state machine (Idle → InSpeech → hangover → emit) with
  `SpeechSegmentDetectorOptions` knobs — `MinSpeechDuration` (default 0.3 s, discards noise blips),
  `SilenceHangover` (0.7 s, bridges intra-sentence pauses), `MaxSegmentDuration` (8 s hard latency cap
  for continuous speech); env-tunable via `UC_NATIVE_MIN_SPEECH` / `UC_NATIVE_HANGOVER` /
  `UC_NATIVE_MAX_SEGMENT`. Engine behavior: Stop flushes the in-progress segment and drains queued
  segments (no dropped final); a per-session epoch guard prevents a decode that outlives Stop from
  raising a stale FINAL into a restarted session; decode/start failures map to
  `RecognitionFailed` and stop the session; non-mono input raises `InvalidAudioFormat` once.
- **Deterministic tests** (21 new: 11 detector + 11 engine) — synthetic PCM + scripted VAD/worker, no
  Python/model. Full suite **357/357 passing**, Release build 0 warnings / 0 errors, format clean, no
  vulnerable packages. Evidence in `TEST_REPORT.md` (Slice 10 section); fresh-context review PASSED
  with fixes (segment-duration accounting across hangover resumes, session-epoch guard, broadened
  Start error mapping, option validation, engine-level cap test).
- Scope record: `CHANGE_IMPACT_ANALYSIS.md` Entry 11 (2026-08-05). Real-App/benchmark validation is the
  next gate (not yet run).

## v0.5.17 - 2026-08-05

### Added / scoped

- **Slice 10 scoped — faster-whisper native streaming (2026-08-05).** Recorded the isolated-experiment
  slice in `CHANGE_IMPACT_ANALYSIS.md` Entry 11 (design only, no code yet). New
  `FasterWhisperNativeStreamingEngine` behind `UC_STT_ENGINE=fasterwhisper-native` (additive; the existing
  `fasterwhisper` windowed engine and the ggml-base production default are both unchanged). C# owns
  VAD/segment detection, speech buffering, and when-to-decode; each completed speech segment is decoded
  once through the **existing** faster-whisper worker wire protocol (protocol, worker script,
  `IFasterWhisperProcess`, `LineProtocolFasterWhisperProcess`, `StreamingTranscriptCommitter`, ADR-0007:
  all untouched). One FINAL per segment, no live partials in this experiment. Acceptance is not WER-only:
  must prove accuracy (better than base, no recurring `(Song)`/`(Subscribe)` hallucinations), responsive
  first caption (no 20–40 s stale backlog), one coherent FINAL per segment, no duplicate/re-emitted
  segments, no dropped final at Stop, and the existing translation path still works. Deterministic tests
  first (segment-detector state machine on synthetic PCM; engine against scripted VAD/decoder — no
  Python), then benchmark/real-App validation through the App on the actual video audio vs the `fil-orig`
  reference. If the experiment fails any target, faster-whisper stays opt-in and production stays exactly
  as it is. Rollback = unset `UC_STT_ENGINE`. Baseline for the experiment (2026-08-05 diagnostic):
  ggml-base full-file WER **51.2%** vs faster-whisper small int8 **31.1%** on the same video audio; first
  FINAL real-App 14.8–21.0 s (base) vs 27.1 s (windowed faster-whisper); windowed faster-whisper committed
  only **2 FINALs in 120 s** (~40 s cadence) vs base ~12 (~5–9 s) — the stale-caption defect the native
  streaming slice targets.

## v0.5.16 - 2026-08-05

### Added

- **TD-005 closed — file-based settings persistence (2026-08-05).** User preferences now survive restart.
  New `UniversalCaptions.App/Settings` types: `UserSettings` (immutable record; nullable = use built-in
  default; `Version` field for future migration), `ISettingsStore` (injectable seam), `SettingsStore`
  (file-backed). The **six user-facing categories** persist to per-user JSON at
  `%LocalAppData%\UniversalCaptions\settings.json`: (1) audio source device id, (2) speech language,
  (3) translation on/off + target, (4) overlay appearance — opacity/font size/click-through, (5) overlay
  placement (persisted only after the user drags; otherwise adaptive bottom-anchored default), (6)
  overlay view state (expanded/collapsed). In-box `System.Text.Json` (camelCase, case-insensitive read,
  **unknown fields ignored → forward compatible**); missing/malformed/wrong-type file → safe defaults,
  never fails startup; **atomic write** via same-dir `.tmp` + `File.Move(overwrite: true)` (a failed
  write preserves the last good file); a store lock serializes writes. `App.xaml.cs` loads settings
  **before window construction**; `ControlWindow` applies persisted values on load and saves on change
  (coalesced dispatcher saves + a synchronous flush on close; merges into the persisted file so
  overlay-owned fields are never clobbered); `CaptionOverlayWindow` seeds opacity/font/click-through/
  expanded/placement and saves placement + view state on drag/collapse/close. Engine/environment knobs
  (`UC_STT_*`, Argos/Python paths, model selection) stay env-var-driven and are **not** persisted.
  New `SettingsStoreTests` (6) — save/load round-trip; missing → defaults; malformed/wrong-type → safe
  defaults; unknown/new fields ignored; atomic + failed-write preserves last good; concurrent/rapid
  saves settle without torn state. Full suite now **335/335 passing** (App 76→82), Release build 0
  warnings/0 errors, `dotnet format --verify-no-changes` clean. No change to TD-002 / ADR-0007 / model
  selection / the resampler. (Design + impact: Entry 10; TD-005 row + SECURITY_PLAN + TEST_REPORT updated.)

## v0.5.15 - 2026-08-05

### Changed / decided

- **TD-001 closed — resampler benchmark: windowed-sinc vs NAudio `WdlResampler` (2026-08-05).** The
  current `<SampleRateConverter>` (windowed-sinc) was benchmarked head-to-head against `WdlResampler`
  on the same representative audio (clean + noisy `jfk.wav`), per the TD-001 decision gate.
  Benchmark-only — no production change. A `resample` command was added to
  `UniversalCaptions.Benchmarks` (`ResamplerBenchmark.cs`; benchmark project now references
  `UniversalCaptions.Audio`). Measurements (best of 5, 0.5 s chunks, mono): WDL converts 44.1k->16k
  and 48k->16k in **~13 ms** vs **~356–400 ms** for the current sinc (≈28–31x faster, 0.00x vs
  0.03–0.04x realtime) with ~3.0–3.2 MB vs 5.7–6.1 MB allocation per 11 s clip; **STT impact is
  identical** — both resamplers and the no-resampling control give **0.0% WER** on clean and noisy
  audio at equal decode latency. Because the current sinc already runs 25-30x faster than realtime
  and decode dominates the pipeline by >10x, resampling does not materially contribute to live-caption
  latency (saving ≈0.4 ms per 0.5 s chunk), so the switch is **not justified**: keep
  `SampleRateConverter`, do not introduce `WdlResampler` into production. Full suite **302/302
  passing**, Release build 0 warnings / 0 errors. Findings + decision in `BENCHMARK_REPORT.md`
  (TD-001); evidence in `TEST_REPORT.md` (TD-001).

## v0.5.15 - 2026-08-05

### Added

- **TD-002 — device-change notification + automatic-recovery contract (2026-08-05).** Trace-first pass
  per the TD-001 discipline; delivers the **notification/recovery contract + 20 deterministic tests**.
  New Core-pure contract `UniversalCaptions.Core.Capture` `IDeviceChangeMonitor` (`DeviceChanged` +
  `Start`/`Stop`), `DeviceChangeNotification` (`Kind`/`DeviceId`/`State`), `DeviceChangeKind`,
  `DeviceState`. New `UniversalCaptions.Audio` `WasapiDeviceChangeNotifier`: implements
  `IMMNotificationClient` registered via `MMDeviceEnumerator.RegisterEndpointNotificationCallback`,
  with a **lazy** `MMDeviceEnumerator` so unit tests drive the `IMMNotificationClient` methods directly
  with no COM/audio service; surfaces only `DataFlow.Render` (output) changes. New `UniversalCaptions.App`
  `DefaultDeviceAutoRecovery` coordinator: while the live session is on the **system default** device it
  restarts that session on default-device change or when the endpoint is unplugged/not-present;
  explicit-device sessions are never auto-restarted; a burst of notifications coalesces into one restart.
  Tests: `WasapiDeviceChangeNotifierTests` (11) + `DefaultDeviceAutoRecoveryTests` (9). Full suite now
  **322/322 passing** (77 Audio + 72 Captions + 77 Speech + 27 Translation + 69 App), Release build 0
  warnings/0 errors, `dotnet format --verify-no-changes` clean.
- **TD-002 production wiring (2026-08-05).** User-approved step-6 decision. `CaptionPipeline` composes a
  `DefaultDeviceAutoRecovery` when given an `IDeviceChangeMonitor`: a live default-device session starts
  monitoring and stops it on teardown; **`Removed` is added as a restart trigger** alongside default-change
  and unplug/not-present. New `CaptionPipeline.RestartCaptureAsync` detaches + disposes the stale capture,
  re-queries the **system default** device, and recreates a capture chain **while preserving the speech
  engine unchanged** (engine/model never touched); guarded against stop/dispose races, duplicate sessions,
  and faulted/disposed restarts; a failed recovery stops the session in a controlled error state.
  `App.xaml.cs` registers `WasapiDeviceChangeNotifier` as the monitor (DI composition root). New tests:
  **7 `CaptionPipeline` recovery tests** (default-change recreates + keeps STT; removed triggers; explicit
  device never recovers; burst coalesces to one session; stop/dispose no recovery; failure → error+stop).
  Full suite now **329/329 passing** (77 Audio + 72 Captions + 77 Speech + 27 Translation + 76 App).
  **Real hotplug verification is pending — TD-002 stays Open until it passes** (change-impact Entry 9;
  TD-002 row + TEST_REPORT updated). No change to the `ggml-base` default / faster-whisper selection /
  ADR-0007 / resampler.

## v0.5.14 - 2026-08-04

### Added

- **TD-016 closed — deterministic protocol-contract tests for `LineProtocolFasterWhisperProcess` (2026-08-04).** Closes the Slice 9 finding that the two wire bugs (magic byte order `0x46574355`; 16→20-byte segment header) were caught only by the real-App run. A fake-worker fixture emits exactly the production wire format over an in-memory stdout stream (no Python/venv/model), and the real production reader is exercised unchanged through a new internal injectable-stream constructor seam on `LineProtocolFasterWhisperProcess` (`Stream stdin, Stream stdout`; `StartAsync` skips the real process spawn when streams are injected; `WriteRequestAsync` no longer requires a live `_process`). New tests: `LineProtocolFasterWhisperProcessProtocolTests` (9) — golden 20-byte-header frame parses exactly (Kumusta/0.5/1.25), request header writes correct magic + layout (incl. int16 PCM), wrong magic `0x55435746` rejected as `Protocol`, 20-byte header does not consume payload (a 16-byte reader would read "Kums" as text length → huge length → EOF), two segments parse in order with distinct timestamps, fragmented pipe reads (3/7/1/9 chunks) reconstruct the frame, truncated 19-byte segment header → deterministic `EngineUnavailable` "closed the protocol stream" (never a partial segment), truncated 15-byte response header → deterministic error, multi-byte UTF-8 payload boundary consumes exactly the declared byte length. Full suite **302/302 passing** (66 Audio + 72 Captions + 77 Speech + 27 Translation + 60 App), Release build 0 warnings / 0 errors. This is the higher-priority TD item per user (TD-013-style faster-whisper protocol suite; Argos `LineProtocolArgosProcess` TD-013 remains Open separately). Isolated to the opt-in faster-whisper path; the `ggml-base` default is untouched.

## v0.5.13 - 2026-08-04

### Added

- **Faster-whisper as a selectable `ISpeechToTextEngine` (2026-08-04).** Adds a parallel faster-whisper STT path (`UC_STT_ENGINE=fasterwhisper`; default/empty still whisper.cpp `ggml-base`, ADR-0003 unchanged) targeting the "small-level Tagalog accuracy + lower-than-small latency" gap that no whisper.cpp model closed (Slice 8 finding: base ~3.1 s but weak accuracy/hallucinated `1.`; small best accuracy but 16.9–21.9 s). Architecture preserved the approved shape: the whisper.cpp **decode portion** was extracted to the `ISTTDecoder` seam (`WhisperCppDecoder` owns `WhisperFactory`/`WhisperProcessor`; the engine's windowing/trim/commit orchestration is untouched, zero behavior change to the `ggml-base` path), and `FasterWhisperDecoder` runs a persistent binary-framed Python worker (`Server/faster_whisper_worker.py`) that loads the faster-whisper model once (`small` int8, 8 threads, beam 5, `condition_on_previous_text=False`, float32-normalized PCM). New env knobs: `UC_FW_PYTHON` (venv auto-discovery `%TEMP%\fwv`, else system `python`), plus the shared `UC_STT_WINDOW`/`UC_STT_INTERVAL`/`UC_STT_MIN_AUDIO`/`UC_STT_STABILITY`. New types: `FasterWhisperEngineOptions`, `IFasterWhisperProcess`, `FasterWhisperProcessException` (EngineUnavailable/Timeout/Protocol/EngineFailed), `LineProtocolFasterWhisperProcess`, `FasterWhisperDecoder`, `FasterWhisperSpeechToTextEngine`.
- **Tests:** `FasterWhisperSpeechToTextEngineTests` (5) + `FasterWhisperDecoderTests` (4) with a fake process seam; Speech project bundles the worker script (`CopyToOutputDirectory`). Full suite **293/293 passing** (66 Audio + 72 Captions + 68 Speech + 27 Translation + 60 App), Release build 0 warnings/0 errors.
- **Real-App validation (UIA-driven Release App, same 90 s Tagalog slice, STT `tl`, frozen config st2/8 s/0.5 s/0.5 s):** faster-whisper `small` int8 committed clean bilingual finals with **no `1.`/`one` hallucination** (STT latency 10.7–11.7 s vs whisper.cpp small 16.9–21.9 s; first final 16.5–29.9 s; 3–4 finals). A 1.5 s-interval variant gave the cleanest complete sentences (first final 16.5 s ≈ base 17.5 s). Evidence: `artifacts/samples/realapp_fasterwhisper_small_tagalog.log` (+ `_int1_5_` variant); findings below in `docs/reports/BENCHMARK_REPORT.md` + `docs/reports/TEST_REPORT.md`. **Default model stays `ggml-base`; no promotion without user approval.**
- **Protocol fixes found during real-App validation:** `LineProtocolFasterWhisperProcess` magic constant corrected to the little-endian `0x46574355` ("UCWF") and the per-segment header read corrected from 16 → 20 bytes (worker's `"<ddI"` is 8+8+4). The unit-test-fake seam did not exercise the wire format; the real-App run surfaced both mismatches (the earlier run committed only `Listening.`).

### Changed / decided

- **Faster-whisper default-selection decision-gate (decision: NOT promoted; 2026-08-04).** Measured real-App startup + steady-state latency for the promotion candidate vs the frozen default. Worker cold start decomposes to spawn 0.006 s + Python import/model load **2.6 s** + first 8 s-window decode 2.5 s. Real-App (same 90 s Tagalog slice, STT `tl`): faster-whisper `small` first caption **16.5–17.4 s** (better than ggml-base's measured 25.0 s) but steady-state STT latency **13.7–15.8 s** vs ggml-base **2.4–3.7 s**. Window/interval tuning did not close the steady-state gap (1.0 s interval ≈ no change; 1.5 s worse at 24.2 s; 4 s window produced no captions) — the frozen 8 s/0.5 s config is already near-optimal for the faster-whisper path. Pre-warm would only save ~2.6 s. **Decision per user: `ggml-base` stays the production default; faster-whisper `small` int8 remains opt-in (`UC_STT_ENGINE=fasterwhisper`) until its steady-state latency can be materially reduced.** Accuracy winner: faster-whisper; responsiveness winner: ggml-base. No production change; evidence in `BENCHMARK_REPORT.md` (Slice 9 decision-gate) + `TEST_REPORT.md`.

## v0.5.12 - 2026-08-04

### Diagnostics

- **Slice 8 — Tagalog STT-vs-committer isolation + model-selection decision (2026-08-04, no production change).** The reported Tagalog live-caption defects (misrecognitions, fragmented finals, hallucinated `1.`) were isolated to the **STT layer**: RAW Whisper full-file segments already contain `Kung usta?`, `Ikao.`, `Salaman.`, hallucinated `1.` segments, and short 0.5–1.6 s boundaries; the committer aggregates them faithfully and does not manufacture cuts. ADR-0007 (commit/boundary/trim) is **not implicated** and remains **Proposed/frozen**.
- **Real-App model comparison** (UIA-driven Release App, same 90 s Tagalog slice, STT `tl`, frozen config st2 / 8 s / 0.5 s / 0.5 s, full `ProcessorCount` threads): `ggml-base` ~3.1 s STT latency but weak accuracy; `ggml-tiny` ~1.75 s fastest but no accuracy gain + worst fragmentation; `ggml-small` best Tagalog accuracy (`Kumusta`/`Ikaw`/`Salamat`/`Juan` correct, no `1.` hallucination) but **16.9–21.9 s** latency — cannot keep real-time pace. **Conclusion: no available local model gives both Tagalog quality and responsiveness. `ggml-base` remains the frozen default (ADR-0003); model exploration deferred.** Evidence: `artifacts/samples/raw_vs_committed_tagalog.log`, `realapp_{tiny,base,small}_tagalog.log`; findings in `docs/reports/BENCHMARK_REPORT.md` (Slice 8) + `docs/reports/TEST_REPORT.md`. Automated suite unchanged at **284/284**.

## v0.5.11 - 2026-08-04

### Fixed

- **ADR-0007 Option B — boundary-preserving fallback in `StreamingTranscriptCommitter` (2026-08-04).** Replaces the stability-only `stable → FINAL` commit and the original "commit the word-backed stable prefix at the 2 s cap" fallback (which live tracing proved force-committed interior fragments like `country can do for` with `boundary_found: false, fallback_used: true`) with four rules: (1) stable + meaningful segment boundary → FINAL; (2) stable + no boundary, budget running → wait; (3) budget expires + ≥1 completed boundary in the stable prefix → commit the last completed boundary only, keep the tail partial with a fresh timer; (4) budget expires + no completed boundary → keep partial, never manufacture a word-backed FINAL.
  - New `LastCompletedBoundaryLength(stable, segments)` (largest cumulative segment end `E ≤ stable.Length`) drives rule 3; `AdvanceCommittedUntil` still backward-snaps to real segment ends (I-1 preserved). `PendingStable` accessor added.
  - **Replacement-drop fix:** `UpdatePendingStable` now also receives `current`; when the stable prefix is empty (transient after a true replacement) it drops any pending that is no longer consistent with the current decode, so stale pending text is never committed against a new window's segments where its length could coincidentally land on a boundary.
  - Scope held: `FindTailOverlap`, `CaptionService` dedup, overlay, wrapping, Argos, `StabilityWindow`, `DecodeInterval`, and `CommittedUntilUtc` backward-snap untouched. `BoundaryWaitBudget` stays 2 s (provisional; not increased as a substitute fix).
- **Tests:** `StreamingTranscriptCommitterTests` rewritten for Option B (rule-3/rule-4 fallback, `CommittedUntilUtc` snap-to-boundary, replacement drop, epoch-rollover timer survival); `WhisperSpeechToTextEngineTests` migrated to a multi-segment `ScriptedSegmentDecoder` (Option B never commits interior prefixes in single-segment windows). Solution total **284/284 passing** (66 Audio + 72 Captions + 59 Speech + 27 Translation + 60 App), build 0 warnings/0 errors.
- **Live JFK verification (controlled English, PASS):** real App Release + `ggml-base`, `StabilityWindow=2`, steady config, real WASAPI loopback, `jfk_long.wav`. Run A (single pass) and Run B (continuous ~2 min loop) both commit complete boundary-backed sentences and **no longer emit the pre-fix `country can do for` interior fragment**; Stop drain preserves committed finals. Evidence `artifacts/samples/adv7_optionB_jfk.log`. (Pre-fix trace: `artifacts/samples/adv7_trace_evidence.log`, gitignored.)
- **ADR-0007 status:** remains **Proposed** — the controlled English JFK verification passes, but final acceptance still requires live validation of the original `"At gusto ko"` / `"Kaya"` / `"artipisyal na katalinuhan"` scenario against the **original operator recording**, which is unavailable; per user, no substitute Tagalog sample may be used to claim acceptance.

## v0.5.10 - 2026-08-02

### Fixed

- **Slice 7 — stable incremental caption rendering + scope-limited bottom scrolling (2026-08-02).** Addresses the reported "whole text reflows / newest content jumps" feel without touching the translation or STT path:
  - **Layout measurement (diagnosis first).** A deterministic STA layout probe (`CaptionLayoutProbeTests`) recreates the exact `ScrollViewer → Grid → StackPanel → TextBlock` tree at the real overlay width and confirms the caption `TextBlock` already measures and wraps correctly: a short utterance fills the full ~522 px viewport and stays on one line (it does not measure at its natural word width, which would cause the "two words → new line" symptom), a long sentence uses the full width and wraps only when width is exhausted, and growing tails keep a constant realized width (constancy across appends). Width is therefore not the cause of the reported reflow.
  - **Stable incremental render (A):** `UpdateCaptionItems`/`ReconcileHistory` now return whether a brand-new text block was inserted; a Partial only ever rewrites the existing live active line's `Text` in place — history `TextBlock` instances are reused by sequence and never rebuilt. A Final inserts the committed line as a fresh block and reuses the single live block for the next partial. Verified by new `CaptionRenderIdentityTests` (4) that drive the real `CaptionOverlayWindow` over STA/reflection and assert block-instance identity is preserved across Partial and Final steps.
  - **Scope-limited bottom scroll (C):** the overlay previously forced `ScrollToBottom` and re-ran the bottom re-anchor on every caption render. It now scrolls to the bottom only when a new block was actually inserted (a Final commit or the first line), and only when the content overflows the fixed-height viewport — a Partial that rewrites the live line never forces a scroll and never reflows history. The window's bottom re-anchor no longer runs per render (only on Loaded / collapse / hover toggles, where the window size actually changes), removing the per-partial window "jump".
- **Tests:** App tests 51 → **58** (3 layout probe + 4 render-identity). Solution total **267/267 passing** (66 Audio + 71 Captions + 45 Speech + 27 Translation + 58 App), build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean. Baseline defaults unchanged (`ggml-base`, `StabilityWindow` 2); Whisper/Argos/latency path untouched.

## v0.5.9 - 2026-08-02

### Added

- **Argos background pre-warm to remove the ~23-30 s cold-start from the first caption (2026-08-02).** Real measurement showed STT/audio/overlay are not the bottleneck: the first translation result arrived 24-34 s out of a ~28-34 s first-caption E2E time, driven by Argos cold start (Python interpreter import + language discovery/model load ≈ 18 s + first `en→tl` inference ≈ 5-12 s under Whisper CPU load). The fix warms Argos off the first real-caption path:
  - `ArgosTranslationEngine.TriggerPreWarmAsync(source, target)` — idempotent background warm-up: one shared `_warmTask` is reused while running/completed *for the same target language*; changing the target starts a fresh warm-up so the first caption in the new language is not cold.
  - `EnsureStartedAsync`/`StartCoreAsync` are rewritten so every caller (pre-warm and real translations) awaits a single shared `_startTask`; at most one process/initialization ever starts. If that shared start task faults, it is cleared and the next real translation re-creates the process instead of being handed a dead "completed" start.
  - A warm-up process error is swallowed and logged (never surfaced as a caption), and fatal kinds (timeout/unavailable/unknown) reset the shared start task so the real translation re-starts the local Argos process rather than losing the first caption (lazy start remains the fallback).
  - `ArgosTranslationEngineOptions.WarmUpText` (default `"The quick brown fox jumps over the lazy dog."`) — the one-time throwaway source text; it and its result stay purely local and are discarded.
  - `ControlWindow` kicks the Argos pre-warm fire-and-forget when translation is enabled with a target (`ApplyTranslationSettings`), and `App.xaml.cs` registers the engine as both the concrete singleton and `ITranslationEngine` so the control window and the caption service share one process/initialization.
- **Tests:** new `ArgosTranslationEngineTests` (26 → 27): pre-warm starts the process + sends one warm-up request, idempotency across concurrent triggers, real translation during warm-up reuses the shared start (StartCount == 1), fatal warm error → real translation re-starts the process (StartCount == 2), and target change → fresh warm-up. Solution total **260/260 passing** (66 Audio + 71 Captions + 45 Speech + 27 Translation + 51 App), build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, baseline defaults unchanged (`ggml-base`, `StabilityWindow` 2, window 8 s / interval 1 s).

## v0.5.8 - 2026-08-01

### Fixed

- **Overlay caption display — chronological order + no bottom clipping (2026-08-01).** `CaptionDisplayPolicy.ToDisplayModel` was reversing the committed history (`newest first`), so the newest caption sat at the *top*; it now renders the snapshot's history in natural order — oldest at the top, newest at the bottom (the highlighted/current caption is a separate `ActiveLine`, rendered in its own overlay row below the history). The overlay's hard height caps (window `MaxHeight="420"` and `HistoryList MaxHeight="120"` in `CaptionOverlayWindow.xaml`) were removed so the auto-sized pill grows to fit every rendered line: with caps present, WPF clipped the bottom of the history list, cutting off the newest committed caption (or the current line) once content exceeded the cap — capacity-based eviction of the oldest line remains the only bound. No transforms, z-index, absolute positioning, or scroll containers were involved; the height caps were the sole clipping mechanism.
- **Tests:** `CaptionDisplayPolicyTests` updated to the chronological contract and extended with deterministic cases — first caption alone, multiple captions in order, newest at the bottom, capacity eviction removing the oldest from the top, and partial→final append preserving order with no duplicate history entry. App tests now 50 (49 → 50), solution total **253/253 passing** (66 Audio + 71 Captions + 45 Speech + 21 Translation + 50 App), build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean. Baseline defaults unchanged (`ggml-base`, `StabilityWindow` 2).

## v0.5.7 - 2026-08-01

### Changed

- **Slice 6 closed out (2026-08-01).** All MVP slices (0–6) are now complete. Phases 1a (E2E latency metric + tests, 238/238), 1b (OFAT sweep + shortlist in `BENCHMARK_REPORT.md`), and 1c (App-level SAPI E2E validation in `TEST_REPORT.md` — baseline + shortlist × 3 runs each through the real App, every run publishing real translated Tagalog) are complete; the validated baseline `base/8/1/st2` is the App default (`StabilityWindow` 3→2, model `ggml-base` unchanged). A fresh-context review of the Phase 1a E2E metric code (CaptionLine translation timestamps, injectable clock, `EndToEndLatencyUpdated` partial/final samples, ControlWindow E2E row) was completed clean — no findings. Phase 2 real-app validation (YouTube/Chrome, VLC, Zoom) remains **deferred per user** as a future reassessment pass over the baseline defaults, not a prerequisite for the current release.

## v0.5.6 - 2026-08-01

### Changed

- **App default promoted to the validated Slice 6 baseline `base / 8 s / 1 s / st2`.** The authoritative `WhisperEngineOptions.StabilityWindow` default is **3 → 2**, and the App (`App.xaml.cs` `UC_STT_STABILITY` fallback) and the benchmark (`Program.cs` `--stability` fallback) both follow it — one authoritative configuration shared by App + benchmark, so future measurements are comparable against the real application configuration. Model default **`ggml-base` unchanged** (ADR-0003); window/interval defaults unchanged (8 s / 1 s).
- Rationale: Phase 1c App-level SAPI E2E validation (real WASAPI loopback → Whisper → Argos en→tl → WPF overlay, 3 runs per config) confirmed the OFAT shortlist — `st2` cuts first-final latency ~2 s with no full-file accuracy change, commits faster (base 16 vs 10 finals) with identical model accuracy, and keeps the conservative base model that already meets the accuracy target. Evidence in `docs/reports/TEST_REPORT.md` (Slice 6 Phase 1c) + `docs/reports/BENCHMARK_REPORT.md`.
- **Status label:** validated baseline for the current release. Real-application validation (YouTube/Chrome, VLC, Zoom) is **deferred per user**; defaults may be revisited after Phase 2.

## v0.5.5 - 2026-08-01

### Added

- **Slice 6 Phase 1a — end-to-end latency metric (change-impact Entry 8).** `CaptionLine` gains optional translation start/completion timestamps (`TranslationStartedAtUtc`, `TranslationCompletedAtUtc`, XML-doc'd; propagated through `WithPendingTranslation`/`WithTranslation`/`WithTranslationFailure`). `CaptionService` accepts an injectable clock (`utcNow`) and stamps the start at request dispatch and the completion **only when the result is actually applied** — stale/superseded/disabled-mid-flight results produce no timestamps and no update. `CaptionPipeline` raises a new `EndToEndLatencyUpdated` event (`EndToEndLatencySample` with `Partial`/`Final` kind) whenever a translated caption is published to subscribers: end-to-end latency = originating audio capture time (`CapturedAtUtc`) → translated caption published; translation latency = request start → published. `LatencyUpdated` (STT-final only) semantics are unchanged. The Control window shows a live "E2E latency" row (`partial: … ms · final: … ms`).
- **Slice 6 Phase 1b — benchmark parameterization + OFAT sweep.** `src/UniversalCaptions.Benchmarks` STT mode now accepts `--window <s>`, `--interval <s>`, `--stability <n>`, `--feed <realtime|fast>`, `--sample <name-substr>` (repeatable), and `--csv <path>` (RFC-4180 quoting incl. transcripts). The streamed pass records **streamed-finals WER** (concatenated committed finals vs reference) and streamed CPU. `--feed fast` is ingest-only (a whole clip arriving faster than realtime yields a single decode pass, so streaming finals require `realtime` pacing).
- **App benchmark overrides** (defaults unchanged): `UC_STT_WINDOW`, `UC_STT_INTERVAL`, `UC_STT_STABILITY` environment variables let the real App run a shortlisted configuration for Phase 1c validation without changing defaults.
- **OFAT sweep results recorded** in `docs/reports/BENCHMARK_REPORT.md` (Slice 6 section): base + tiny × jfk + OSR, sweeping window {6,8,10} s, interval {0.5,1,2} s, stability {2,3,5}. Findings: `StabilityWindow` dominates first-final latency (3→2 cuts ~2.1–2.4 s; 5 commits nothing on an 11 s clip); window/interval are secondary; streamed-finals WER is a commit-rate proxy, not accuracy (tiny commits more because it decodes faster, but full-file WER still favors base 4.9% vs 16% on OSR); streaming re-decodes the growing window every interval so streamed CPU ≈ 5× a single full-file pass. **Shortlist:** base/8 s/1 s/st2 (accuracy-first), tiny/8 s/1 s/st2 (latency-first), base/8 s/1 s/st3 (current default control). No App defaults changed.
- **Slice 6 Phase 1c — App-level SAPI E2E validation (evidence in `docs/reports/TEST_REPORT.md`).** Baseline + shortlist configs × 3 runs each through the real App (Release exe, WASAPI loopback → Whisper → Argos en→tl → overlay), driven by a UIA harness (SAPI-paced fixed English corpus; translation ON + target `tl`; the Phase 1a E2E latency row polled at 100 ms + 12 s settle). Every run published real translated Tagalog (0 misses). Results: **tiny/8/1/st2** is the latency winner end-to-end (E2E final median 16.25 s incl. Argos cold start; warm last-final 7.45 s; STT 3.61 s; 18 translated finals), **base/8/1/st2** ≈ baseline on E2E final but commits faster (16 vs 10 finals; STT 4.18 vs 6.49 s) with identical model accuracy, **base/8/1/st3** (control) is the conservative default. E2E final medians are inflated by the per-session Argos cold start (~14 s) on the first translated line; warm last-final E2E isolates steady state. **No App defaults changed** — any default change is a Must-Ask after Phase 2 (deferred per user). Raw series + per-run aggregates in `artifacts/reports/e2e/*.csv` (git-ignored).

### Changed

- Test count **224/224 → 238/238 passing** (66 Audio + **69** Captions + 41 Speech + 21 Translation + **41** App): new `CaptionLineTests` (6, timestamp propagation), `CaptionService` timestamp stamping tests (5: final/partial success, failure start-only, stale-result no-timestamps, disabled-mid-flight no-timestamps), and `CaptionPipeline` E2E tests (3: partial sample, final sample, no-sample for untranslated/failed). Build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean.

### Fixed

- Benchmark CSV writer now quotes fields containing commas (streamed/full transcripts), so columns parse correctly.

### Removed

- None

## v0.5.4 - 2026-08-01

### Changed

- **Entry 7 close-out (manual verification completed 2026-08-01):** the redesigned overlay (auto-sized pill, `TL` badge, chevron, hide button) and live active-line translation were verified end-to-end through the App against real WASAPI loopback audio + the real local Argos child process (target `tl`). SAPI-paced English speech was transcribed by Whisper `ggml-base` and **live-translated into Tagalog on the in-progress overlay line while the speaker was still talking, before commit** (observed pairs incl. "world"→`Daigdig`, "This is"→`Ito ay`, "translation"→`Pagsasalin`, "test"→`pagsubok`, and the full "The quick brown fox jumps over the lazy dog. Thank you for listening to the translation test."→`Ang mabilis na brown fox ay lumukso sa ibabaw ng tamad na aso. Salamat sa inyong pakikinig sa pagsubok sa pagsasalin.`); the `TL` badge stayed visible throughout. Overlay controls verified via UIA: chevron expands the committed history (all lines `IsTranslated = True`, pill 235→109 px on collapse), close hides the overlay, ControlWindow "Show Captions" re-shows it, and speech while hidden still produced a fresh live-translated active line ("The meeting starts at nine o'clock"→`Nagsisimula ang pulong sa alas - 9.`). Full timed sample timeline + evidence in `TEST_REPORT.md` (Slice 5 refinement note). `docs/CHANGE_IMPACT_ANALYSIS.md` Entry 7 marked Completed; `PROJECT_STATUS.md` updated.
- Test count remains **224/224 passing** (66 Audio + 58 Captions + 41 Speech + 21 Translation + 38 App), build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean.

## v0.5.3 - 2026-08-01

### Added

- **Live active-line translation** (supersedes the "active line = verbatim source only" half of the Entry 6 Q1 resolution): the in-progress caption line is now translated in the target language as the speaker is still talking, so the overlay reads in the target language before the utterance commits. `CaptionService` uses a **single in-flight slot** (`MaybeStartActiveLineTranslation` → `RunActiveLineTranslationAsync` → `ApplyActiveLineTranslation`): at most one active-line translation runs at a time (the Argos backend serializes requests and is torn down on cancellation), the slot self-replenishes to translate a newer partial that arrived meanwhile, and results are stale-guarded by line-instance identity via the new `CaptionState.ReplaceActiveLine`. A result whose request started before translation was disabled is discarded, never applied; a result for a superseded partial is discarded.
- **Chrome-style overlay redesign**: `CaptionOverlayWindow` is now an auto-sized translucent pill (`SizeToContent="WidthAndHeight"`, rounded dark chrome, white caption text) with a target-language badge (e.g. `TL` when translation is on), an expand/collapse chevron that reveals/ hides the committed history, and a close button that hides the overlay (re-shown via the control window). The font-size slider now scales history text too (inherited attached property; local template `FontSize` removed).
- **ControlWindow "Show Captions" button** — re-shows a closed/hidden overlay; **Start Captions** also re-shows it.
- Tests: `CaptionState.ReplaceActiveLine` (4), `CaptionService` live active-line translation (9: translate-on-partial, off-makes-no-request, failure-preserves-source, single-slot serialization + self-replenish, stale-result discard, discard-on-commit, disabled-mid-flight discard, updated event, enable-mid-session translates current partial), `CaptionDisplayPolicy` language badge (2).

### Changed

- Test count from 209/209 → **224/224 passing** (66 Audio + 58 Captions + 41 Speech + 21 Translation + 38 App), build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean.
- `docs/CHANGE_IMPACT_ANALYSIS.md` Entry 7 added; display-policy documentation updated (active line = latest partial, live-translated when translation is on).

### Fixed

- Fresh-context review findings (2026-08-01): the overlay font-size slider was not scaling history text (local `FontSize` in the `ItemsControl` template overrode the inherited attached property) — removed the local value; a translation that completes after translation was disabled mid-flight is now discarded rather than applied.

### Removed

- None

## v0.5.2 - 2026-08-01

### Added

- **Slice 5 close-out (2026-08-01): real-Argos wiring verified end-to-end through the App.** Recreated the dev Argos venv (`argostranslate==1.11.0` + en→tl, tl→en, ja→en, en→ja packages) under a short 8.3 temp path (`C:\Users\TOGODB~1\AppData\Local\Temp\argosv`) per TD-011, prepended its `Scripts` dir to PATH, and verified the live App: toggled translation ON, selected **Tagalog (tl)**, started captions, played speech via SAPI, and **committed overlay lines displayed real translated Tagalog** (`tamad aso.`, `Ang mabilis na kayumangging sorra ay tumatalon sa ibabaw ng eruplano`, `IsTranslated = True`) served by the App-spawned Argos child process (`python` venv shim → UV base python running `argos_translate_server.py`). Evidence recorded in `docs/reports/TEST_REPORT.md` (Slice 5).
- `ControlWindow.ApplyTranslationSettings` guard-path fix (exercised by the live run): on a guard rejection (e.g. `en`→`en`), the translation toggle now **stays ON** and the target combo **stays enabled** so the user can select a valid target; the rejection is surfaced in the status line only.

### Changed

- Slice 5 marked **complete** in `TEST_REPORT.md`, `PROJECT_STATUS.md`, `ROADMAP.md`, `BUILD_PLAN.md`, and `CHANGE_IMPACT_ANALYSIS.md` Entry 6. Test count remains **209/209 passing**, build 0 warnings/0 errors.

### Fixed

- None

### Removed

- None

## v0.5.1 - 2026-08-01

### Added

- Slice 5 fix round (fresh-context review of `UniversalCaptions.App`), closing out M1–M4 + Low-7/8/9:
  - `Controls/AudioSourceLoader.cs` — device enumeration wrapped with a preferred-default and failure-surfacing (`AudioSourceLoaderTests` 4)
  - `Controls/TranslationGuard.cs` — source-equals-target rejection (case-insensitive) with a user-readable status message (`TranslationGuardTests` 4)
  - `Core/Captions/CaptionSnapshot.cs` + `CaptionService.GetSnapshot` — immutable snapshot of active line + history (`CaptionSnapshotTests` 5); thread-safe reads concurrent with mutations
  - `Pipeline/CaptionPipeline.cs` — teardown ordering (`Stop` returns before component teardown completes; `Dispose` waits), fail-on-start teardown paths, audio-processing-exception surfacing (`CaptionPipelineTests` 20)
  - `ControlWindow.xaml.cs` / `Overlay/CaptionOverlayWindow.xaml.cs` / `App.xaml.cs` — wiring and UI updates for the above
- **Manual overlay/device verification completed 2026-08-01** (recorded in `docs/reports/TEST_REPORT.md`): real system audio → Whisper `ggml-base` → live overlay captions; always-on-top/transparency; drag/resize/click-through; stop/restart; rapid Stop→close (clean ~2 s exit); model-not-found error path; source-equals-target rejection live.
- GitHub repository published: `git@github.com:marcgregory/universal-live-captions.git` (branch `main`; `master` removed).

### Changed

- Test count from 190/190 → **209/209 passing** (66 Audio + 45 Captions + 41 Speech + 21 Translation + 36 App), build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, no vulnerable packages. Real-Argos wiring was pending at this release and was closed out in v0.5.2.

### Fixed

- Slice 5 review findings (2026-08-01): `CaptionPipeline.Start()` not synchronized with `Stop`/`Dispose` at the reusable-class level (real race, not reachable through the current UI's button gating — overlaps deferred Low-6/TD-014; logged as M-1, deferred by decision); orphaned teardown-task overwrite acceptable (L-1); teardown exceptions swallowed by design (L-2); non-COM enumeration exceptions not caught (L-3); translation source-language decoupling pre-existing (L-4). No Critical/High findings; no Slice 5 blockers.

### Removed

- None

## v0.5.0 - 2026-08-01

### Added

- Slice 5 — WPF overlay + control window (`UniversalCaptions.App`; **complete in v0.5.2**) — implementation + unit tests in this release
- `src/UniversalCaptions.App` (new WPF project, `net8.0-windows`, `UseWPF`, `app.manifest` with PerMonitorV2 DPI, `Microsoft.Extensions.DependencyInjection` 8.0.0) — the DI composition root wiring capture → processor → STT → caption service → overlay:
  - `Overlay/IOverlayService.cs` — overlay state contract (visibility, position, opacity [0.2, 1.0], font size [10, 96], click-through, `Show`/`Hide`/`ShowAt`)
  - `Overlay/CaptionDisplayModel.cs` + `CaptionDisplayPolicy` — Q1 display-policy resolution: the active caption is rendered verbatim from the latest partial (`CaptionState.ActiveLine`); committed finals render as bounded history (newest first); translated text replaces the source on a committed line only when `CaptionTranslationStatus.Completed`, otherwise the source is preserved (PRD FR-5/FR-14)
  - `Overlay/CaptionOverlayWindow.xaml(.cs)` — borderless/transparent/always-on-top overlay, history `ItemsControl` + active line, drag + resize grip, opacity/font size, click-through via `WS_EX_TRANSPARENT` (`Overlay/NativeMethods.cs`), dispatcher-coalesced rendering of `ICaptionService` events
  - `Pipeline/CaptionPipeline.cs` + `Pipeline/PipelineStatus.cs` — wiring controller: `Func` factories for capture/STT (DI + test seams), idempotent `Start`/`Stop`/`Dispose`, `StatusChanged`/`LatencyUpdated` events, error handling that stops a running session but defers teardown during startup
  - `Controls/ControlWindow.xaml(.cs)` — audio source + language selection, translation on/off + target, capture/status/latency indicators, opacity/font-size sliders, click-through toggle, Start/Stop; consumes pipeline events on the dispatcher
  - `App.xaml.cs` — DI registration (Argos → CaptionService → AudioProcessor → capture/STT factories → CaptionPipeline → overlay + control window); `ShutdownMode.OnMainWindowClose`
- `tests/UniversalCaptions.App.Tests` (new, `net8.0-windows`, UseWPF): `CaptionDisplayPolicyTests` (8 — null/active, translated-replaces vs source-preserved on not-requested/pending/failed, history order, empty) + `CaptionPipelineTests` (14 — fakes at the capture/STT boundaries: audio flow, format conversion, partial/final flow, latency, capture/recognition/factory errors, stop/dispose, chunks-after-stop ignored)
- `.slnx`: added `src/UniversalCaptions.App` and `tests/UniversalCaptions.App.Tests`

### Changed

- `docs/CHANGE_IMPACT_ANALYSIS.md` Entry 6 — Slice 5 kick-off + Q1 display-policy resolution (verbatim latest partial as the active line; committed finals as history; translated text replaces source only when translation completes)
- `docs/ARCHITECTURE.md`, `docs/REPOSITORY_STANDARDS.md`, `docs/TECH_STACK.md`, `docs/DEPLOYMENT.md` — App composition root, `IOverlayService`, overlay/control-window boundaries, `dotnet run --project src/UniversalCaptions.App`
- Slice 5 implementation + unit tests complete (2026-08-01): **190/190 tests passing** (66 Audio + 41 Speech + 21 Translation + 40 Captions + 22 App), build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, no vulnerable packages (all 13 projects). Manual overlay/device and real-Argos verification **pending** — Slice 5 not yet closed out.

### Fixed

- Slice 5 implementation bugs found by build/test: XAML MC3089 (single-child `Border` → nested `Grid` for the resize grip), missing `using System.IO`, event-handler overload ambiguity (`EventHandler<CaptionLine>` vs `EventHandler<CaptionState>` split into two handlers), `e.ChangedButton` → `e.ButtonState`, `OverlayRoot.FontSize` → `TextElement.SetFontSize`, `CaptionPipeline.Dispose` ordering (`Stop()` before `_disposed = true`), test-harness init-order (processor injected as a constructor parameter, not an object initializer)

### Removed

- None

## v0.4.0 - 2026-08-01

### Added

- Slice 4 — caption service (complete)
- Caption contracts in `UniversalCaptions.Core` (namespace `UniversalCaptions.Core.Captions`): `ICaptionService` (transcript/translation events → caption state, `ProcessPartial`/`ProcessFinal`, `Start`/`Stop`/`Reset`, `SetTranslationEnabled`, `FlushAsync` for in-flight translations, state-change events), `CaptionLine` (immutable: text/translated text, source/target language, sequence, timestamps, `CaptionLineState` Active/Final, `CaptionTranslationStatus` NotRequested/Pending/Completed/Failed, `WithTranslation`/`WithPendingTranslation`/`WithTranslationFailure`), `CaptionState` (active line, bounded sequence-ordered history with duplicate re-delivery handling, translation-enabled state, session lifecycle), `CaptionServiceOptions` (source/target language, history capacity)
- `src/UniversalCaptions.Captions`: `CaptionService` — partials replace the active line, finals commit to history, optional background translation (result/failure applied without ever replacing the source text; stale results matched by line identity so a re-committed line is not overwritten), cancellation of in-flight translations on stop/reset/dispose, gate-serialized state with events raised outside the lock and snapshot `History`
- `tests/UniversalCaptions.Captions.Tests`: `CaptionState` tests (ordering, duplicate replace, bounded history, active-line lifecycle, translation config, session lifecycle) and `CaptionService` tests with deterministic `StubTranslationEngine`/`GatedTranslationEngine` (partial/final handling, translation on/off, translation failure preserves source, unexpected engine failure does not break the pipeline, stale translation guard, cancellation on stop/reset, bounded history, events) — 40 tests
- `.slnx`: added `src/UniversalCaptions.Captions` and `tests/UniversalCaptions.Captions.Tests`

### Changed

- `docs/ARCHITECTURE.md`, `docs/implementation/BUILD_PLAN.md`, `docs/implementation/PROJECT_STATUS.md`, `docs/implementation/ROADMAP.md`, `docs/reports/TEST_REPORT.md` updated for the Captions package (contracts in Core per ADR-0003/0006 precedent, so `UniversalCaptions.Captions` depends only on Core)
- Slice 4 recorded as **Completed** (close-out approved 2026-08-01): all Definition-of-Done items satisfied (0 warnings/errors, 168/168 tests, format clean, no vulnerable packages, fresh-context review findings fixed, docs finalized)

### Fixed

- Fresh-context review fixes (2026-08-01): `CaptionState.History` returned as a snapshot (bounded read without holding the lock); CTS deferred disposal (no `ObjectDisposedException` on stop/reset/dispose); atomic translation-start token (in-flight translations cancellable and tracked); stale-translation identity guard (a re-delivered/re-committed line is not overwritten by an out-of-order translation result); event-raising moved out of the translation catch (state events raised outside the gate, never inside a catch); target language normalization (canonical code form before requests and state)

### Removed

- None

## v0.3.0 - 2026-08-01

### Added

- Slice 3 — Translation spike
- Translation contracts in `UniversalCaptions.Core` (namespace `UniversalCaptions.Core.Translation`): `ITranslationEngine` (`TranslateAsync(text, sourceLanguage?, targetLanguage, CancellationToken)`), `TranslationResult` (text, source/target language, detected source, pivot metadata, timestamps + latency, sequence), `TranslationErrorKind`, `TranslationException`
- `src/UniversalCaptions.Translation`: `ArgosTranslationEngine` (owns a child Python process, lazy startup, input validation, error mapping, cancellation), `ArgosTranslationEngineOptions`, `Argos/LineProtocolArgosProcess` (newline-delimited JSON over stdin/stdout, ping-ready handshake, request timeout, process kill on timeout/dispose, stderr capture), `Argos/argos_translate_server.py` (bundled server: direct translation, auto-detect-unavailable handling, `CompositeTranslation` pivot detection, error kinds, BOM tolerance)
- `tests/UniversalCaptions.Translation.Tests`: `FakeTranslationEngine` (contract test seam with registered outputs, latency, failures, pivot metadata, call log), `FakeArgosProcess` (engine seam), contract tests (8) and `ArgosTranslationEngine` tests (13, incl. restart-after-fatal-error) — 21 tests
- `UniversalCaptions.Benchmarks` `translate` mode: per-pair load/first-latency, steady-state distinct-text latency, identical-input cache latency, throughput, Argos child-process working set (WMI), finals-stream ordering/latency, char-similarity quality vs reference; `--python`, `--iterations`, `--no-quality`
- Translation benchmark evidence: `docs/reports/BENCHMARK_REPORT.md` (Slice 3 section)

### Changed

- ADR-0006 refinement (documented): translation contracts live in `UniversalCaptions.Core` (not `UniversalCaptions.Translation`), so `UniversalCaptions.Captions` (Slice 4) depends only on Core; `UniversalCaptions.Translation` owns `ArgosTranslationEngine` and its process seam
- ADR-0006: deferred pair/process-protocol selection resolved with Slice 3 evidence (line protocol, direct pairs `en↔tl`/`ja↔en`/`en→ja`, pivoting via `en` verified, `tl`-as-source limitation noted)
- `.slnx`: added `src/UniversalCaptions.Translation` and `tests/UniversalCaptions.Translation.Tests`
- `docs/REPOSITORY_STANDARDS.md`, `docs/ARCHITECTURE.md`, `docs/TECH_STACK.md` updated for the Translation package and dependency boundaries

### Fixed

- `argos_translate_server.py`: `_installed_languages` returned a 2-tuple only on the failure path (unpacked as 2 values on success too) — now consistently returns `(languages, None)`
- `argos_translate_server.py`: accepts a leading UTF-8 BOM on the first input line (C# `StreamWriter` emits none, but hardened for stdin consumers)
- `TranslationBenchmark`: identical input was served from Argos's internal cache, making steady-state latency read ~0 ms — now measures distinct texts plus a separate cached-repeat figure
- Fresh-context review fixes (2026-08-01): stale-process brick after kill (process/reader/writer now cleared on kill so a later call relaunches; engine resets `_started` on fatal errors — regression test added); unwrapped `JsonException`/`IOException`/`ObjectDisposedException` now mapped to `TranslationException`; Python server no longer dies on malformed requests (request id/target validated, `get_translation` guarded); `ArgosTranslationEngineOptions` validates positive timeouts; `ProcessStartInfo.ArgumentList` replaces string-built args; UTF-8 pinned via `PYTHONIOENCODING`/`PYTHONUTF8`

### Removed

- None

## v0.2.0 - 2026-07-31

### Added

- Slice 2 — STT spike
- Streaming STT contracts in `UniversalCaptions.Core` (namespace `UniversalCaptions.Core.Speech`): `ISpeechToTextEngine` (events `PartialTranscriptAvailable`/`FinalTranscriptAvailable`/`RecognitionFailed`, `Start`/`Stop`/`Process`), `SpeechTranscript` (base: `Text`, `CapturedAtUtc`, `EmittedAtUtc`, `Sequence`, `Confidence?`, `Latency`), `PartialTranscript`, `FinalTranscript`, `SpeechRecognitionErrorKind`, `SpeechRecognitionError`, `SpeechRecognitionException`
- `src/UniversalCaptions.Speech`: `WhisperSpeechToTextEngine` (sliding-window streaming over whisper.cpp via Whisper.net 1.9.1, background decode loop, partial/final commit), `WhisperEngineOptions`, `StreamingTranscriptCommitter`, `TranscriptSegment`; engine implements `IAsyncDisposable`
- `src/UniversalCaptions.Benchmarks`: console harness benchmarking ggml-tiny/base (load time, RAM, decode realtime factor, streaming first-partial/final latency, WER) over four samples — `jfk.wav`, `jfk_noisy.wav` (jfk + 10 dB SNR white noise), `jfk_long.wav` (jfk×2 + pause), `OSR_us_000_0010_8k.wav` (conversational) — with ggml-small as OSR pseudo-reference; auto-downloads models and samples into git-ignored `artifacts/`
- `tests/UniversalCaptions.Speech.Tests`: `FakeSpeechToTextEngine` + contract tests (14), fake-engine tests (4), `StreamingTranscriptCommitter` stability tests (10), `WhisperSpeechToTextEngine` tests with injected deterministic decoder (13, incl. decode→Stop→DisposeAsync regression) — 41 tests
- `WhisperEngineOptions`: `StabilityWindow` (min 2, default 3), opt-in `MaxSegmentLength` and `SplitOnWord` (default off, benchmark before enabling)
- Benchmark evidence: `docs/reports/BENCHMARK_REPORT.md`

### Changed

- ADR-0003 refinement (documented): STT contracts live in `UniversalCaptions.Core` (not `UniversalCaptions.Speech`), so `UniversalCaptions.Captions` (Slice 4) depends only on Core; `UniversalCaptions.Speech` owns concrete engines
- `.slnx`: added `src/UniversalCaptions.Benchmarks` and `tests/UniversalCaptions.Speech.Tests`
- `StreamingTranscriptCommitter`: rewritten around a stability window (partial → stable → final) with word-boundary common-prefix back-off and epoch reset; `Update(segments, windowStartUtc)`; `WhisperSpeechToTextEngine` uses a growing-window epoch loop that trims only audio committed/past the overlap, so in-progress hypotheses survive
- `WhisperEngineOptions.CommitOverlap` meaning refined: audio at the end of each window is always kept (never trimmed)
- Default Whisper model set to **ggml-base** (user-approved; ggml-tiny kept as a low-resource fallback) — ADR-0003 finalized with OSR quality evidence (base 4.9% vs tiny 16.0% WER)

### Fixed

- `WhisperSpeechToTextEngine.Dispose`: Whisper.net 1.9.1 `WhisperProcessor.Dispose()` throws `"Cannot dispose while processing"` if a native decode is in flight; engine now implements `IAsyncDisposable` and disposes the processor via `DisposeAsync()`, with sync `Dispose()` blocking on it
- Streaming emitted partials but **no finals** (whisper.cpp yields single whole-window segments): resolved by the stability-based committer — real-model streaming now commits finals on every benchmark sample (see BENCHMARK_REPORT.md)
- `StabilityWindow < 2` rejected at construction (would force every hypothesis to final)

### Removed

- None

## v0.1.0 - 2026-07-31

### Added

- Bootstrapped repository with enterprise-project-bootstrap v3.0.0 governance and MVP documentation
- `UniversalCaptions.slnx` solution with `src/UniversalCaptions.Core`, `src/UniversalCaptions.Audio`, `src/UniversalCaptions.Diagnostics`, `tests/UniversalCaptions.Audio.Tests`
- NAudio 2.2.1 WASAPI loopback capture dependency
- Audio contracts in Core: `AudioFormat`, `AudioChunk`, `AudioCaptureError`, `IAudioCapture`, `IAudioBuffer`, `IAudioProcessor`, `IVoiceActivityDetector`
- Audio implementations: `WasapiLoopbackCaptureSource`, `LoopbackDeviceEnumerator`, `ByteToFloatConverter`, `PcmRingBuffer`, `SampleRateConverter`, `EnergyVad`, `AudioLevelMeter`
- Diagnostic console app for live audio meter
- ADRs 0001–0005

### Changed

- Product requirement refined: live local/offline translation of the source transcript is a core feature (PRD FR-12…FR-15); slice order updated to Audio → STT → Translation → Captions → Overlay → E2E
- Translation stack decision: `ITranslationEngine` abstraction with Argos Translate as the first engine, isolated behind a local process (ADR-0006)

### Fixed

- `SampleRateConverter`: kernel loop and ring buffer produced phase-skewed output (1000 Hz sine read as ~889 Hz); rewritten as a sliding-window resampler with explicit frame eviction and pre-stream zero padding. Output now measures 1000.32 Hz for a 1000 Hz input.
- `ByteToFloatConverter`: replaced non-existent `AudioSubTypes` with `NAudio.MediaFoundation.AudioSubtypes` GUIDs; 24-bit negative conversion now sign-preserving (`(value >> 8) / 8388608f`).
- `LoopbackDeviceEnumerator`: removed invalid `using` on `MMDeviceCollection` (not `IDisposable` in NAudio 2.2.1); disposes each `MMDevice`; `GetDefaultRenderEndpoint` handles `COMException`.
- `WasapiLoopbackCaptureSource`: added missing usings; HResult failure mapping (0x88890004 → Disconnected, 0x8889000A → Unavailable).
- `SampleRateConverterTests`/`ByteToFloatConverterTests`: frequency-preservation tests now measure ~1000 Hz; float/extensible-format tests build the native `WAVEFORMATEXTENSIBLE` via `WaveFormat.MarshalFromPtr` and use `CreateIeeeFloatWaveFormat` for float formats.

### Removed

- None
