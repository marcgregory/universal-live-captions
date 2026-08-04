# Universal Live Captions Test Report

Last updated: 2026-08-04

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Record test execution evidence for the current slice |
| Scope | Slice 1 — Audio Capture Spike, Slice 2 — STT Spike, Slice 3 — Translation Spike, Slice 4 — Caption Service (complete), and Slice 5 — Overlay + Control Window (complete) (automated unit tests + manual real-model verification) |
| Audience | Engineering |
| Owner | Engineering |
| Status | Active |
| Related Documents | [BUILD_PLAN.md](../implementation/BUILD_PLAN.md), [QUALITY_ASSURANCE.md](../QUALITY_ASSURANCE.md), [RELEASE_PLAN.md](../implementation/RELEASE_PLAN.md), [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md) |

---

## Summary

Slices 1–5 automated tests pass: **253/253 passed, 0 failed, 0 skipped** (66 Audio + 71 Captions + 45 Speech + 21 Translation + 50 App). Solution builds with **0 warnings, 0 errors** (warnings-as-errors). A post-close-out refinement (change-impact Entry 7) adds **live active-line translation** (single in-flight slot, instance-identity stale-guard, disabled-mid-flight results discarded) and a **Chrome-style overlay redesign** (auto-sized translucent pill, white text, target-language badge, expand/collapse chevron, hide button) with a ControlWindow "Show Captions" button; its automated tests are complete (**238/238** for Slice 6 Phase 1a; Entry 7 itself was 224/224) and its **manual verification with real audio + real Argos is complete (2026-08-01)** — the in-progress overlay line reads Tagalog before it commits (see Slice 5 refinement note). **Overlay caption display fixed (2026-08-01):** `CaptionDisplayPolicy` renders the committed history chronologically (oldest at top, newest at the bottom), and the overlay's hard height caps (`HistoryList MaxHeight` + window `MaxHeight`) were removed so the auto-sized pill grows to fit every rendered line — the newest committed caption and the highlighted/current caption are never clipped or covered (the active line occupies its own layout row, separate from the history). Deterministic display-policy tests cover first-caption, chronological ordering, newest-at-bottom, capacity eviction (oldest removed from the top), and partial→final append with no duplication; build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean. **Slice 6 (Phases 1a–1c) is complete (close-out 2026-08-01)** — E2E latency metric + tests (238/238), OFAT sweep + shortlist in `BENCHMARK_REPORT.md`, and the App-level SAPI E2E validation recorded below (baseline + shortlist configs × 3 runs each through the real App: WASAPI loopback → Whisper → Argos en→tl → overlay, E2E latency row polled via UIA, every run publishing real translated Tagalog). The validated baseline `base/8/1/st2` is the App default (`StabilityWindow` 3→2, model `ggml-base` unchanged). Phase 2 real-app validation remains deferred per user. Slice 1 manual verification against real system audio succeeded. Slice 2 real-model verification succeeded: `WhisperSpeechToTextEngine` streamed **partial and final transcripts** from four samples through the real ggml-tiny/base models at realtime pacing with a clean stop/dispose (see [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md)). Slice 3 real-Argos verification succeeded: `ArgosTranslationEngine` translated **offline/local** through a real Argos 1.11.0 child process for direct pairs (`en→tl`, `ja→en`, `en→ja`) and a pivot pair (`ja→tl` via `en`), with correct error mapping (see below and [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md)). Slice 4 (complete): `CaptionService`/`CaptionState` verified with deterministic fake translation engines — partial→active→final→committed transitions, translation on/off, translation failure preserving the source caption, ordering, bounded history, and cancellation. Slice 5 (complete): `UniversalCaptions.App` overlay display policy + pipeline wiring verified with deterministic fakes (`CaptionDisplayPolicyTests` 8 + `CaptionPipelineTests` 20 + `AudioSourceLoaderTests` 4 + `TranslationGuardTests` 4) — Q1 display policy resolution (active line = verbatim latest partial; finals = bounded history newest-first; translated text replaces source only when `Completed`), capture→processor→STT→caption-service wiring, error handling, lifecycle, audio-source enumeration (preferred default, failure-surfacing), and translation guard (source-equals-target rejection). **Manual overlay/device verification completed 2026-08-01** (all items Passed — see Slice 5 section below), including the **real-Argos wiring end-to-end through the App** (committed overlay lines translated to Tagalog by a real local Argos child process).

## ADR-0007 Option B — boundary-preserving fallback (2026-08-04)

### Automated tests — PASS

Full suite **284/284 passing, 0 failed, 0 skipped**, Release build 0 warnings / 0 errors.

```
Passed!  - Failed: 0, Passed: 66, Total: 66  - UniversalCaptions.Audio.Tests.dll
Passed!  - Failed: 0, Passed: 72, Total: 72  - UniversalCaptions.Captions.Tests.dll
Passed!  - Failed: 0, Passed: 59, Total: 59  - UniversalCaptions.Speech.Tests.dll
Passed!  - Failed: 0, Passed: 27, Total: 27  - UniversalCaptions.Translation.Tests.dll
Passed!  - Failed: 0, Passed: 60, Total: 60  - UniversalCaptions.App.Tests.dll
```

Changes under test: `StreamingTranscriptCommitter` Option B rules 1/3/4 (`LastCompletedBoundaryLength`, `PendingStable`, replacement-drop in `UpdatePendingStable`) — rewritten budget-fallback tests (rule 3 commits last completed boundary + keeps tail; rule 4 never manufactures a word-backed FINAL), `CommittedUntilUtc` snap-to-boundary tests, epoch-rollover timer survival, and `WhisperSpeechToTextEngine` multi-segment `ScriptedSegmentDecoder` migration.

### Live JFK verification — controlled English verification, PASS

**Hardware/real-path:** real App Release build, `ggml-base`, `StabilityWindow=2`, steady 8 s / 0.5 s config, real WASAPI loopback capture of the default render device, `artifacts/samples/jfk_long.wav` played through the loopback device, overlay committed-FINAL lines observed via UI Automation.

**Run A — single 22 s playback:** committed FINALs in order:
1. `Listening.` (pre-existing Whisper silence hallucination — also present pre-fix)
2. `Ask what you can do for your country.`
3. `And so my fellow Americans ask not what your country can do for you, ask what you can do for your country.`

