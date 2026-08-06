# Universal Live Captions Build Plan

Last updated: 2026-08-06

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define sprint execution plans, tasks, and delivery milestones |
| Scope | Active and future sprints, implementation tasks |
| Audience | Engineering |
| Owner | Engineering |
| Status | Active |
| Related Documents | [ROADMAP.md](ROADMAP.md), [RELEASE_PLAN.md](RELEASE_PLAN.md), [TECHNICAL_DEBT.md](TECHNICAL_DEBT.md), [PROJECT_STATUS.md](PROJECT_STATUS.md), [QUALITY_ASSURANCE.md](../QUALITY_ASSURANCE.md) |

---

Only one sprint may be active. Each sprint must produce a visible feature, a working demo, updated documentation, a passing build, and passing tests.

## Completed Sprint: Slice 1 — Audio Capture Spike (2026-07-31)

Status: **Complete** — 66/66 tests passing, real-device capture verified and recorded in `TEST_REPORT.md`, CHANGELOG/PROJECT_STATUS updated.

### Goal

Prove that the application can detect and receive Windows system audio through WASAPI loopback without VB-CABLE, and surface it through a diagnostic meter.

### Scope

- `IAudioCapture`, `IAudioBuffer`, `IAudioProcessor`, `IVoiceActivityDetector` contracts in `UniversalCaptions.Core`
- `WasapiLoopbackCaptureSource` (NAudio loopback) in `UniversalCaptions.Audio`
- PCM byte → float conversion, PCM ring buffer, sample-rate conversion (windowed-sinc resampler), energy-based VAD, level meter
- `UniversalCaptions.Diagnostics` console app: lists output devices and renders a live meter from loopback audio
- Unit tests for all of the above using a fake `IWaveIn` at the capture boundary

### Dependencies

- .NET 8 SDK (installed), NAudio 2.2.1 (installed), a Windows 10 machine with an active audio output device for manual verification

### Tasks

1. Define `AudioFormat`, `AudioChunk`, `AudioCaptureError`, `IAudioCapture`, `IAudioBuffer`, `IAudioProcessor`, `IVoiceActivityDetector` in `UniversalCaptions.Core`
2. Implement `ByteToFloatConverter`, `PcmRingBuffer`, `SampleRateConverter`, `EnergyVad`, `AudioLevelMeter` in `UniversalCaptions.Audio`
3. Implement `WasapiLoopbackCaptureSource : IAudioCapture` wrapping NAudio `IWaveIn`
4. Implement `LoopbackDeviceEnumerator` for output-device discovery
5. Build the diagnostics console: device list + live meter (RMS/peak, format, timestamps)
6. Write `UniversalCaptions.Audio.Tests` (buffering, conversion, resampling, VAD, meter, capture source with fake `IWaveIn`, failure/recovery mapping)
7. Verify build + tests; run the diagnostics console against real system audio; record evidence in `TEST_REPORT.md`

### Definition of Done

- All Slice 1 tasks complete
- `dotnet build` succeeds with 0 warnings (warnings-as-errors)
- All automated tests pass with execution evidence
- Diagnostics console captures real system audio (manual verification recorded)
- No VB-CABLE involved
- Docs updated (CHANGELOG, PROJECT_STATUS, TEST_REPORT)

### Acceptance Criteria

- [ ] Capture source starts, receives PCM, and stops cleanly
- [ ] Conversion handles 8/16/24/32-bit int and 32/64-bit float PCM
- [ ] Ring buffer preserves ordering and handles wrap-around/overflow
- [ ] Resampler preserves frequency content within tolerance (tested)
- [ ] VAD distinguishes silence from speech deterministically (tested)
- [ ] Level meter reports RMS/peak per chunk (tested)
- [ ] Device-disconnect/init failure maps to a user-readable error (tested via fake `IWaveIn`)
- [ ] Manual run shows live meter from system audio

### Demo

Run `dotnet run --project src/UniversalCaptions.Diagnostics` with audio playing in Chrome/VLC and observe the live meter moving.

## Sprint Queue

### Slice 10 — Faster-Whisper Native Streaming

Status: **Complete (2026-08-05)** — change-impact Entry 11. Engine + detector implemented; 357/357
tests; benchmark + real-App validation PASSED and the decision gate is recorded (see Definition of Done
and `TEST_REPORT.md` Slice 10 section).

#### Goal

Replace the stale ~40 s faster-whisper commit cadence with a streaming architecture: C#-side VAD
speech-segment detection commits one coherent FINAL per completed speech segment through the
**existing** faster-whisper worker wire protocol. Isolated experiment behind
`UC_STT_ENGINE=fasterwhisper-native`; the ggml-base production default, the windowed `fasterwhisper`
engine, the worker protocol, and ADR-0007 all stay untouched.

#### Scope

- `UniversalCaptions.Speech` — new public `FasterWhisperNativeStreamingEngine` (`ISpeechToTextEngine`,
  no live partials; one FINAL per segment), internal `SpeechSegmentDetector` state machine
  (MinSpeechDuration / SilenceHangover / MaxSegmentDuration), reuses `FasterWhisperEngineOptions` for
  process/model fields. Composes the Core `IVoiceActivityDetector` (Speech does not reference Audio).
- `UniversalCaptions.App` — additive `UC_STT_ENGINE=fasterwhisper-native` selector branch wiring
  `EnergyVad` + segment knobs.
- Tests — deterministic only, no Python: `SpeechSegmentDetectorTests` on synthetic PCM; engine tests
  against scripted VAD + scripted `IFasterWhisperProcess`.
- Validation (after deterministic tests pass) — benchmark/real-App run with `fasterwhisper-native` on
  the actual video audio vs the `fil-orig` reference, plus live App run.

#### Definition of Done

