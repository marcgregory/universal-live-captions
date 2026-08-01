# Universal Live Captions Build Plan

Last updated: 2026-08-01

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

**Status: In progress (2026-08-01)** — implementation + unit tests complete; manual overlay/device + real-Argos verification pending before close-out.

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
7. Write `UniversalCaptions.App.Tests` — `CaptionDisplayPolicyTests` (8) + `CaptionPipelineTests` (14 with fakes at the capture/STT boundaries)
8. Verify gates (build 0 warnings, 190/190 tests, format clean, no vulnerable packages); record results in `TEST_REPORT.md`
9. Manual verification (pending): run `dotnet run --project src/UniversalCaptions.App`, verify overlay visuals/always-on-top/click-through/resize on real system audio and record evidence; real-Argos wiring when the dev Argos venv is available
10. Close-out: fresh-context review + docs (CHANGELOG, PROJECT_STATUS, TEST_REPORT, ROADMAP, BUILD_PLAN) + close-out record in `CHANGE_IMPACT_ANALYSIS.md` Entry 6

#### Definition of Done

- [x] `IOverlayService` + overlay/control windows + DI composition root in `UniversalCaptions.App`
- [x] Overlay renders `CaptionState` (active + history) with the resolved Q1 display policy
- [x] Pipeline wiring + status/latency surfaced in the control window; UI marshals events to the dispatcher
- [x] `UniversalCaptions.App.Tests` (22 tests) with fakes at the capture/STT boundaries; total **190/190**
- [x] Build 0 warnings/0 errors; `dotnet format --verify-no-changes` clean; no vulnerable packages
- [ ] Manual verification of the overlay + control window on real system audio (recorded in TEST_REPORT)
- [ ] Real-Argos wiring verified when the dev Argos venv is available (translation stays Off by default otherwise)
- [ ] Fresh-context review completed
- [ ] Close-out docs + Entry 6 close-out record completed

#### Slice 5 Evidence (2026-08-01)

- `UniversalCaptions.App` (net8.0-windows, UseWPF) is the DI composition root: `ArgosTranslationEngine` → `CaptionService` ("en", target "en", history 50) → `AudioProcessor` (16 kHz mono) → capture/STT factories (`WasapiLoopbackCaptureSource` default or by device; `WhisperSpeechToTextEngine` with `UC_STT_MODEL_PATH` env override, default `artifacts/models/ggml-base.bin`) → `CaptionPipeline` → `CaptionOverlayWindow` + `ControlWindow`.
- `CaptionDisplayPolicyTests` (8) verify the resolved Q1 policy: active line = latest partial; committed finals newest-first; translated text replaces source only when `Completed`; source preserved when off/pending/failed.
- `CaptionPipelineTests` (14) verify wiring against `FakeAudioCapture`/`FakeSpeechToTextEngine`/passthrough processor: audio → processor → STT flow, format conversion, partial/final flow into `CaptionService`, latency, capture/recognition/capture-factory errors, stop/dispose, and chunks-after-stop ignored.
- Final gates green: `dotnet build UniversalCaptions.slnx` 0 warnings/0 errors, `dotnet test UniversalCaptions.slnx --no-build` **190/190**, `dotnet format --verify-no-changes` clean, `dotnet list package --vulnerable` no vulnerable packages (all 13 projects).
- Known caveats (honest status): WPF visuals and real-device capture are **not yet manually verified** (ADR-0004); real Argos wiring runs only when the dev Argos venv is present (this machine currently has no argostranslate on system Python — translation defaults Off); these remain before Slice 5 close-out.

### Slice 6 — End-to-End

#### Goal

Verify the full pipeline on real audio and measure latency/accuracy.

#### Scope

- YouTube/Chrome verification
- VLC or Zoom verification
- Latency measurement; Whisper model benchmark and Argos pair benchmark; record findings and set default model/pair