**Run B — continuous ~2 min loop:** committed FINALs in order:
1. `Listening.`
2. `you ask what you can do for your country.`
3. `And so my fellow Americans ask not what your country can do for you, ask what you can do for your country.`
4. `And so my fellow Americans ask not what your country can do for you,`
5. `ask what you can do for your country. And so my fellow Americans ... ask what you can do for your country.` (cross-loop sliding-window re-emission — TD-006/007, pre-existing, isolated; not the Option B fallback defect)

**Pass criteria — all met:**
- ❌ Pre-fix interior-fragment FINAL `country can do for` → **ABSENT in both runs** (pre-fix run committed it with `boundary_found: false, fallback_used: true`; evidence `artifacts/samples/adv7_trace_evidence.log`, gitignored).
- ✅ Complete boundary-backed JFK sentences present.
- ✅ Stop drain preserves the final committed captions (POST-STOP == committed set).
- ✅ No app crash/hang.
- Notes: `Listening.` is a pre-existing Whisper artifact (present in the pre-fix baseline too); Run B line 5 is the known TD-006/007 overlap re-emission, out of scope for this step (duplicate handling is a separate follow-up per ADR-0007).

**Evidence:** `artifacts/samples/adv7_optionB_jfk.log`; driver script (UI Automation) preserved at the temp harness used for both runs.

**Acceptance gate — PENDING:** the original Tagalog recording scenario (`"At gusto ko"` / `"Kaya"` / `"artipisyal na katalinuhan"`) is the remaining acceptance evidence. The original operator recording is **not available** in the workspace; per user, no substitute Tagalog sample may be used to claim acceptance. ADR-0007 therefore remains **Proposed** until that live evidence exists (fragmentation, duplicates, missing words, Stop drain judged end-to-end through the real App).

## Environment

| Item | Value |
|---|---|
| OS | Windows 10 Pro (build 19045, NT 10.0.19045) |
| Runtime | .NET 8.0.29 |
| Solution | `UniversalCaptions.slnx` |
| Test framework | xUnit (net8.0) |
| Capture dependency | NAudio 2.2.1 (WASAPI loopback, no VB-CABLE) |
| Translation engine | Argos Translate 1.11.0 (Python 3.11 venv under `artifacts/argos/`, git-ignored) |

## Build Verification

Command: `dotnet build UniversalCaptions.slnx`

```
    0 Warning(s)
    0 Error(s)
```

## Automated Test Results

Command: `dotnet test UniversalCaptions.slnx`

```
Passed!  - Failed:     0, Passed:    66, Skipped:     0, Total:    66, Duration: 371 ms - UniversalCaptions.Audio.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    58, Skipped:     0, Total:    58, Duration: 215 ms - UniversalCaptions.Captions.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    41, Skipped:     0, Total:    41, Duration: 1 s - UniversalCaptions.Speech.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    21, Skipped:     0, Total:    21, Duration: 89 ms - UniversalCaptions.Translation.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 630 ms - UniversalCaptions.App.Tests.dll (net8.0)
```

### Coverage by Area

| Area | Tests | Result |
|---|---|---|
| `ByteToFloatConverter` — 8/16/24/32-bit int and 32-bit float PCM, extensible sub-formats, stereo interleave, unsupported encodings, range validation | 10 | Passed |
| `SampleRateConverter` — frequency preservation (16k→48k upsample, 48k→16k downsample, 44.1k→48k), channel count, empty/short input, identity 48k→48k | 12 | Passed |
| `EnergyVad` — silence vs speech detection, thresholds, boundary behavior | 6 | Passed |
| `PcmRingBuffer` — ordering, wrap-around, overflow, read/write boundaries | 10 | Passed |
| `AudioProcessor` — pipeline chaining, resample + convert + VAD integration | 8 | Passed |
| `AudioLevelMeter` — RMS/peak per chunk, window aggregation | 8 | Passed |
| `WasapiLoopbackCaptureSource` — fake `IWaveIn` boundary: start/stop, chunk delivery, device-invalidated (0x88890004 → Disconnected), 0x8889000A → Unavailable, unknown → Unknown | 12 | Passed |
| `ISpeechToTextEngine` + `FakeSpeechToTextEngine` — contract: process-before-start ignored, start/stop, idempotent stop, partials by duration, partial→final ordering, monotonic sequence, stop-cancels, runtime error, start-throw, start-model-error, continuous chunks, timestamp/latency, dispose | 14 | Passed |
| `FakeSpeechToTextEngine` — scripted direct emit, offline trigger, partial-then-error | 4 | Passed |
| `StreamingTranscriptCommitter` — stability-based commit: partial vs stable vs final, word-boundary common-prefix back-off, epoch reset, committed boundary, no premature commit, reset | 10 | Passed |
| `WhisperSpeechToTextEngine` (injected deterministic decoder) — partial→final as stable text is confirmed, changing partials don't commit early, final not emitted twice, stop doesn't commit incomplete audio, restart resets state, decoder failure doesn't leave stale text, decode→Stop→DisposeAsync regression, process-before-start ignored, stop cancels decode, invalid-format → `InvalidAudioFormat`, decode failure → `EngineFailed`, missing model → `ModelNotFound`, `StabilityWindow` < 2 rejected | 13 | Passed |
| `ITranslationEngine` + `FakeTranslationEngine` — contract: mapped text + languages, auto-detect source, pivot metadata, monotonic sequence + call ordering, cancellation, configured failure → `TranslationException`, empty input → `EmptyInput`, source-equals-target → `SourceEqualsTarget` | 8 | Passed |
| `ArgosTranslationEngine` (injected `FakeArgosProcess`) — request mapping (text/source/target), detected-source + pivot metadata, process failure → mapped `TranslationException`, null text → `ArgumentNullException`, empty text → `EmptyInput`, missing target → `UnsupportedLanguage`, source-equals-target → `SourceEqualsTarget` (process not started), start failure → mapped `TranslationException`, cancellation, monotonic sequence, concurrent calls serialized, restart-after-fatal-error (process relaunches on the next call), dispose disposes process | 13 | Passed |
| `CaptionState` — sequence-ordered history, duplicate-sequence replace, bounded history (drop oldest, capacity 0), active-line replace/clear + state validation, translation update by exact line identity (stale instance rejected), missing sequence no-op, active-line translation replace by exact line identity (apply, stale instance rejected, after-clear no-op, state validation), translation on/off + normalization, session begin/end, reset, negative-capacity rejected | 20 | Passed |
| `CaptionSnapshot` — immutable snapshot of active line + history (detached from later commits, thread-safe against concurrent mutations), `GetSnapshot` matches current state | 5 | Passed |
| `CaptionService` (deterministic `StubTranslationEngine`/`GatedTranslationEngine`) — partial updates active line + events, partial/final before-start ignored, final commits history + clears active, committed event, after-stop ignored, idempotent start, translation on → background request + completed line, explicit target override, translation off → no request, enabled without engine → no request, translation failure preserves source text, unexpected engine exception doesn't break the pipeline, gated completion applies when released, updated event, stale translation result doesn't overwrite a re-delivered line, stop/reset cancels in-flight (line stays pending), bounded history, dispose stops, options validation, missing-target exception, target normalization, **live active-line translation** (partial translates in the target language, off makes no active-line request, failure preserves source, single-slot serialization + self-replenish to a newer partial, stale partial result discarded and never surfaced, result discarded when the line was committed, result discarded when translation disabled mid-flight, updated event, enabling translation mid-session translates the current partial) | 33 | Passed |
| `CaptionDisplayPolicy` (Q1 display-policy resolution) — null/empty state, active line rendered verbatim from the latest partial, committed finals newest-first in bounded history, translated text replaces source only when `Completed`, source preserved when translation not-requested/pending/failed, target-language badge exposed when translation enabled / absent when disabled | 10 | Passed |
| `CaptionPipeline` (fakes at the capture/STT boundaries: `FakeAudioCapture`/`FakeSpeechToTextEngine`/passthrough processor) — wiring capture→processor→STT→caption service, format conversion, partial/final flow, latency, capture error, recognition error, capture-factory error, audio-processing exception, stop/dispose, teardown ordering (`Stop` returns before teardown completes; `Dispose` waits), fail-on-start teardown paths, chunks-after-stop ignored | 20 | Passed |
| `AudioSourceLoader` — enumerates devices with preferred default selected, empty list has no preferred, enumeration failure surfaces without throwing, blank device normalized | 4 | Passed |
| `TranslationGuard` — source-equals-target rejected (case-insensitive), null/blank target rejected, different languages allowed | 4 | Passed |