- [x] `FasterWhisperNativeStreamingEngine` + `SpeechSegmentDetector` implemented; selector branch added
- [x] Deterministic tests pass (segment detector state machine + engine, no Python) — 357/357 total,
      21 new; Release build 0 warnings/0 errors; format clean; no vulnerable packages
- [x] Fresh-context review completed — PASSED with fixes (segment-duration accounting, session-epoch
      stale-FINAL guard, broadened Start error mapping, option validation, engine-level cap test)
- [x] Benchmark + real-App validation **PASSED (2026-08-05)**: controlled benchmark (`sttnative` mode,
      realtime feed) + real-App run with `fasterwhisper-native` (small int8, tl) on the actual video
      audio vs the `fil-orig` reference — committed WER **32.6%** (ggml-base 51.2%); **0 partials
      (FINAL-only)**; commit cadence **13.3 FINALs/120 s** (windowed faster-whisper: 2/120 s); first
      real-App caption **15.2 s**; STT latency 11.6–12.9 s from segment start ≈ **~4 s behind segment
      end** with no growing backlog; no recurring `(Song)`/`(Subscribe)` hallucinations; music gaps
      produce no captions; one coherent FINAL per segment, no duplicates/re-emissions; no dropped final
      at Stop; translation path untouched (this run was STT-only)
- [x] Docs + TEST_REPORT updated with real-App evidence — Slice 10 section, CHANGELOG v0.5.19,
      PROJECT_STATUS, CHANGE_IMPACT_ANALYSIS Entry 11
- [x] Decision-gate recorded (2026-08-05): **question answered — segment-based native streaming
      preserves faster-whisper small's accuracy advantage (32.6% vs 51.2%) while eliminating the stale
      20–40 s commit backlog** (one fresh FINAL per ~8.2 s segment, ~4 s behind segment end, FINAL-only).
      faster-whisper stays **opt-in** (`UC_STT_ENGINE=fasterwhisper-native`); the ggml-base production
      default is unchanged (frozen). Documented tradeoff: the 8 s segment cap can split sentences
      mid-word (tunable via `UC_NATIVE_MAX_SEGMENT`). Promotion to production default is out of scope
      (freeze) and would be a separate decision.


### Slice 11 — Native-Streaming Segment-Boundary Tuning

Status: **Complete (close-out 2026-08-05)** — change-impact Entry 12. Scoped per user after the Slice 10 PASS.
Follow-up tuning of the opt-in `fasterwhisper-native` segment boundaries. Goal is not "lower WER" but
**accurate + natural sentence boundaries + bounded live latency**, the legitimate basis for a future
default-selection decision.

#### Goal

Test `UC_NATIVE_MAX_SEGMENT` around **8 / 10 / 12 s**; measure whether longer segments reduce
mid-sentence splits; check that latency/backlog remains bounded; keep `SilenceHangover = 0.7 s` as the
baseline. No worker-protocol / ggml-base / windowed-engine changes.

#### Scope

- `UniversalCaptions.Benchmarks` `sttnative` — additive: fix the realtime-feed timer-granularity
  artifact (`timeBeginPeriod(1)` around the feed so controlled latencies are valid) and add a
  mid-sentence-split (continuation) counter for a quantified boundary metric.
- `UniversalCaptions.Speech` / `UniversalCaptions.App` — **may** update the native engine's
  `MaxSegmentDuration` knob default (8 → 10/12) to the sweep winner (opt-in engine only).
- Validation — controlled `sttnative` sweep 8/10/12 s on the actual video audio vs the `fil-orig`
  reference, then one real-App run on the winner.

#### Definition of Done

- [x] `sttnative` sweep runs at max-segment 8/10/12 s (hangover 0.7 s fixed) — controlled, valid pacing
- [x] Boundary metric + WER + cadence + emit-lag/backlog recorded per cap
- [x] Winner validated — the winner is **keep 8 s**, already real-App validated in Slice 10
      (`realapp_native_streaming.log`); no redundant re-run required
- [x] Decision recorded — **keep 8 s** (`MaxSegmentDuration` default unchanged): longer segments do
      not reduce mid-sentence splits (31% → 42% → 45% of FINALs), cost responsiveness (9.1 vs 13.3
      FINALs/120 s) and add end-of-audio cap hallucinations (`Pag-pag-pag…` at 10 s, truncated `tunog`
      at 12 s); the small 12 s WER gain (30.0% vs 32.6%) is a boundary artifact, not a decoding gain.
      Latency/backlog bounded at all three caps. No production or knob-default change; ggml-base
      default untouched. Docs + TEST_REPORT + BENCHMARK_REPORT updated (CHANGELOG v0.5.20).


### Slice 12 — Faster-Whisper Native-Streaming Live Partials

Status: **Complete (close-out 2026-08-05)** — change-impact Entry 13. Scoped per user after the Slice 10/11
gates closed with the "one FINAL per completed segment, 0 live partials" tradeoff.

#### Goal

Chrome-Live-Caption-style incremental text on the opt-in `fasterwhisper-native` engine: live partial
captions while the speaker is still talking, one FINAL per completed segment (unchanged). No
wire-protocol change, translation OFF, ggml-base untouched. Key measurement = **first visible partial
latency** (not first FINAL).

#### Scope

- `UniversalCaptions.Speech` — additive: `SpeechSegmentDetector.TryGetPartial(maxSamples, out samples,
  out capturedAtUtc)` (bounded trailing-window snapshot; refused while idle/hangover/after close);
  `FasterWhisperEngineOptions.PartialDecodeInterval` (default 0 = FINAL-only preserved) /
  `PartialDecodeWindow` (4 s); `FasterWhisperNativeStreamingEngine` cadence dispatch with at most one
  partial decode in flight/queued (no backlog; ticks deferred, not queued), partials cleared on FINAL,
  `PartialTranscriptAvailable` event. Partials flow the existing CaptionService/overlay active-line path.
