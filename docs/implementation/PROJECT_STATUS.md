# Universal Live Captions Project Status

Last updated: 2026-08-01

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

Slice 5 — WPF overlay + control window (render `CaptionState`, consume `ICaptionService` events on the dispatcher) — **in progress**: implementation + unit tests complete (2026-08-01); manual overlay/device + real-Argos verification pending.

## Current Progress

Slice 1 (Audio Capture Spike), Slice 2 (STT Spike), Slice 3 (Translation Spike), and **Slice 4 (Caption Service) are complete and verified.** Slice 4 close-out approved 2026-08-01: `ICaptionService`, `CaptionLine`, `CaptionState`, and `CaptionServiceOptions` in `UniversalCaptions.Core.Captions` (contracts in Core so `UniversalCaptions.Captions` depends only on Core, per the ADR-0003/0006 precedent); `src/UniversalCaptions.Captions` implements `CaptionService`: partials replace the active line, finals commit to a bounded sequence-ordered history, optional background translation (failure preserves the source caption; stale results matched by line identity are dropped), cancellation of in-flight translations on stop/reset/dispose, and events raised outside the serialization gate with snapshot `History`. Verified with deterministic `StubTranslationEngine`/`GatedTranslationEngine` fakes — 40 tests, build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, no vulnerable packages. Fresh-context review completed; findings fixed (snapshot history reads, deferred CTS disposal, atomic translation-start token, stale-translation identity guard, event-raising moved out of the translation catch, target normalization).

**Slice 5 (WPF overlay + control window) is implemented with unit tests complete (2026-08-01); manual verification pending.** `src/UniversalCaptions.App` (new WPF project, `net8.0-windows`, `UseWPF`, PerMonitorV2 manifest) is the DI composition root: `IOverlayService` (visibility/position/opacity/font size/click-through), `CaptionOverlayWindow` (borderless/transparent/always-on-top, history + active line, drag/resize, click-through via `WS_EX_TRANSPARENT`), `CaptionPipeline` (wiring capture → processor → STT → `CaptionService` via `Func` factories, idempotent Start/Stop/Dispose, `StatusChanged`/`LatencyUpdated` events, error handling), `ControlWindow` (audio source/language, translation on/off + target, status/latency, overlay sliders, Start/Stop), and `App.xaml.cs` (DI registration + `ShutdownMode.OnMainWindowClose`). The deferred Q1 display policy is resolved: the active caption renders verbatim from the latest partial (`CaptionState.ActiveLine`); committed finals render as bounded history; translated text replaces the source on a committed line only when translation completes (PRD FR-5/FR-14). Verified with `UniversalCaptions.App.Tests` — `CaptionDisplayPolicyTests` (8) + `CaptionPipelineTests` (14, fakes at the capture/STT boundaries). Total test count: **190/190 passing** (66 Audio + 40 Captions + 41 Speech + 21 Translation + 22 App). Remaining before close-out: manual verification of the overlay visuals and real-device capture on this Windows 10 machine, and real-Argos wiring when the dev Argos venv is available.

The `ITranslationEngine` contract (with `TranslationResult`, `TranslationErrorKind`, `TranslationException`) lives in `UniversalCaptions.Core.Translation`; it is verified with a deterministic `FakeTranslationEngine` (8 tests); `ArgosTranslationEngine` (child Python process over a newline-delimited JSON line protocol, bundled `argos_translate_server.py`) is verified with a fake process seam (13 tests, incl. restart-after-fatal-error) and against real Argos 1.11.0 end-to-end (direct pairs `en→tl`, `ja→en`, `en→ja`; pivoting `ja→tl` via `en`). The translation benchmark is recorded (load/first latency, steady-state distinct-text latency, identical-input cache, throughput, Argos working set, finals-stream ordering, char-similarity quality). Fresh-context review completed; findings fixed (stale-process recovery, unwrapped exceptions, Python crash path, options validation, `ArgumentList`, UTF-8 pinning) with remaining items in TD-013–TD-015.

## Current Focus

Finishing Slice 5: manual verification of the overlay + control window on real system audio (always-on-top, transparency, click-through, resize, per-monitor DPI) and real Whisper→`CaptionService`→overlay wiring; real-Argos wiring when the dev Argos venv is available; then Slice 5 close-out per Definition of Done. The Q1 display policy is resolved (verbatim latest partial as the active line; committed finals as history; translated text replaces source when completed).

## Architecture Status

Approved: .NET 8 + WPF + NAudio + local Whisper behind streaming `ISpeechToTextEngine` (ADRs 0001–0005) + Argos Translate behind `ITranslationEngine` (ADR-0006, refined: contracts in Core, `UniversalCaptions.Translation` owns the engine + process seam; pair/protocol selection resolved by Slice 3 benchmark). Pipeline layers per `ARCHITECTURE.md`.

## Platform Status

Windows 10 target (build 17763+). Development environment: Windows with .NET SDK 8/10. NAudio 2.2.1 restored. Whisper.net 1.9.1 + Whisper.net.Runtime (CPU). No VB-CABLE. Whisper models cached in git-ignored `artifacts/models/` (tiny/base/small). Argos 1.11.0 in a dedicated Python 3.11 venv + `en/ja/tl` language packages under `artifacts/argos/` (git-ignored; dev machine venv created under the temp dir with the short 8.3 path to avoid Windows MAX_PATH limits during torch install).

## Current Blockers

None.

## Next Milestone

Slice 5 close-out: complete manual verification of the overlay + control window (`dotnet run --project src/UniversalCaptions.App`) and record evidence in `docs/reports/TEST_REPORT.md`; then Slice 6 — end-to-end latency/accuracy on real audio. See `docs/implementation/BUILD_PLAN.md`.

## Last Build

2026-08-01 — `dotnet build UniversalCaptions.slnx` succeeded, 0 warnings, 0 errors. `dotnet test UniversalCaptions.slnx` passed 190/190 (66 Audio + 40 Captions + 41 Speech + 21 Translation + 22 App). `dotnet format --verify-no-changes` clean. `dotnet list package --vulnerable` — no vulnerable packages (all 13 projects).