### Notable Fixes Found by Testing

- **32-bit float conversion test** used `new WaveFormat(48000, 32, 1)`, which is 32-bit *integer* PCM; corrected to `WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)` to exercise the float path.
- **Extensible sub-format parsing**: NAudio 2.2.1 `WaveFormat.FromFormatChunk` returns `WaveFormatExtraData` for `WAVE_FORMAT_EXTENSIBLE`; the test helper now builds the native `WAVEFORMATEXTENSIBLE` struct in unmanaged memory and uses `WaveFormat.MarshalFromPtr`, which dispatches to `WaveFormatExtensible` (verified `SubFormat == MFAudioFormat_Float/PCM`).
- **`SampleRateConverter` rewrite**: the original kernel loop and ring buffer produced phase-skewed output (~889 Hz for a 1000 Hz sine). Rewritten as a sliding-window resampler with explicit dropped-frame eviction and pre-stream zero padding; output measures 1000.32 Hz for a 1000 Hz input.
- **`LoopbackDeviceEnumerator`**: removed invalid `using` on `MMDeviceCollection` (not `IDisposable` in NAudio 2.2.1); disposes each `MMDevice`.

## Manual Device Verification

Command: `dotnet run --project src/UniversalCaptions.Diagnostics -- --seconds 5`

```
Universal Live Captions - Audio Diagnostics
Runtime: 8.0.29 on Microsoft Windows NT 10.0.19045.0
Privacy: audio is processed in memory only; nothing is recorded or transmitted.

Output devices found: 1
  [0] Speaker/HP (Realtek(R) Audio)
Capturing system audio via WASAPI loopback.
Format: 48000 Hz, 2 ch, 32-bit. Press Ctrl+C to stop.

[================================] peak  0.985  rms  0.235    2 chunks /   140 ms  seq #4        elapsed 00:00:00
[================================] peak  0.985  rms  0.197    2 chunks /   130 ms  seq #6        elapsed 00:00:00
[========================        ] peak  0.736  rms  0.166    2 chunks /   120 ms  seq #8        elapsed 00:00:00
... (live meter tracks system playback levels)
```

Second run (timed stop): `--seconds 3` → `Capture stopped after 3.0s. Last chunk sequence: 47.` — capture starts, streams chunks continuously (~120–140 ms per 2-chunk window), and stops cleanly.

**Result: Passed.** Device enumeration works, loopback capture works, meter reacts to real audio, clean shutdown on timeout/Ctrl+C. Privacy respected: in-memory only, no persistence.

## Real-Model Verification (Slice 2)

Harness: `src/UniversalCaptions.Benchmarks` (Release) feeding `jfk.wav`, `jfk_noisy.wav`, `jfk_long.wav`, and `OSR_us_000_0010_8k.wav` through `WhisperSpeechToTextEngine` with the real ggml-tiny and ggml-base models (Whisper.net 1.9.1, CPU, 4 threads); streaming chunks fed at realtime pacing (0.5 s chunks / 0.5 s sleep). Full results in [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md).