- `UniversalCaptions.App` — additive knobs `UC_NATIVE_PARTIAL_INTERVAL` (1 s) /
  `UC_NATIVE_PARTIAL_WINDOW` (4 s) for the `fasterwhisper-native` branch.
- `UniversalCaptions.Benchmarks` — `sttnative` gains `--partial-interval`/`--partial-window`, first-partial
  /first-caption-lag/partial-cadence/lag-distribution metrics, CSV partial table + summary columns.
- Validation — one controlled real-audio run with partials ON vs the Slice 11 FINAL-only baseline.

#### Definition of Done

- [x] `TryGetPartial` + partial knobs + cadence dispatch implemented (default interval 0 preserves
      Slice 10/11 FINAL-only behavior)
- [x] Deterministic tests pass — 367/367 total (10 new: 6 detector + 4 engine incl. a
      `BlockingFasterWhisperProcess` fake proving the single-in-flight bound); Release 0 warnings/0
      errors; format clean
- [x] Controlled real-audio benchmark **PASSED (2026-08-05)**: Release `sttnative`, small int8, tl,
      hangover 0.7 s, max segment 8 s, realtime feed, translation OFF,
      `--partial-interval 1 --partial-window 4` on `uc_video_full_16k.wav` (288.79 s) vs `fil-orig` —
      first visible partial **5.59 s after speech onset** (vs first FINAL 15.0 s), **19.5 partials/120 s**,
      active line increments while speaking, FINAL stream **text-identical to Slice 11** (no accuracy
      regression, WER 33.19% in-harness), FINAL ~6 s after segment close, backlog **bounded** (plateau
      ~50 s vs 43 s FINAL-only; one 17.5 s machine-contention spike), realtime-safe 1.18×, nothing
      dropped/reordered
- [x] Decision-gate recorded (2026-08-05): **PASS — Slice 12 closes out; `ggml-base` stays the
      production default; partials default off** (`PartialDecodeInterval = 0`), so production behavior
      is unchanged unless a user opts in via `UC_STT_ENGINE=fasterwhisper-native` +
      `UC_NATIVE_PARTIAL_INTERVAL=1`/`UC_NATIVE_PARTIAL_WINDOW=4`. Documented tradeoffs: ~5 % wall +
      ~8 s tail-latency cost of partial decodes; the rolling-4 s-window means the FINAL reveals the
      earlier words not shown by the last partial (expected Chrome-style rolling-window behavior).
- [x] Docs + TEST_REPORT + BENCHMARK_REPORT updated — Slice 12 sections, CHANGELOG v0.5.21, Entry 13


### Installer & Distribution (post-core; 2026-08-06)

Status: **Complete — Entry 17 CLOSED as PASS (2026-08-06).** Offline, no-repo, no-admin packaging of
the frozen v0.5.25 core. One approved additive production seam (`UC_FW_MODEL`) + packaging/launcher
only. **Caveat (recorded):** installer acceptance passed using the final staged package; the
reproducible `build-package.ps1` path remains an optional follow-up validation because the final
installer was built successfully through the underlying Inno Setup process. Next meaningful test
before distribution is a truly clean Windows machine. Evidence:
`docs/reports/INSTALLER_DISCOVERY.md` (§8 decisions, §9 build + acceptance), CHANGELOG v0.5.26,
`packaging/`.

#### Goal

A clean Windows 10 machine with **no repository** and **no network** must be able to install and run
the production configuration: WASAPI loopback → local STT (`fasterwhisper-native` + live partials) →
optional local en→tl translation → overlay, with no admin at runtime and a clean uninstall.

#### Scope

- `UC_FW_MODEL` env knob (approved seam) in `SpeechEngineFactory.CreateNative` → `FasterWhisperEngineOptions.Model`; unset → `"small"` (unchanged); set → worker `--model <path-or-name>`. Process-scoped only.
- Inno Setup 6.7.3 per-user installer; self-contained .NET 8 win-x64 publish; bundled pruned Python runtime (uv standalone cpython-3.11 + merged import-verified fwv/argosv site-packages); bundled `faster-whisper-small` model; bundled pruned Argos `en→tl` packages; bundled `ggml-base.bin` (fallback engine only).
- `packaging/launcher.cmd` (process-scoped env: `UC_FW_PYTHON`/`UC_ARGOS_PYTHON` → `py\python.exe`, `UC_FW_MODEL`, `UC_STT_MODEL_PATH`, `ARGOS_PACKAGES_DIR`, `HF_HOME`+offline, `PYTHONDONTWRITEBYTECODE=1`), `packaging/UniversalCaptions.iss`, `packaging/build-package.ps1`.

#### Definition of Done

- [x] `UC_FW_MODEL` implemented + tested (`NativeModel_Unset_DefaultsToSmall`, `NativeModel_Override_IsRespected`); full suite **384/384**, Release 0 warnings/0 errors, `dotnet format` clean
- [x] Runtime dependency closure verified by import + real-run (torch/torchgen/sympy/mpmath/networkx required — stanza SBD; torch `include`/`share`/`distributed` extras, `functorch`, pip/setuptools, license trees, `__pycache__` dropped)
- [x] Staged bundle verified: bundled model offline load + real Tagalog transcription; `ARGOS_PACKAGES_DIR` real en→tl; stanza SBD from bundled resources
- [x] Setup.exe **795.5 MB**; installs to `%LocalAppData%\UniversalCaptions` exit 0 (**1,634.5 MB**); flattened layout keeps every path ≤172 chars (MAX_PATH fix for the torch license tree)
- [x] **Installed-bundle acceptance PASS**: worker cmdlines installed-only (no dev venv/repo/hf refs), first caption ≈4.1–4.7 s warm, live partials + committed translated Tagalog (`EN || TL`), settings persist, clean Start/Stop/Exit 0 orphans
- [x] **Clean uninstall**: exit 0, only the app's own `settings.json` remains (`PYTHONDONTWRITEBYTECODE=1` verified), no leftover processes/registry

