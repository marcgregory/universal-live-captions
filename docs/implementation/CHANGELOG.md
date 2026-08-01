# Universal Live Captions Changelog

Last updated: 2026-08-01

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