```
=== Sample: jfk.wav (11.00s) ===
  === ggml-tiny.bin ===
    stream:   12.25s wall (1.11x realtime, 40.95s cpu); 6 partials, 2 finals
    first partial:  3.614s  avg lat 1404ms
    first final:    6.612s  avg lat 4002ms
  === ggml-base.bin ===
    stream:   12.47s wall (1.13x realtime, 42.44s cpu); 3 partials, 1 finals
    first partial:  4.509s  avg lat 2303ms
    first final:    9.876s  avg lat 6974ms

=== Sample: jfk_long.wav (22.50s) ===
  === ggml-tiny.bin ===
    stream:   23.01s wall (1.02x realtime, 85.42s cpu); 16 partials, 5 finals
    first partial:  2.886s  avg lat 1127ms
    first final:    5.267s  avg lat 794ms

=== Sample: OSR_us_000_0010_8k.wav (33.62s) ===
  === ggml-tiny.bin ===
    stream:   35.32s wall (1.05x realtime, 134.11s cpu); 22 partials, 11 finals
    first partial:  3.273s  avg lat 1450ms
    first final:    5.752s  avg lat 6184ms
  === ggml-base.bin ===
    stream:   35.66s wall (1.06x realtime, 134.31s cpu); 11 partials, 4 finals
    first partial:  4.590s  avg lat 2798ms
    first final:   10.045s  avg lat 9037ms
```

**Result: Passed.** Real models load (~0.3–0.8 s), **partial and final transcripts stream at realtime pacing** (≤1.16× wall), finals are committed progressively across windows on every sample, and Stop + DisposeAsync unwind cleanly with no exception.

### Notable Fix Found by Real-Model Testing

- **`WhisperSpeechToTextEngine.Dispose`**: Whisper.net 1.9.1 `WhisperProcessor.Dispose()` throws `"Cannot dispose while processing, please use DisposeAsync instead"` when a native decode is in flight (a stop can race an in-progress decode). Fixed by implementing `IAsyncDisposable` on the engine and disposing the processor via its `DisposeAsync()` (which waits for the decode to unwind), with sync `Dispose()` blocking on it. Locked by the `Stop_AndDisposeAsync_WhileDecodeInProgress_IsClean` regression test.

## Real-Argos Verification (Slice 3)

Ran end-to-end through `ArgosTranslationEngine` (real Python 3.11 + Argos 1.11.0 child process, `argos_translate_server.py` line protocol). Verified pairs and behavior:

| Request | Result |
|---|---|
| `en→tl` "Hello world, this is a live caption test." | **Passed** — "Hello world, ito ay isang live kapsiyon test." (first call includes ~12–14 s process+model load) |
| `ja→en` "こんにちは世界" | **Passed** — "Hello world" |
| `en→ja` "Good morning everyone" | **Passed** — "おはようございます" |
| `ja→tl` (no direct model; pivots via `en`) | **Passed** — `usedPivot=true`, `pivotLanguage=en` |
| unknown language code `zz` | **Passed** — `UnsupportedLanguage` |
| empty text | **Passed** — `EmptyInput` |
| source == target | **Passed** — `SourceEqualsTarget` |
| `tl` as **source** | **Known limitation** — Argos sentence-boundary detection does not support `tl` as a source; MVP pairs use `tl` only as a target (see ADR-0006) |

Offline check: translations ran with no network traffic during requests (packages installed during setup only; runtime is local). **Result: Passed** — direct pairs, pivoting, and error mapping all verified through the real engine. Benchmark details in [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md) (Slice 3 section).

## Acceptance Criteria Status (Slice 1)

| Criteria | Status | Evidence |
|---|---|---|
| Capture source starts, receives PCM, stops cleanly | **Passed** | `WasapiLoopbackCaptureSourceTests` (12) + manual run |
| Conversion handles 8/16/24/32-bit int and 32/64-bit float PCM | **Passed** | `ByteToFloatConverterTests` (10) |
| Ring buffer preserves ordering and handles wrap-around/overflow | **Passed** | `PcmRingBufferTests` (10) |
| Resampler preserves frequency content within tolerance | **Passed** | `SampleRateConverterTests` (12) |
| VAD distinguishes silence from speech deterministically | **Passed** | `EnergyVadTests` (6) |
| Level meter reports RMS/peak per chunk | **Passed** | `AudioLevelMeterTests` (8) |
| Device-disconnect/init failure maps to a user-readable error | **Passed** | `WasapiLoopbackCaptureSourceTests` failure mapping |
| Manual run shows live meter from system audio | **Passed** | Manual verification above |

### Acceptance Criteria Status (Slice 2)

| Criteria | Status | Evidence |
|---|---|---|
| `ISpeechToTextEngine` streaming contract verified with a fake engine | **Passed** | `ISpeechToTextEngineTests` (14) + `FakeSpeechToTextEngineTests` (4) |
| Local Whisper produces partial transcripts from captured audio | **Passed** | Real-model runs above (partials streamed at realtime pacing) |
| Streaming finals emitted (commit tuning) before Slice 4 | **Passed** | Stability-based committer (10 tests) + engine (13 tests); real-model runs commit finals on all four samples |
| Model selection benchmark recorded | **Passed** | [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md) — quality now discriminated on OSR sample (tiny 16.0% vs base 4.9% WER) |

### Acceptance Criteria Status (Slice 3)

| Criteria | Status | Evidence |
|---|---|---|
| `ITranslationEngine` contract verified with a fake engine | **Passed** | `FakeTranslationEngineTests` (8) |
| Argos translates source transcripts to the target language offline/local | **Passed** | Real-Argos verification above (direct + pivot pairs, offline) |
| Translation benchmark recorded (latency + quality per pair) | **Passed** | [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md) — Slice 3 section |
| Translation consumes Whisper FINAL segments (partials ignored) | **Passed (design)** | Finals feed is implemented in Slice 4 caption service; the engine contract is one-shot (text in/out) and the benchmark finals-stream path feeds discrete final segments |

## Slice 5 — Overlay + Control Window Verification (App)

Automated (done, 2026-08-01):

- `CaptionDisplayPolicyTests` (8) — resolved Q1 policy: active line rendered verbatim from the latest partial (`CaptionState.ActiveLine`); committed finals rendered newest-first as bounded history; translated text replaces the source on a committed line only when `CaptionTranslationStatus.Completed`; source preserved when translation is off/pending/failed.
- `CaptionPipelineTests` (20) — wiring verified against fakes at the capture/STT boundaries (`FakeAudioCapture`, `FakeSpeechToTextEngine`, passthrough processor): audio → processor → STT → caption-service flow, format conversion, partial/final flow, latency propagation, capture-error/recognition-error/capture-factory-error/audio-processing-exception surfacing, teardown ordering (`Stop` returns before component teardown completes; `Dispose` waits for teardown), fail-on-start teardown paths, idempotent stop/dispose, and chunks-after-stop ignored.
- `AudioSourceLoaderTests` (4) — device enumeration (preferred default), empty list, enumeration failure surfacing, blank-device normalization.
- `TranslationGuardTests` (4) — source-equals-target rejection (case-insensitive), null/blank target rejection, different-language allowance.