### Entry 16 — CPU Optimization: Cap Faster-Whisper Decode Threads at 4

Status: **Complete (close-out 2026-08-06)** — change-impact Entry 16. The promoted path sustained
**77.4% of the machine** in the STT worker: every partial and FINAL decode used all 12 cores
(`FasterWhisperEngineOptions.Threads` defaulted to `Environment.ProcessorCount` and the App passed all
12 to the worker's `--threads`). Decode wall is thread-count-invariant for real speech (Entry 16
sweep), so capping threads cuts CPU with no caption regression.

#### Goal

Cut the STT worker's sustained machine share ~3× (≈77% → ≈26%) by defaulting `Threads` to 4, with a
knob for machines that want more decode cores. Isolated performance slice: STT engine selection,
worker wire protocol, segmentation/8 s cap, partial behavior, overlay, and Argos all untouched.

#### Scope

- `SpeechEngineFactory.CreateNative` — `UC_NATIVE_THREADS` env knob → `FasterWhisperEngineOptions
  .Threads` (default **4**, clamped [1, ProcessorCount]; unparseable/out-of-range → 4).
- `LineProtocolFasterWhisperProcess.BuildWorkerArguments()` — worker args extracted (identical
  behavior); `--threads` remains the single decode-thread control.
- `NativeStreamingBenchmark` — `sttnative` gains `--threads` for the gate.
- Tests — `SpeechEngineFactoryTests` (default 4 / override / invalid fallback) +
  `LineProtocolFasterWhisperProcessProtocolTests` (worker args carry `--threads`); internal `Options`
  seam on the native engine + `InternalsVisibleTo("UniversalCaptions.App.Tests")`. Pre-existing flaky
  `CaptionPipelineTests` recovery-test race hardened (`List` → `ConcurrentQueue`).
- Gate — `sttnative` threads=12 vs 4 on `uc_video_full_16k.wav` vs `fil-orig`; real-App CPU probe at
  the new default.
- Docs — Entry 16, TEST_REPORT (Entry 16 close-out), BENCHMARK_REPORT (Entry 16 gate), CHANGELOG
  v0.5.24, PROJECT_STATUS, ROADMAP, BUILD_PLAN, CLAUDE.md.

#### Definition of Done

- [x] `UC_NATIVE_THREADS` knob with production default 4 (clamped); ggml-base / windowed engine
      untouched
- [x] Worker `--threads` wiring verified by test; no behavior change to the protocol
- [x] Formal `sttnative` gate 12t vs 4t: WER **33.2% both**, realtime **1.18× both**, FINAL stream
      **100% text-identical**, latency/backlog comparable
- [x] Real-App CPU probe at default: STT worker system mean **77.4% → 31.6%** (max 88.2% → 37.6%);
      captions still flow (first caption 3.72 s, live partials visible, overlay producing)
- [x] Full suite passes — **382/382** (App 95); Release build 0 warnings/0 errors; `dotnet format`
      clean
- [x] **Final real-world acceptance (2026-08-06) — PASS:** continuous VLC media through the default
      device at the production default. Leg 1 Tagalog/translation-OFF (`uc_video_full.m4a`, 288.79 s):
      STT worker 31.8% system mean (max 37.6%), App 0.9%, first caption 3.27 s, 95 snapshots, max 33
      lines, clean exit, 0 orphans. Leg 2 English/en→tl (`english_sustained_90s.wav` looped 300 s): STT
      33.5% (max 37.1%) + Argos 4.2% (max 21.6%), App 1.3%, first caption 3.23 s, 129 snapshots, max 54
      lines, clean exit, 0 orphans. Overlay verified live (growing partials, FINAL freeze into bounded
      history with `EN || TL` badge, real Tagalog, Stop retains history). Evidence: TEST_REPORT (final
      real-world acceptance), CHANGELOG v0.5.25, `acceptance_summary.csv` + `acceptance_*` (untracked).
- [x] No commit unless explicitly requested

### Entry 15 — Overlay Live-Line Integration (ADR-0008 follow-up)

Status: **Complete (close-out 2026-08-06)** — change-impact Entry 15. The promoted default
(`fasterwhisper-native` + live partials, Entry 14) was previously invisible in the overlay: commit
`7d1c057` ("temporary diagnostic tracer", 2026-08-03) had replaced Slice 7's active-line painting and
`_activeBlock` was never assigned (tests even asserted `ActiveBlock() == null`).

#### Goal

Make the live partial stream actually visible: one active overlay line that appears while the speaker is
talking, is rewritten in place on later partials, freezes into history on the FINAL, and never churns or
leaves a stale partial after Stop. Restore the Slice 7 stable-incremental-rendering guarantees. Keep the
`shouldUpdate` gate (no source-language flash during translation-pending). Preserve the `ggml-base`
fallback.

#### Scope

- `CaptionOverlayWindow.UpdateCaptionItems` — create one mutable `_activeBlock`; rewrite its text in place
  on later partials; remove it when `model.ActiveLine` is null (committed/stopped/hidden-while-
  translating). `ReconcileHistory` reuse-by-sequence and the `shouldUpdate` gate unchanged. XAML already
  described the restored design (no XAML change).
- Tests — `CaptionRenderIdentityTests` rewritten 4→6 (partial rewrites same block identity; growing
  stream paints one block with no history churn; no partial ever enters committed history; FINAL freezes
  active into history; cleared active line removes block keeps history; finalized blocks keep text
  instances and order).
- Smoke — `smoke.ps1` gains `-SampleMs`, per-sample `app%|wkr%|n` (App CPU / faster-whisper worker CPU /
  overlay text-element count) and `POSTSTOP_1..3` probes. Runs: `promoted` (tl), `liveoverlay`,
  `stopmid`, `transen` (en→tl Argos), `trans` (tl→en, documented-unsupported → graceful degradation).
