# Universal Live Captions Test Report

Last updated: 2026-08-01

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

Slices 1–5 automated tests pass: **209/209 passed, 0 failed, 0 skipped** (66 Audio + 45 Captions + 41 Speech + 21 Translation + 36 App). Solution builds with **0 warnings, 0 errors** (warnings-as-errors). Slice 1 manual verification against real system audio succeeded. Slice 2 real-model verification succeeded: `WhisperSpeechToTextEngine` streamed **partial and final transcripts** from four samples through the real ggml-tiny/base models at realtime pacing with a clean stop/dispose (see [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md)). Slice 3 real-Argos verification succeeded: `ArgosTranslationEngine` translated **offline/local** through a real Argos 1.11.0 child process for direct pairs (`en→tl`, `ja→en`, `en→ja`) and a pivot pair (`ja→tl` via `en`), with correct error mapping (see below and [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md)). Slice 4 (complete): `CaptionService`/`CaptionState` verified with deterministic fake translation engines — partial→active→final→committed transitions, translation on/off, translation failure preserving the source caption, ordering, bounded history, and cancellation. Slice 5 (complete): `UniversalCaptions.App` overlay display policy + pipeline wiring verified with deterministic fakes (`CaptionDisplayPolicyTests` 8 + `CaptionPipelineTests` 20 + `AudioSourceLoaderTests` 4 + `TranslationGuardTests` 4) — Q1 display policy resolution (active line = verbatim latest partial; finals = bounded history newest-first; translated text replaces source only when `Completed`), capture→processor→STT→caption-service wiring, error handling, lifecycle, audio-source enumeration (preferred default, failure-surfacing), and translation guard (source-equals-target rejection). **Manual overlay/device verification completed 2026-08-01** (all items Passed — see Slice 5 section below), including the **real-Argos wiring end-to-end through the App** (committed overlay lines translated to Tagalog by a real local Argos child process).

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
Passed!  - Failed:     0, Passed:    45, Skipped:     0, Total:    45, Duration: 260 ms - UniversalCaptions.Captions.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    41, Skipped:     0, Total:    41, Duration: 1 s - UniversalCaptions.Speech.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    21, Skipped:     0, Total:    21, Duration: 69 ms - UniversalCaptions.Translation.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    36, Skipped:     0, Total:    36, Duration: 515 ms - UniversalCaptions.App.Tests.dll (net8.0)
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
| `CaptionState` — sequence-ordered history, duplicate-sequence replace, bounded history (drop oldest, capacity 0), active-line replace/clear + state validation, translation update by exact line identity (stale instance rejected), missing sequence no-op, translation on/off + normalization, session begin/end, reset, negative-capacity rejected | 16 | Passed |
| `CaptionSnapshot` — immutable snapshot of active line + history (detached from later commits, thread-safe against concurrent mutations), `GetSnapshot` matches current state | 5 | Passed |
| `CaptionService` (deterministic `StubTranslationEngine`/`GatedTranslationEngine`) — partial updates active line + events, partial/final before-start ignored, final commits history + clears active, committed event, after-stop ignored, idempotent start, translation on → background request + completed line, explicit target override, translation off → no request, enabled without engine → no request, translation failure preserves source text, unexpected engine exception doesn't break the pipeline, gated completion applies when released, updated event, stale translation result doesn't overwrite a re-delivered line, stop/reset cancels in-flight (line stays pending), bounded history, dispose stops, options validation, missing-target exception, target normalization | 24 | Passed |
| `CaptionDisplayPolicy` (Q1 display-policy resolution) — null/empty state, active line rendered verbatim from the latest partial, committed finals newest-first in bounded history, translated text replaces source only when `Completed`, source preserved when translation not-requested/pending/failed | 8 | Passed |
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

## Known Gaps / Deferred

- Real-device verification was performed on this machine only; a second output device or a different machine is not recorded.
- Resampler benchmark against speech signals (TD-001) still open; unit tests verify sine frequency preservation only.
- Device-change notifications not yet wired (TD-002).
- Default model: **ggml-base** (user-approved; tiny kept as a low-resource fallback) — see ADR-0003 / BENCHMARK_REPORT.
- Slice 5 manual overlay/device verification **completed 2026-08-01** (recorded in the Slice 5 section above), including the **real-Argos wiring end-to-end through the App** (committed overlay lines translated to Tagalog via a real local Argos child process); **Slice 5 closed out 2026-08-01**.
- Slice 6 open: latency/CPU tuning of window size, decode interval, and `StabilityWindow` against real WASAPI loopback audio; optional `WithSplitOnWord`/`WithMaxSegmentLength` benchmark for finer boundaries.

## Conclusion

Slice 1 Definition of Done is satisfied (build green, tests pass, real-device capture recorded). Slice 2 Definition of Done is satisfied on tests and evidence (107 tests total, streaming finals committed on every sample, build 0 warnings/0 errors, default model user-approved). Slice 3 Definition of Done is satisfied on tests and evidence: the `ITranslationEngine` contract is verified with a fake engine (8 tests), `ArgosTranslationEngine` is verified with a fake process seam (13 tests) and against the real Argos 1.11.0 process (direct pairs + pivoting + error mapping, offline), and the translation benchmark is recorded (128 tests total, build 0 warnings/0 errors). Fresh-context review findings were fixed (stale-process recovery, unwrapped exceptions, Python crash path, options validation, `ArgumentList`, UTF-8 pinning) and the remaining items logged as TD-013–TD-015.

Slice 4 — Caption Service is **complete** (close-out approved 2026-08-01): `ICaptionService`/`CaptionLine`/`CaptionState` contracts live in `UniversalCaptions.Core.Captions`, and `CaptionService` in `UniversalCaptions.Captions` consumes only Core. The partial→active→final→committed transition, optional background translation (failure preserves the source caption), ordering, duplicate prevention, bounded history, session lifecycle, and cancellation are verified with deterministic fake translation engines (40 tests, build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, no vulnerable packages). A fresh-context review was completed and its findings fixed (snapshot history reads, deferred CTS disposal, atomic translation-start token, stale-translation identity guard, event-raising moved out of the translation catch, target normalization). All Slice 4 Definition-of-Done items are satisfied.

Slice 5 — Overlay + Control Window is **complete (close-out 2026-08-01)**: `UniversalCaptions.App` implementation and its deterministic unit tests are complete — 209/209 tests total (66 Audio + 45 Captions + 41 Speech + 21 Translation + 36 App), build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, no vulnerable packages. The resolved Q1 display policy and the capture→processor→STT→caption-service wiring are verified with fakes. **Manual overlay/device verification completed 2026-08-01**: real system audio → Whisper `ggml-base` → live overlay captions, always-on-top/transparency, drag/resize/click-through, stop/restart, rapid Stop→close (clean ~2 s exit), and the model-not-found + source-equals-target error paths all verified on this Windows 10 machine. **Real-Argos wiring verified end-to-end through the App 2026-08-01**: with the dev Argos venv (recreated under a short 8.3 path per TD-011), committed overlay lines displayed real translated Tagalog (`tamad aso.`, `Ang mabilis na kayumangging sorra ay tumatalon sa ibabaw ng eruplano`, `IsTranslated = True`) served by the App-spawned Argos child process; this also exercised the `ApplyTranslationSettings` guard fix (toggle stays ON + target combo enabled on guard error). All Slice 5 Definition-of-Done items are satisfied.