Manual (completed 2026-08-01, this Windows 10 machine — build 19045):

- [x] Launched `dotnet run --project src/UniversalCaptions.App` (built exe `src/UniversalCaptions.App/bin/Debug/net8.0-windows/UniversalCaptions.App.exe`). Both windows created: control window "Universal Live Captions" (400×448) and overlay "Captions" (720×180). Overlay verified via UIA: `Topmost=True`, `Layered=True` (transparent). No startup errors on stdout/stderr.
- [x] Device enumeration: audio source combo auto-selected the default render device `Speaker/HP (Realtek(R) Audio)` (WASAPI loopback endpoint id `{0.0.0.00000000}.{d16c2292-...}`).
- [x] **Real end-to-end capture + STT:** Started captions (status "Capturing system audio."). Played speech through SAPI (`System.Speech`) on the machine; real WASAPI loopback audio was captured, processed, fed through the real Whisper `ggml-base` model, and **live captions appeared in the overlay** — partials updated the active line and finals committed to the bounded history (hint text replaced by real transcripts). Ambient machine audio was also transcribed (expected — loopback captures all system audio).
- [x] Overlay interaction: drag-move (overlay follows the mouse), resize via grip (720×180 → 722×225), and click-through toggle (sets/clears `WS_EX_TRANSPARENT`) all behaved as designed (ADR-0004).
- [x] Stop/restart: Stop → status "Captions stopped.", Stop button disabled; Start again → "Capturing system audio." and a second session transcribed fresh audio.
- [x] Lifecycle: rapid Stop → close (Stop at 18:27:25.105, WM_CLOSE +118 ms) exited the process cleanly in ~2 s with no errors on stdout/stderr and no lingering process — bounded background teardown verified.
- [x] Error path: launched with `UC_STT_MODEL_PATH=C:\does-not-exist\ggml-base.bin` → Start surfaced the user-readable status "Whisper model file 'C:\does-not-exist\ggml-base.bin' was not found." with no crash.
- [x] Translation config guard: with source == target (`en`→`en`), enabling translation rejected it live with "Translation into en is not supported because the captions are already in en." (the `TranslationGuard` message; `SourceEqualsTarget`).
- [x] **Real-Argos wiring (end-to-end through the App):** Recreated the Argos venv (`argostranslate==1.11.0` + en→tl, tl→en, ja→en, en→ja packages under `C:\Users\TOGODB~1\AppData\Local\Temp\argosv`, short 8.3 path per TD-011), prepended its `Scripts` dir to PATH, and verified the line-protocol server directly (en→tl "Hello world, ito ay isang live kapsiyon test." ~2.5 s) with both the full venv python path and the bare PATH-resolved `python`. In the live App run (2026-08-01): toggled translation ON, selected **Tagalog (tl)** in the target combo, started captions, played speech via SAPI, and **committed overlay lines displayed real translated Tagalog** — `tamad aso.` and `Ang mabilis na kayumangging sorra ay tumatalon sa ibabaw ng eruplano` — with `IsTranslated = True` on the committed history lines. The App spawned the Argos child chain (`python` venv shim → UV base python running `argos_translate_server.py`), which served the translations. This also exercised the `ControlWindow.ApplyTranslationSettings` guard fix (toggle stays ON + target combo stays enabled on a guard error so a valid target can be selected). Engine-level real-Argos verification (direct + pivot pairs, offline) is in [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md) (Slice 3 section).
- Note (latency): first-session Whisper latency read ~23 765 ms / 38 581 ms on the second (under ambient machine load with the `ggml-base` model on this CPU). This is a real observed measurement, not a code defect; latency tuning is Slice 6 work (window size, decode interval, `StabilityWindow`).

**Result: Passed** for overlay/control-window behavior, device enumeration, real capture → Whisper → overlay captions, interaction (move/resize/click-through), lifecycle (stop/restart/clean close), the model-not-found + source-equals-target error paths, and the **real-Argos wiring end-to-end through the App** (committed overlay lines translated to Tagalog by a real local Argos child process).

## Slice 5 Post-Close-Out Refinement — Live Active-Line Translation + Chrome-Style Overlay (Entry 7)

Automated (done, 2026-08-01):

- `CaptionService` (9 new tests) — live active-line translation: a partial is translated into the target language while the speaker is still talking; translation off makes no active-line request; a failure preserves the source; a **single in-flight slot** serializes requests and self-replenishes to translate a newer partial that arrived meanwhile; a **stale result** for a superseded partial is discarded and never surfaced; a result is discarded when its line was committed or when translation was **disabled mid-flight**; `CaptionLineUpdated` fires when a live translation applies; enabling translation mid-session translates the current partial.
- `CaptionState.ReplaceActiveLine` (4 new tests) — active-line translation replacement by exact line identity (applies; stale instance rejected; after-clear no-op; state validation).
- `CaptionDisplayPolicy` (2 new tests) — the overlay model exposes an uppercase target-language badge when translation is enabled, none when disabled.