- Docs — Entry 15, TEST_REPORT (Entry 15 section), CHANGELOG v0.5.23, PROJECT_STATUS, ROADMAP,
  BUILD_PLAN, CLAUDE.md, ADR-0007 display-clause supersession note.

#### Definition of Done

- [x] Overlay paints the live active line from the native partial stream (verified in real App)
- [x] Later partials rewrite the same block; FINAL freezes it into history; history never churns;
      no partial ever enters committed history; Stop/Dispose leaves no stale partial
- [x] First-visible-partial latency and CPU impact measured (≈5.6 s after capture start; App CPU
      ~0–66%, worker ~0%)
- [x] Full suite passes — **374/374** (App 89); Release build 0 warnings/0 errors; `dotnet format`
      clean
- [x] en→tl Argos live-translated active line verified (no raw-source flash); tl→en confirmed
      documented-unsupported (stanza SBD) with graceful degradation
- [x] `ggml-base` fallback intact; worker protocol, 8 s `MaxSegmentDuration`, windowed engine,
      ADR-0007, TD-002, TD-005 untouched; no commit unless explicitly requested

### Entry 14 — Production Default Promotion (faster-whisper native + live partials)

Status: **Complete (close-out 2026-08-05)** — change-impact Entry 14, ADR-0008. Product decision
(user-approved): the validated Slice 10–12 native engine with live partials becomes the production
STT default; ggml-base is preserved as the explicit fallback.

#### Goal

Make the Chrome-Live-Caption-style native streaming path the out-of-box experience: live partials
(interval 1 s, window 4 s) + one coherent FINAL per completed segment, 8 s `MaxSegmentDuration` cap
(frozen), materially better Tagalog recognition than ggml-base. Keep the faster-whisper worker wire
protocol, the windowed engine, ADR-0007, TD-002, and TD-005 untouched. No new tuning or feature work.

#### Scope

- `UniversalCaptions.App` — extract engine selection into a testable `SpeechEngineFactory`
  (new `src/UniversalCaptions.App/SpeechEngineFactory.cs`): default / `fasterwhisper-native` → native
  + partials (default ON), `ggml-base` → original local-Whisper engine (explicit fallback),
  `fasterwhisper` → windowed engine. `App.xaml.cs` factory delegates to it; resolve helpers
  (model path `UC_STT_MODEL_PATH` → `artifacts/models/ggml-base.bin`; python `UC_FW_PYTHON` →
  `%TEMP%\fwv`) moved into the factory. No automatic runtime fallback (deliberate — ADR-0003
  no-silent-switch).
- Tests — `SpeechEngineFactoryTests` (5 new, side-effect-free constructors, no Python/model needed).
- Docs — ADR-0008 (+ ADR-0003 supersession note, ADR README), Entry 14, CHANGELOG v0.5.22,
  PROJECT_STATUS, ROADMAP, BUILD_PLAN, CLAUDE.md, BENCHMARK/TEST report notes.

#### Definition of Done

- [x] `SpeechEngineFactory` is the single selection point; default = native + partials; ggml-base and
      `fasterwhisper` preserved as explicit selections
- [x] Full suite passes — **372/372** (5 new App tests; App 87, Speech 109, Captions 72, Audio 77,
      Translation 27); Release build 0 warnings/0 errors; `dotnet format --verify-no-changes` clean
- [x] Decision recorded — ADR-0008 (supersedes ADR-0003 default-model clause); Entry 14 close-out;
      CHANGELOG v0.5.22; PROJECT_STATUS/ROADMAP/CLAUDE.md updated
- [x] Faster-whisper worker protocol, windowed engine, ADR-0007, TD-002, TD-005 untouched; no
      automatic runtime fallback; no commit unless explicitly requested


### Slice 2 — STT Spike

Status: **Complete** — 107/107 tests passing, streaming finals committed on all benchmark samples, model benchmark recorded and default user-approved (ggml-base, tiny as fallback), code review + docs close-out done.

#### Goal

Connect PCM → streaming STT abstraction → partial transcripts, first with a fake engine, then with local Whisper.

#### Scope

- `ISpeechToTextEngine` (streaming), `PartialTranscript`, `FinalTranscript` in `UniversalCaptions.Core.Speech` (refined — see ADR-0003)
- Fake engine for deterministic tests; Whisper engine integration (model download, chunking, partial hypotheses)
- Latency instrumentation from capture to transcript

#### Dependencies

- Slice 1 complete

#### Tasks

1. Define `ISpeechToTextEngine`, `PartialTranscript`, `FinalTranscript`, `SpeechRecognitionError` in Core
2. Build `FakeSpeechToTextEngine` + contract tests (deterministic, no hardware/models)
3. Implement `WhisperSpeechToTextEngine` (sliding-window streaming over whisper.cpp via Whisper.net) + `StreamingTranscriptCommitter`
4. Verify real model end-to-end (load model, stream `jfk.wav` at realtime pacing, clean stop)
5. Build `UniversalCaptions.Benchmarks` harness; benchmark ggml-tiny/base/small; record results
6. Verify build + tests; record evidence in `TEST_REPORT.md` / `BENCHMARK_REPORT.md`; update docs

#### Definition of Done

- [x] `ISpeechToTextEngine` streaming contract verified with a fake engine
- [x] Local Whisper produces partial transcripts from captured audio
- [x] Model selection benchmark recorded; default user-approved (ggml-base; tiny as low-resource fallback)
- [x] Streaming finals resolution (stability-based commit tuning; finals committed on every benchmark sample)
- [x] Code review (self + fresh-context) + docs close-out

### Slice 3 — Translation Spike

Status: **Complete (2026-08-01)** — contract + fake + engine + tests (21) + real Argos verification + benchmark + fresh-context review + final gates all done.

