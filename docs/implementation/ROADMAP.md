# Universal Live Captions Roadmap

Last updated: 2026-08-01

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

## In Progress

- **Slice 5 — Overlay + control window.** Always-on-top WPF caption overlay rendering `CaptionState` (active line + bounded history + translated caption) and a minimal control UI (translation on/off, source/target languages), consuming `ICaptionService` events via the dispatcher. **Implementation + unit tests complete (2026-08-01):** `UniversalCaptions.App` is the DI composition root with `IOverlayService`, `CaptionOverlayWindow`, `CaptionPipeline`, `ControlWindow`; 190/190 tests passing (22 new App tests), build 0 warnings, format clean, no vulnerable packages. Q1 display policy **resolved** (verbatim latest partial as the active line; committed finals as history; translated text replaces source when completed — PRD FR-5/FR-14). Remaining: manual overlay/device verification and real-Argos wiring before close-out.

## Sprint Queue

- **Slice 6 — End-to-end.** YouTube/Chrome and VLC/Zoom verification; latency measurement; accuracy benchmarking for Whisper model and Argos pair selection.

## Future

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