Manual (**completed 2026-08-01**, this Windows 10 machine — build 19045): with the redesigned overlay (auto-sized translucent pill, white text, target-language badge, expand/collapse chevron, hide button) and live active-line translation, ran the real audio + real-Argos pass with the App started against the WASAPI loopback device and the dev Argos venv on PATH (target `tl`). **Passed.** SAPI-paced English speech was captured by loopback, transcribed by Whisper `ggml-base`, and **live-translated into Tagalog on the in-progress overlay line while the speaker was still talking, before commit**. A 300 ms UIA poll timeline of the `ActiveCaption` element shows the English partial being replaced by Tagalog on the active line within ~0.2–1 s of the partial appearing (e.g. `TS=4.011 ' world Okay'` → `TS=4.241 'Daigdig Okay'`; `TS=8.517 '...This.'` → `TS=8.724 '...Ito.'`; `TS=12.514 'Ito ay'`; `TS=23.233 'Pagsasalin'`; `TS=27.118 'pagsubok'`; `TS=53.475` full-sentence `Ang mabilis na brown fox ay lumukso sa ibabaw ng tamad na aso.`). The `TL` badge (`LanguageBadge`) was present in every sample. Chevron expand/collapse verified: expanding reveals the committed-history list (8 committed `CaptionDisplayLine` items, all `IsTranslated = True`, e.g. `Daigdig Okay`, `Okay Hello Ito`, `mabuhay. Pagsasalin. pagsubok.`, `TUST 1. 2. 3.`), and the pill auto-sizes 235→109 px. Close (X) verified: overlay window leaves the UIA tree. "Show Captions" verified: overlay re-appears (`IsOffscreen=False`). Pipeline-while-hidden verified: with the overlay hidden, spoken English was still transcribed and live-translated (`The meeting starts at nine o'clock` → `Nagsisimula ang pulong sa alas - 9.` on re-show; history grew to 9 committed lines, all translated). Note: the previous manual items that referenced the 720×180 fixed-size overlay, the resize grip, and "translated text on committed lines only" are superseded by this redesign.

## Slice 6 — Phase 1c: App-Level SAPI E2E Validation (shortlist vs baseline)

Completed **2026-08-01**, this Windows 10 machine (build 19045), Release App build. **Purpose:** the validation gate between the controlled OFAT sweep (Phase 1b) and real-world apps (Phase 2) — measure real end-to-end latency through the real App with real WASAPI loopback audio + the real local Argos child process, at baseline + shortlisted configs.

### Protocol (identical for every run)

- **Harness:** fresh `UniversalCaptions.App.exe` process per run (Release, `bin/Release/net8.0-windows`), working dir = repo root; Argos dev venv `Scripts` dir prepended to PATH; translation ON + target **Tagalog (tl)** selected in the control window; Start Captions; SAPI-paced fixed English corpus; then Stop + window-close (all via UIA). No parameters changed between repetitions within a config.
- **Speech:** fixed 6-sentence English script (≈30 s, `SpeechSynthesizer` rate 0, volume 100) played through the default render device captured by WASAPI loopback. Same text/device/settings every run.
- **Measurement:** a 100 ms UIA poll of the control-window **E2E latency row** (`partial: … ms · final: … ms`, Phase 1a) and the **STT latency** row for the whole speech + a 12 s settle tail; every distinct value that the gauges advanced to was recorded (each advance = a translated caption actually published to subscribers). Per run we record the multiset of observed E2E partials/finals + last STT latency + the overlay's final active-caption text (Tagalog evidence). Raw series: `artifacts/reports/e2e/series.csv`; per-run aggregates: `artifacts/reports/e2e/runs.csv` (git-ignored).
- **Configs × 3 runs each:** baseline/control `base 8 s/1 s/st3` (App defaults, via `UC_STT_STABILITY=3`), shortlist A `base 8 s/1 s/st2` (`UC_STT_STABILITY=2`), shortlist B `tiny 8 s/1 s/st2` (`UC_STT_MODEL_PATH=…ggml-tiny.bin` + `UC_STT_STABILITY=2`).

### Results (all E2E = audio capture time `CapturedAtUtc` → translated caption published; see Phase 1a definition)

| Config (model / window / interval / stability) | E2E final median | E2E final worst | Warm last-final E2E median | E2E partial median | Last STT latency median | Translated finals observed | Translation published? |
|---|---|---|---|---|---|---|---|
| **base / 8 s / 1 s / st3** (baseline/control) | 20.96 s | 29.3 s | 9.06 s | 5.54 s | 6.49 s | 10 (3–4 per run) | Yes — all 3 runs |
| **base / 8 s / 1 s / st2** (shortlist A, accuracy-first) | 19.65 s | 29.4 s | 10.98 s | 6.00 s | 4.18 s | 16 (4–8 per run) | Yes — all 3 runs |
| **tiny / 8 s / 1 s / st2** (shortlist B, latency-first) | 16.25 s | 24.6 s | 7.45 s | 4.41 s | 3.61 s | 18 (4–8 per run) | Yes — all 3 runs |

Reading the table:
- **E2E final median/worst** = distribution of every translated-final E2E sample across the 3 runs. These are inflated by the **Argos cold start** on the first translated line of each session (~14 s process + model load; every run launches a fresh App process, so every run pays it) plus the trailing-window component; the **warm last-final E2E median** isolates the steady-state last line (the last final of the script, warm Argos).
- **E2E partial median** = live active-line translation E2E samples. These are sparse (1–3 per run; none in `tiny-st2` run 2) by design: the single in-flight Argos slot supersedes most partials before their translation completes, so only 0–3 translated partials actually apply per session. Treat partial medians as low-confidence.
- **Last STT latency median** = capture→emit latency of the last committed final (the `LatencyUpdated` value; unchanged metric).
- **Translated finals observed** = count of distinct translated-final E2E samples (≈ translated committed lines published per run), a commit-rate proxy consistent with the Phase 1b finding.
- Sample counts per run are 3–8 finals, so **P95 ≈ worst** (no meaningful P95 below the max at these N); the "worst" column is the max across run maxima.

### Findings

- **tiny/8/1/st2 is the latency winner end-to-end:** lowest E2E final median (16.25 s vs 20.96 s baseline, −4.7 s, i.e. ~22% lower), lowest warm last-final E2E (7.45 s), lowest STT latency (3.61 s), and the most translated finals (18 vs 10) — it commits text soonest. Consistent with Phase 1b (tiny decodes faster → more stability passes → more commits), with the known accuracy trade-off (OSR full-file WER 16.0% vs base 4.9%).
- **base/8/1/st2 ≈ baseline on E2E final** (19.65 vs 20.96 s, within run-to-run noise) **but commits more finals** (16 vs 10) and has lower STT latency (4.18 vs 6.49 s) — st2 commits faster with identical model accuracy, as the OFAT sweep predicted (first-final cut ~2.1–2.4 s).
- **base/8/1/st3 (control)**: fewest finals, highest STT latency — correct as the conservative default.
- **All 9 runs published real translated Tagalog** (`SawTranslation = True`), e.g. overlay active captions `"Sinusubukan namin ngayon ang live na mga kapsiyon sa makinang ito."`, `"Nabihag ng sistema ang audio, nakilala ang talumpati, at isinalin ito sa Tagalog."` — no missed/failed translations observed. No lingering processes after runs.
- **Caveats:** run-to-run variance is real (ambient CPU, Argos session warm-up); 3 reps is a minimum; first-final E2E is dominated by Argos cold start in every config, so the config ranking rests on warm finals + STT latency + commit rate.