#### Goal

Connect source transcript → translation abstraction → translated transcript, first with a fake engine, then with Argos Translate running as a local process.

#### Scope

- `ITranslationEngine`, `TranslationResult`, `TranslationException`/`TranslationErrorKind` in `UniversalCaptions.Core.Translation` (refined — see ADR-0006)
- Source + target selection; pivoting through an intermediate language when no direct pair is installed
- Fake engine for deterministic tests; `ArgosTranslationEngine` launching a local Argos process over a line protocol (no Python embedded in .NET)
- Translation latency/quality benchmark recorded (model/pair selection resolved by this benchmark)

#### Dependencies

- Slice 2 complete

#### Definition of Done

- [x] `ITranslationEngine` contract verified with a fake engine
- [x] Argos translates source transcripts to the target language offline/local
- [x] Translation benchmark recorded (latency + quality per pair)
- [x] Code review (self + fresh-context) + docs close-out

#### Slice 3 Evidence (2026-08-01)

- Contract verified with `FakeTranslationEngine` (8 tests: success, auto-detect, pivot metadata, ordering, cancellation, failure, empty/source-equals-target validation).
- `ArgosTranslationEngine` verified with `FakeArgosProcess` (13 tests: request mapping, error mapping, validation, cancellation, sequencing, serialized concurrency, restart-after-fatal-error, lifecycle).
- Real Argos 1.11.0 (Python 3.11 venv) verified end-to-end: direct pairs `en→tl`, `ja→en`, `en→ja`; pivoting `ja→tl` via `en` (`usedPivot=true`, `pivotLanguage=en`); error mapping for unknown language codes, empty input, source-equals-target.
- Benchmark recorded in `docs/reports/BENCHMARK_REPORT.md` (Slice 3 section): see per-pair latency/throughput/memory/quality.
- Fresh-context review (2026-08-01) found and fixed: stale-process recovery after kill (restart test added), unwrapped `JsonException`/`IOException` now mapped to `TranslationException`, Python server crash path on malformed requests (target/id validation, `get_translation` guarded), options timeout validation, `ProcessStartInfo.ArgumentList` (no string-built args), UTF-8 pinned via `PYTHONIOENCODING`/`PYTHONUTF8`. Remaining items logged as TD-013–TD-015.
- Final gates green: `dotnet build` 0 warnings, `dotnet test` 128/128, `dotnet format --verify-no-changes`, `dotnet list package --vulnerable` no vulnerable packages.
- Known limitation: Argos sentence-boundary detection does not support `tl` as a source language; MVP pairs use `tl` only as a target (ADR-0006).

### Slice 4 — Caption Service

**Status: Complete (2026-08-01)** — 40 Captions tests + 168/168 total passing, build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, no vulnerable packages, fresh-context review findings fixed, close-out approved (2026-08-01).

#### Goal

Implement partial/final caption state, source-vs-translated caption selection, ordering, duplicate prevention, bounded history, session lifecycle, and cancellation — consumed by the future overlay. No WPF anywhere; the overlay (Slice 5) renders `CaptionState`.

#### Scope

- Contracts in `UniversalCaptions.Core.Captions` (per ADR-0003/0006 precedent — `UniversalCaptions.Captions` depends only on Core): `ICaptionService` (transcript/translation events → caption state, `ProcessPartial`/`ProcessFinal`, `Start`/`Stop`/`Reset`, `SetTranslationEnabled`, `FlushAsync`, state-change events), `CaptionLine` (immutable, Active/Final + translation status), `CaptionState` (active line, bounded sequence-ordered history, translation-enabled state, session lifecycle), `CaptionServiceOptions`
- `src/UniversalCaptions.Captions`: `CaptionService` — partials replace the active line; finals commit to history and are translated in the background when enabled; translation failure preserves the source caption; stale translation results matched by line identity are dropped; in-flight translations cancelled on stop/reset/dispose; gate-serialized state with events raised outside the lock and snapshot `History`
- Unit tests with deterministic fake translation engines: partial → active → final → committed transitions, translation on/off, ordering, duplicate re-delivery, bounded history, translation failure preserves source, cancellation, session reset
- Fresh-context review of the AI-generated Slice 4 code (completed; findings fixed)
- DoD gates: build 0 warnings, tests pass, `dotnet format --verify-no-changes` clean, no vulnerable packages; docs updated (BUILD_PLAN, CHANGELOG, PROJECT_STATUS, TEST_REPORT, ROADMAP, ARCHITECTURE)

#### Definition of Done

- [x] `ICaptionService`, `CaptionLine`, `CaptionState`, `CaptionServiceOptions` in `UniversalCaptions.Core.Captions`
- [x] `CaptionService` in `UniversalCaptions.Captions` (Core-only dependency, no WPF)
- [x] Partial/final handling, ordering, duplicate prevention, bounded history, session reset, cancellation
- [x] Translation integration on/off + translation-failure preservation (source caption intact)
- [x] Deterministic tests (40 Captions tests; total 168/168)
- [x] Build 0 warnings/0 errors; `dotnet format --verify-no-changes` clean; no vulnerable packages
- [x] Fresh-context review completed and findings fixed
- [x] Slice 4 close-out approved (2026-08-01); docs finalized (CHANGELOG, PROJECT_STATUS, TEST_REPORT, ROADMAP, BUILD_PLAN)

#### Slice 4 Evidence (2026-08-01)

- Contracts in `UniversalCaptions.Core.Captions` (per ADR-0003/0006 precedent, so `UniversalCaptions.Captions` depends only on Core): `ICaptionService`, `CaptionLine`, `CaptionState`, `CaptionServiceOptions`.
- `CaptionService` verified with deterministic `StubTranslationEngine`/`GatedTranslationEngine` fakes (24 tests) + `CaptionState` (16 tests) = 40 Captions tests; total **168/168** (66 Audio + 41 Speech + 21 Translation + 40 Captions).
- Lifecycle verified: partial → active → final → committed; translation on/off + explicit target override; translation failure preserves the source caption; stale translation results matched by line identity cannot overwrite newer state; cancellation on stop/reset/dispose; bounded sequence-ordered history; events raised outside the state lock with snapshot `History`.
- Fresh-context review (2026-08-01) found and fixed: snapshot history reads, deferred CTS disposal, atomic translation-start token, stale-translation identity guard, event-raising moved out of the translation catch, target normalization.
- Final gates green: `dotnet build` 0 warnings/0 errors, `dotnet test` 168/168, `dotnet format --verify-no-changes` clean, `dotnet list package --vulnerable` no vulnerable packages.
- Close-out approved by product (2026-08-01); Slice 4 recorded as Completed. Next: Slice 5 — overlay + control window.

### Slice 5 — Overlay + Control Window

**Status: Complete (close-out 2026-08-01)** — implementation + unit tests complete; **manual overlay/device verification completed 2026-08-01**; **real-Argos wiring verified end-to-end through the App 2026-08-01**.

#### Goal

Always-on-top caption overlay and minimal control window.

#### Scope

- WPF overlay window (borderless, transparent, draggable, resizable, opacity, font size, multi-monitor, click-through)
- Control window (audio source, language, engine, translation on/off + source/target languages, start/stop, status, latency, settings)
- `IOverlayService` implementation

#### Tasks

1. Create `UniversalCaptions.App` (WPF, `net8.0-windows`, `UseWPF`, PerMonitorV2 manifest, `Microsoft.Extensions.DependencyInjection` 8.0.0) and add it (+ `UniversalCaptions.App.Tests`) to `UniversalCaptions.slnx`
2. Define `IOverlayService` (visibility, position, opacity [0.2, 1.0], font size [10, 96], click-through, `Show`/`Hide`/`ShowAt`) + `CaptionDisplayModel`/`CaptionDisplayPolicy` (Q1 resolution: active line = verbatim latest partial; finals = bounded history newest-first; translated text replaces source only when `CaptionTranslationStatus.Completed`)
3. Implement `CaptionOverlayWindow` (borderless/transparent/always-on-top, history `ItemsControl` + active line, drag + resize grip, click-through via `WS_EX_TRANSPARENT` P/Invoke, dispatcher-coalesced rendering of `ICaptionService` events)
4. Implement `CaptionPipeline` (wiring capture → processor → STT → `CaptionService` via `Func` factories; idempotent `Start`/`Stop`/`Dispose`; `StatusChanged`/`LatencyUpdated` events; error handling stops a running session but defers teardown during startup) + `PipelineStatus`
5. Implement `ControlWindow` (audio source/language, translation on/off + target, status/latency, overlay sliders, click-through toggle, Start/Stop)
6. Compose everything in `App.xaml.cs` (DI composition root; `ShutdownMode.OnMainWindowClose`)
7. Write `UniversalCaptions.App.Tests` — `CaptionDisplayPolicyTests` (8) + `CaptionPipelineTests` (20) + `AudioSourceLoaderTests` (4) + `TranslationGuardTests` (4), with fakes at the capture/STT boundaries
8. Verify gates (build 0 warnings, 209/209 tests, format clean, no vulnerable packages); record results in `TEST_REPORT.md`
9. Manual verification (completed 2026-08-01): ran `UniversalCaptions.App`, verified overlay visuals/always-on-top/click-through/resize on real system audio (real Whisper `ggml-base` → live overlay captions, lifecycle, error paths) and recorded evidence in `TEST_REPORT.md`; **real-Argos wiring verified end-to-end through the App 2026-08-01** (committed overlay lines translated to Tagalog by the App-spawned Argos child process)
10. Close-out (completed 2026-08-01): fresh-context review + docs (CHANGELOG, PROJECT_STATUS, TEST_REPORT, ROADMAP, BUILD_PLAN) + close-out record in `CHANGE_IMPACT_ANALYSIS.md` Entry 6

#### Definition of Done

- [x] `IOverlayService` + overlay/control windows + DI composition root in `UniversalCaptions.App`
- [x] Overlay renders `CaptionState` (active + history) with the resolved Q1 display policy
- [x] Pipeline wiring + status/latency surfaced in the control window; UI marshals events to the dispatcher
- [x] `UniversalCaptions.App.Tests` (36 tests) with fakes at the capture/STT boundaries; total **209/209**
- [x] Build 0 warnings/0 errors; `dotnet format --verify-no-changes` clean; no vulnerable packages
- [x] Manual verification of the overlay + control window on real system audio (recorded in TEST_REPORT, 2026-08-01)
- [x] Real-Argos wiring verified end-to-end through the App (committed overlay lines translated to Tagalog, 2026-08-01)
- [x] Fresh-context review completed
- [x] Close-out docs + Entry 6 close-out record completed

#### Slice 5 Evidence (2026-08-01)