### Phase 1c conclusion

The shortlist is validated end-to-end through the real App. The latency-first candidate is **tiny/8/1/st2**; the accuracy-preserving candidate is **base/8/1/st2**; the previous-default control is **base/8/1/st3**. Per the user's decision, the validated baseline **`base/8/1/st2` was promoted to the App default on 2026-08-01**: `StabilityWindow` 3→2 (`WhisperEngineOptions` + App + benchmark, one authoritative config), model default `ggml-base` unchanged (see `PROJECT_STATUS.md` "Slice 6 Baseline Defaults"). This is the **validated baseline for the current release**; Phase 2 (YouTube/Chrome, VLC, Zoom) remains **deferred per user** — real-world validation/reassessment, not additional optimization sweeps — and the defaults may be revisited after it.

## Known Gaps / Deferred

- Real-device verification was performed on this machine only; a second output device or a different machine is not recorded.
- Resampler benchmark against speech signals (TD-001) still open; unit tests verify sine frequency preservation only.
- Device-change notifications not yet wired (TD-002).
- Default model: **ggml-base** (user-approved; tiny kept as a low-resource fallback) — see ADR-0003 / BENCHMARK_REPORT.
- Slice 5 manual overlay/device verification **completed 2026-08-01** (recorded in the Slice 5 section above), including the **real-Argos wiring end-to-end through the App** (committed overlay lines translated to Tagalog via a real local Argos child process); **Slice 5 closed out 2026-08-01**.
- **Slice 6 is complete (close-out 2026-08-01)** — Phases 1a (E2E metric + tests), 1b (OFAT sweep + shortlist), and 1c (App-level SAPI E2E validation) all complete; the validated baseline **base/8/1/st2 was promoted to the App default** (`StabilityWindow` 3→2; model `ggml-base` unchanged — see `PROJECT_STATUS.md` "Slice 6 Baseline Defaults"). **Phase 2 — real-application validation (YouTube/Chrome, VLC, Zoom) — is deferred per user** and is a future reassessment pass over the baseline defaults, not a prerequisite for the current release.

## Conclusion

Slice 1 Definition of Done is satisfied (build green, tests pass, real-device capture recorded). Slice 2 Definition of Done is satisfied on tests and evidence (107 tests total, streaming finals committed on every sample, build 0 warnings/0 errors, default model user-approved). Slice 3 Definition of Done is satisfied on tests and evidence: the `ITranslationEngine` contract is verified with a fake engine (8 tests), `ArgosTranslationEngine` is verified with a fake process seam (13 tests) and against the real Argos 1.11.0 process (direct pairs + pivoting + error mapping, offline), and the translation benchmark is recorded (128 tests total, build 0 warnings/0 errors). Fresh-context review findings were fixed (stale-process recovery, unwrapped exceptions, Python crash path, options validation, `ArgumentList`, UTF-8 pinning) and the remaining items logged as TD-013–TD-015.

Slice 4 — Caption Service is **complete** (close-out approved 2026-08-01): `ICaptionService`/`CaptionLine`/`CaptionState` contracts live in `UniversalCaptions.Core.Captions`, and `CaptionService` in `UniversalCaptions.Captions` consumes only Core. The partial→active→final→committed transition, optional background translation (failure preserves the source caption), ordering, duplicate prevention, bounded history, session lifecycle, and cancellation are verified with deterministic fake translation engines (40 tests, build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, no vulnerable packages). A fresh-context review was completed and its findings fixed (snapshot history reads, deferred CTS disposal, atomic translation-start token, stale-translation identity guard, event-raising moved out of the translation catch, target normalization). All Slice 4 Definition-of-Done items are satisfied.

Slice 5 — Overlay + Control Window is **complete (close-out 2026-08-01)**: `UniversalCaptions.App` implementation and its deterministic unit tests are complete — 209/209 tests total (66 Audio + 45 Captions + 41 Speech + 21 Translation + 36 App), build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, no vulnerable packages. The resolved Q1 display policy and the capture→processor→STT→caption-service wiring are verified with fakes. **Manual overlay/device verification completed 2026-08-01**: real system audio → Whisper `ggml-base` → live overlay captions, always-on-top/transparency, drag/resize/click-through, stop/restart, rapid Stop→close (clean ~2 s exit), and the model-not-found + source-equals-target error paths all verified on this Windows 10 machine. **Real-Argos wiring verified end-to-end through the App 2026-08-01**: with the dev Argos venv (recreated under a short 8.3 path per TD-011), committed overlay lines displayed real translated Tagalog (`tamad aso.`, `Ang mabilis na kayumangging sorra ay tumatalon sa ibabaw ng eruplano`, `IsTranslated = True`) served by the App-spawned Argos child process; this also exercised the `ApplyTranslationSettings` guard fix (toggle stays ON + target combo enabled on guard error). All Slice 5 Definition-of-Done items are satisfied.

**Post-close-out refinement (Entry 7, 2026-08-01):** live active-line translation + Chrome-style overlay redesign are implemented with automated tests **224/224** (66 Audio + 58 Captions + 41 Speech + 21 Translation + 38 App), build 0 warnings/0 errors, format clean. **Manual verification of the redesigned overlay + live active-line translation completed 2026-08-01** (recorded in the Slice 5 refinement note above): Tagalog appears on the in-progress overlay line before commit, `TL` badge, chevron expand/collapse of history, close-hide, "Show Captions" re-show, and pipeline-continues-while-hidden all verified against real audio + real Argos. **Entry 7 closed out 2026-08-01.**

**Slice 6 (Entry 8) — complete (close-out 2026-08-01):** Phase 1a (E2E latency metric + tests, **238/238**) and Phase 1b (OFAT sweep + shortlist) are complete, and **Phase 1c — App-level SAPI E2E validation — completed 2026-08-01** (recorded in the Slice 6 Phase 1c section above): baseline + shortlist configs × 3 runs each through the real App (loopback → Whisper → Argos en→tl → overlay), every run publishing real translated Tagalog. Latency winner **tiny/8/1/st2** (E2E final median 16.25 s incl. Argos cold start; warm last-final 7.45 s; STT 3.61 s; 18 translated finals); accuracy-preserving candidate **base/8/1/st2** (commits faster than the old default with identical model accuracy); control **base/8/1/st3**. **The validated baseline `base/8/1/st2` was promoted to the App default on 2026-08-01** (`StabilityWindow` 3→2, model `ggml-base` unchanged — one authoritative config) as the validated baseline for the current release. A fresh-context review of the Phase 1a E2E metric code completed clean (no findings). Phase 2 real-app validation (deferred per user) is a future reassessment pass over the baseline defaults. **All MVP slices (0–6) are complete.**

## Argos Pre-Warm — First-Caption Latency Verification (2026-08-02)

**Manual verification of the Argos background pre-warm, real Windows 10 machine.** Objective: reduce first-caption latency from the ~28-34 s Argos cold-start to ~5-7 s by warming Argos (Python + language discovery + model load, then first `en->tl` inference) in the background when translation is enabled, so the first real caption reuses a warmed process/model.

**Harness:** UIA-launched `UniversalCaptions.App.exe` (Release, cwd = repo root), translation ON + target **Tagalog (tl)**, Argos dev venv python via `UC_ARGOS_PYTHON`, SAPI-paced English clip over default render (WASAPI loopback), App stderr captured to files for `[DIAGNOSTICS] T0-T8` + `[ARGOS-DIAG]` traces.

### Case A — warm-up finishes before playback (headline fix)

| Measurement | Before (cold) | Case A observed |
|---|---|---|
| First audio (T1-T0) | ~0.06 s | **0.064 s** ✓ |
| Whisper Partial (T3-T2) | ~2.0 s | **2.060 s** ✓ |
| Whisper Final (T4-T3) | ~3.6 s | **3.553 s** ✓ |
| Argos first translation (T6-T5) | **23.06 s+** | **0.463 s** (real id=3 round-trip **0.454 s**) |
| First caption (E2E final) | **~28-34 s** | **3.80 s / 6.85 s** |

- `pre-warm ready in 24.2 s` finished ~30 s clip; cold costs paid before first caption.
- Post-warm translation round-trips **0.17-1.50 s**. The 20-30 s Argos gap is gone; first caption ~4-7 s.

### Case B — speech starts during warm-up (concurrency)

- Clip played ~2 s after Start, before background pre-warm finished.
- Exactly **one** process spawn (`T5b ... 0.011 s`) and **one** model load (`T5c/T5d ... 13.4 s`); the first real translation awaited the same shared `_startTask`/`_warmTask` and completed in **0.355 s** once warm finished — **no second process/initialization**.
- The ~23 s first caption in this case is expected/correct: playback began before warm-up was done, so the real caption waits on the one warm-up rather than triggering a duplicate.

**Result: Passed.** Case A meets the ~5-7 s first-caption target; Case B confirms the single shared initialization + lazy-fallback concurrency requirement. Tests 260/260, build 0 warnings/0 errors, format clean; baseline defaults unchanged.

## Slice 7 — Caption Layout & Stable Incremental Rendering (2026-08-02)

**Slice:** stable incremental rendering (A) + scope-limited bottom scrolling (C), after a measurement-first diagnosis of the reported "whole text re-flows / newest content jumps" symptom. Translation (Whisper/Argos/latency) path untouched.

### Task B — width/measurement diagnosis (probe, deterministic STA)

Layout probe `CaptionLayoutProbeTests` recreates the exact overlay tree `ScrollViewer(522px viewport) → Grid → StackPanel → TextBlock(font 20, L260)` and measures real WPF layout:

| Case | Realized width | Available text width | Wrapped lines |
|---|---|---|---|
| "two words" (short) | ~522 px | ~522 px | **1** |
| long sentence | ~522 px | ~522 px | ≥ 2 (wraps only on exhaustion) |
| "the quick" vs long tail | ~522 px (constant) | ~522 px | grows, width constant |

**Verdict: width is correct.** A caption fills the full ~522px viewport and stays on one line for short utterances (it does not measure at its natural word width, so appended tails don't force premature new lines); long text wraps only when 522px is exhausted; growing tails keep a constant fill width. The reported reflow is therefore **not** a width/measurement problem — it must be in the render path.

### Task A — stable incremental rendering (fixed + verified by render-identity test)

`UpdateCaptionItems`/`ReconcileHistory` now return whether a new block was inserted. A Partial only mutates the live `TextBlock`'s `Text` in place; history `TextBlock` instances are reused by sequence and never rebuilt; a Final inserts the committed line as a fresh history block while the single live block is reused for the next phrase.

`CaptionRenderIdentityTests` (4) drive the real `CaptionOverlayWindow` (STA + reflection) and assert **block instance identity is preserved**:
- Partial stream → identical history instances before/after, only active text changes;
- growing Partial → same active instance, text updated in place;
- Final → finalized text becomes history, live block reused for next partial;
- multiple finals → first/second history instances stay `Assert.Same`, order + text preserved.

### Task C — verification via scope-limited bottom scroll

The overlay no longer forces `ScrollToBottom` and no longer re-runs the bottom re-anchor on every caption render. It scrolls only when a new caption block was inserted (a Final or the first line) and the content overflows the fixed-height viewport; a Partial that rewrites the live line alone never scrolls and never reflows history. Window re-anchor runs only on Loaded / collapse / hover (where size actually changes).

**Gates:** App tests 51 → **58** (3 layout probe + 4 render-identity); solution **267/267** (66 Audio + 71 Captions + 45 Speech + 27 Translation + 58 App); build 0 warnings / 0 errors; `dotnet format --verify-no-changes` clean; baseline defaults unchanged; Whisper/Argos/latency path untouched.