- `UniversalCaptions.App` (net8.0-windows, UseWPF) is the DI composition root: `ArgosTranslationEngine` → `CaptionService` ("en", target "en", history 50) → `AudioProcessor` (16 kHz mono) → capture/STT factories (`WasapiLoopbackCaptureSource` default or by device; `WhisperSpeechToTextEngine` with `UC_STT_MODEL_PATH` env override, default `artifacts/models/ggml-base.bin`) → `CaptionPipeline` → `CaptionOverlayWindow` + `ControlWindow`.
- `CaptionDisplayPolicyTests` (8) verify the resolved Q1 policy: active line = latest partial; committed finals newest-first; translated text replaces source only when `Completed`; source preserved when off/pending/failed.
- `CaptionPipelineTests` (20) verify wiring against `FakeAudioCapture`/`FakeSpeechToTextEngine`/passthrough processor: audio → processor → STT flow, format conversion, partial/final flow into `CaptionService`, latency, capture/recognition/capture-factory errors, teardown ordering (Stop returns before teardown completes; Dispose waits), fail-on-start teardown paths, stop/dispose, and chunks-after-stop ignored.
- `AudioSourceLoaderTests` (4) + `TranslationGuardTests` (4) verify device enumeration (preferred default, failure-surfacing) and source-equals-target rejection.
- `CaptionSnapshotTests` (5) verify the immutable active-line/history snapshot (`CaptionService.GetSnapshot`), thread-safe against concurrent mutations.
- Final gates green: `dotnet build UniversalCaptions.slnx` 0 warnings/0 errors, `dotnet test UniversalCaptions.slnx --no-build` **209/209**, `dotnet format --verify-no-changes` clean, `dotnet list package --vulnerable` no vulnerable packages (all 13 projects).
- Manual verification **completed 2026-08-01** (recorded in `TEST_REPORT.md`, Slice 5): real system audio → Whisper `ggml-base` → live overlay captions; always-on-top/transparency; drag/resize/click-through; stop/restart; rapid Stop→close (clean ~2 s exit); model-not-found error path; source-equals-target rejection live.
- **Real-Argos wiring verified end-to-end through the App 2026-08-01** (recorded in `TEST_REPORT.md`, Slice 5): recreated the dev Argos venv (`argostranslate==1.11.0` + en→tl/tl→en/ja→en/en→ja under a short 8.3 temp path per TD-011), prepended its `Scripts` dir to PATH, toggled translation ON, selected **Tagalog (tl)**, started captions, played speech via SAPI, and confirmed **committed overlay lines displayed real translated Tagalog** (`tamad aso.`, `Ang mabilis na kayumangging sorra ay tumatalon sa ibabaw ng eruplano`, `IsTranslated = True`) served by the App-spawned Argos child process (`python` venv shim → UV base python running `argos_translate_server.py`). This also exercised the `ControlWindow.ApplyTranslationSettings` guard fix (toggle stays ON + target combo enabled on a guard error). Engine-level real-Argos verification (direct + pivot pairs, offline) is in `BENCHMARK_REPORT.md` (Slice 3).
- Slice 5 close-out **completed 2026-08-01**: fresh-context review done (findings fixed in v0.5.1), docs updated, Entry 6 close-out record finalized.

### Slice 6 — End-to-End

#### Status

**Complete (close-out 2026-08-01)** — change-impact Entry 8. Phased: Phase 1a E2E latency metric + tests → Phase 1b OFAT baseline sweep (window/decode-interval/StabilityWindow) → Phase 1c App-level SAPI E2E runs → shortlist → Phase 2 real-app validation (YouTube/Chrome, VLC, Zoom). **Phase 1a complete (2026-08-01): E2E latency metric + tests (238/238). Phase 1b complete (2026-08-01): OFAT sweep run; shortlist = base 8 s/1 s/st2, tiny 8 s/1 s/st2, base 8 s/1 s/st3 control (see `docs/reports/BENCHMARK_REPORT.md`). Phase 1c complete (2026-08-01): App-level SAPI E2E validation at baseline + shortlist × 3 runs each through the real App (loopback → Whisper → Argos en→tl → overlay) — every run published real translated Tagalog; latency winner tiny/8/1/st2 (E2E final median 16.25 s incl. Argos cold start; warm last-final 7.45 s; STT 3.61 s), accuracy-preserving base/8/1/st2 (faster commits, same accuracy), control base/8/1/st3 (evidence in `docs/reports/TEST_REPORT.md`). The validated baseline base/8/1/st2 was promoted to the App default: `StabilityWindow` 3→2 (WhisperEngineOptions + App + benchmark, one authoritative config); model default `ggml-base` unchanged. Fresh-context review of the Phase 1a E2E metric code completed clean. Phase 2 real-app validation is deferred per user — a future reassessment pass over the baseline defaults, not a prerequisite for Slice 6 completion.**

#### Goal

Verify the full pipeline on real audio and measure latency/accuracy. Baseline the latency knobs (window size, decode interval, `StabilityWindow`) before tuning. Latency is the primary metric; accuracy/stability are hard constraints.

#### Scope

- **Phase 1a — E2E latency metric.** Add `EndToEndLatencyUpdated` (capture→STT→translation→translated caption available to the UI) as a separate metric from `LatencyUpdated` (unchanged: STT-final latency). Carry the originating audio timestamp through `CaptionLine` (`CapturedAtUtc` already present; add translation start/completion timestamps). Distinguish **E2E partial** (audio → translated active line) and **E2E final** (audio → translated committed line). Testable with a fake clock + deterministic fakes. Full gates.
- **Phase 1b — OFAT baseline sweep.** Parameterize the STT benchmark over `WindowDuration`, `DecodeInterval`, `StabilityWindow`; 3 values per knob centered on defaults (window {6,8,10}s, interval {0.5,1,2}s, stability {2,3,5}); models {base, tiny}; samples {jfk, OSR}. Metrics per run: first-partial latency, stable/final latency, streamed-finals WER, decode factor, CPU/RAM. Emit a table + CSV. Then shortlist 2–3 configs.
- **Phase 1c — App-level E2E runs.** Repeatable SAPI scripted corpus through the real App (loopback → Whisper → Argos en→tl → overlay): record E2E partial/final latency + translation latency at baseline + shortlisted configs, plus translation correctness (char-similarity).
- **Phase 2 — Real-application validation (manual).** YouTube/Chrome, VLC, Zoom (continuous + conversational turn-taking). Validate the selected config survives real-world audio; do not tune per app.
- Latency measurement; Whisper model + Argos pair benchmark; record findings and propose default model/pair to the user (Must-Ask).
