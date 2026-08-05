# Universal Live Captions Project Status

Last updated: 2026-08-05

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

**Slices 1–6 complete (close-out 2026-08-01).** Slice 5 (WPF overlay + control window) + Entry 7 (live active-line translation + Chrome-style overlay) closed out 2026-08-01; Slice 6 (E2E latency + OFAT baseline) closed out 2026-08-01. **Argos pre-warm closed out 2026-08-02** (v0.5.9): first-caption latency ~23–30 s → ~3.8–6.85 s. **Slice 7 — caption overlay layout & stable incremental rendering (in progress, 2026-08-02)**: full-viewport width verified via a layout probe; the render path now mutates only the live block on a Partial and reuses history blocks by identity, with bottom scroll/re-anchor limited to when a new block is inserted and content overflows. **All MVP slices (0–6) complete; Phase 2 real-app validation deferred per user.**

**Post-close-out refinement (2026-08-01):** live **active-line translation** + **Chrome-style overlay redesign** landed on top of Slice 5 (change-impact Entry 7): the in-progress caption line is now translated in the target language while the speaker is still talking (single in-flight slot, instance-identity stale-guard, disabled-mid-flight results discarded); the overlay is an auto-sized translucent pill with white text, a target-language badge, expand/collapse chevron, and a hide button; the control window adds "Show Captions". Implementation + unit tests **complete (224/224)**; **manual verification with real audio + real Argos completed 2026-08-01** — Tagalog appears on the in-progress overlay line before commit, `TL` badge, chevron expand/collapse, close-hide, "Show Captions" re-show, and pipeline-continues-while-hidden all verified (evidence in `TEST_REPORT.md`). **Entry 7 closed out 2026-08-01.**

## Current Progress

Slice 1 (Audio Capture Spike), Slice 2 (STT Spike), Slice 3 (Translation Spike), and **Slice 4 (Caption Service) are complete and verified.** Slice 4 close-out approved 2026-08-01: `ICaptionService`, `CaptionLine`, `CaptionState`, and `CaptionServiceOptions` in `UniversalCaptions.Core.Captions` (contracts in Core so `UniversalCaptions.Captions` depends only on Core, per the ADR-0003/0006 precedent); `src/UniversalCaptions.Captions` implements `CaptionService`: partials replace the active line, finals commit to a bounded sequence-ordered history, optional background translation (failure preserves the source caption; stale results matched by line identity are dropped), cancellation of in-flight translations on stop/reset/dispose, and events raised outside the serialization gate with snapshot `History`. Verified with deterministic `StubTranslationEngine`/`GatedTranslationEngine` fakes — 40 tests, build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, no vulnerable packages. Fresh-context review completed; findings fixed (snapshot history reads, deferred CTS disposal, atomic translation-start token, stale-translation identity guard, event-raising moved out of the translation catch, target normalization).

**Slice 5 (WPF overlay + control window) is complete (close-out 2026-08-01).** `src/UniversalCaptions.App` (new WPF project, `net8.0-windows`, `UseWPF`, PerMonitorV2 manifest) is the DI composition root: `IOverlayService` (visibility/position/opacity/font size/click-through), `CaptionOverlayWindow` (borderless/transparent/always-on-top, history + active line, drag/resize, click-through via `WS_EX_TRANSPARENT`), `CaptionPipeline` (wiring capture → processor → STT → `CaptionService` via `Func` factories, idempotent Start/Stop/Dispose, `StatusChanged`/`LatencyUpdated` events, error handling, teardown ordering), `ControlWindow` (audio source/language, translation on/off + target, status/latency, overlay sliders, Start/Stop), `AudioSourceLoader` (device enumeration with preferred default), `TranslationGuard` (source-equals-target rejection), and `App.xaml.cs` (DI registration + `ShutdownMode.OnMainWindowClose`). The deferred Q1 display policy is resolved: the active caption renders verbatim from the latest partial (`CaptionState.ActiveLine`); committed finals render as bounded history; translated text replaces the source on a committed line only when translation completes (PRD FR-5/FR-14). Verified with `UniversalCaptions.App.Tests` — `CaptionDisplayPolicyTests` (8) + `CaptionPipelineTests` (20) + `AudioSourceLoaderTests` (4) + `TranslationGuardTests` (4). **Manual overlay/device verification completed 2026-08-01** on this Windows 10 machine: real system audio → Whisper `ggml-base` → live overlay captions; always-on-top/transparency; drag/resize/click-through; stop/restart; rapid Stop→close (clean ~2 s exit); model-not-found and source-equals-target error paths (evidence in `TEST_REPORT.md` Slice 5). **Real-Argos wiring verified end-to-end through the App 2026-08-01**: with the dev Argos venv recreated (`argostranslate==1.11.0` + en→tl/tl→en/ja→en/en→ja under a short 8.3 temp path per TD-011), the App spawned the Argos child process and committed overlay lines displayed real translated Tagalog (`tamad aso.`, `Ang mabilis na kayumangging sorra ay tumatalon sa ibabaw ng eruplano`) with `IsTranslated = True`; this also exercised the `ControlWindow.ApplyTranslationSettings` guard fix (toggle stays ON + target combo enabled on a guard error so a valid target can be selected). Total test count: **209/209 passing** (66 Audio + 45 Captions + 41 Speech + 21 Translation + 36 App). All Slice 5 Definition-of-Done items are satisfied.

The `ITranslationEngine` contract (with `TranslationResult`, `TranslationErrorKind`, `TranslationException`) lives in `UniversalCaptions.Core.Translation`; it is verified with a deterministic `FakeTranslationEngine` (8 tests); `ArgosTranslationEngine` (child Python process over a newline-delimited JSON line protocol, bundled `argos_translate_server.py`) is verified with a fake process seam (13 tests, incl. restart-after-fatal-error) and against real Argos 1.11.0 end-to-end (direct pairs `en→tl`, `ja→en`, `en→ja`; pivoting `ja→tl` via `en`). The translation benchmark is recorded (load/first latency, steady-state distinct-text latency, identical-input cache, throughput, Argos working set, finals-stream ordering, char-similarity quality). Fresh-context review completed; findings fixed (stale-process recovery, unwrapped exceptions, Python crash path, options validation, `ArgumentList`, UTF-8 pinning) with remaining items in TD-013–TD-015.

## Slice 6 Baseline Defaults (validated 2026-08-01)

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

**All MVP slices (0–6) remain complete.** **Argos pre-warm landed 2026-08-02** (v0.5.9): background pre-warm warms one shared Argos process/model off the real-caption path, so the first caption drops from ~23–30 s cold start to ~3.8–6.85 s (warm translation ~0.46 s), verified live through the real App (Cases A + B: single process spawn + single model init, no duplicate init, no lost first caption; 260/260 tests). **Slice 7 — caption overlay layout & stable incremental rendering (2026-08-02)**: a layout probe confirmed the caption `TextBlock` already uses the full ~522 px viewport width correctly (short lines stay one line; long text wraps only on width exhaustion — the reported "whole text re-flows" is not a width bug), and the render path now does scope-stable incremental rendering (a Partial only rewrites the live block's text in place; history blocks reused by identity, never rebuilt) with bottom scroll re-anchoring limited to when a new block is inserted and content overflows.

**ADR-0007 Option B — boundary-preserving fallback (2026-08-04, in progress toward acceptance):** the streaming commit path was the last quality gap (premature `At gusto ko` / `Kaya` / `country can do for` fragments). Implemented + unit-tested (**284/284**) and validated live against **JFK (controlled English verification, PASS)** — single + continuous runs through the real App no longer emit the pre-fix `country can do for` interior fragment and Stop drain preserves finals. **The original Tagalog recording scenario (`At gusto ko` / `Kaya` / `artipisyal na katalinuhan`) is the remaining acceptance evidence and is Pending** — the original operator recording is not available in the workspace; per user, no substitute Tagalog sample may be used to claim acceptance. Implementation frozen; ADR-0007 stays `Proposed` until that live evidence exists. **Phase 2 real-app validation (YouTube/Chrome, VLC, Zoom) stays deferred per user.**

**Slice 8 — Tagalog STT-vs-Committer isolation + model selection (2026-08-04, recorded):** the reported Tagalog defects were isolated to the STT layer, not the committer. RAW Whisper segments already contain the misrecognitions (`Kung usta?`, `Ikao.`, `Salaman.`), hallucinated `1.` segments, and short fragment boundaries; the committer aggregates them faithfully. Real-App comparison across all three local models on the same Tagalog slice (STT `tl`, frozen config): **base ~3.1 s** STT latency but weak accuracy; **tiny ~1.75 s** fastest but no accuracy gain and worst fragmentation; **small** best Tagalog accuracy (`Kumusta`/`Ikaw`/`Salamat`/`Juan` all correct, no `1.` hallucination) but **16.9–21.9 s** latency — cannot keep real-time pace. **No available whisper.cpp local model gives both Tagalog quality and responsiveness.** ADR-0007 is NOT implicated (this is model-selection, not commit/boundary behavior). Evidence: `artifacts/samples/raw_vs_committed_tagalog.log` + `realapp_{tiny,base,small}_tagalog.log`; findings in `BENCHMARK_REPORT.md` (Slice 8) + `TEST_REPORT.md`.

**Faster-whisper selectable STT engine (2026-08-04, recorded):** the Slice 8 gap was closed by adding a **parallel faster-whisper STT path** (`UC_STT_ENGINE=fasterwhisper`) behind the same streaming engine boundary — the whisper.cpp decode portion was extracted to the `ISTTDecoder` seam with **zero behavior change** to the default `ggml-base` path (293/293 tests). A persistent binary-framed Python worker (`Server/faster_whisper_worker.py`, model loaded once, `small` int8, 8 threads, beam 5, `condition_on_previous_text=False`) drives `FasterWhisperDecoder`. **Real-App validation (same 90 s Tagalog slice, STT `tl`, frozen config st2/8 s/0.5 s/0.5 s) confirmed the target: whisper-small-level Tagalog accuracy with no `1.`/`one` hallucination at lower latency than whisper.cpp small** — STT latency 10.7–11.7 s (vs small 16.9–21.9 s), first final 16.5–29.9 s, 3–4 clean bilingual finals. A 1.5 s-interval variant gave the cleanest complete sentences (first final 16.5 s ≈ base 17.5 s). Two wire-protocol bugs (`0x46574355` magic endianness; 16→20-byte segment header) were found and fixed during the real-App run (unit-test fakes did not exercise the wire format). Evidence: `artifacts/samples/realapp_fasterwhisper_small_tagalog.log` (+ `_int1_5_` variant); findings in `BENCHMARK_REPORT.md` + `TEST_REPORT.md`.

**Faster-whisper default-selection decision-gate — CLOSED: NOT promoted (2026-08-04).** The promotion candidate was measured against the frozen default on startup + steady-state latency. Worker cold start: spawn 0.006 s + Python import/model load **2.6 s** + first 8 s-window decode 2.5 s. Real-App (same 90 s Tagalog slice, STT `tl`): faster-whisper `small` first caption **16.5–17.4 s** (better than ggml-base's measured 25.0 s) but steady-state STT latency **13.7–15.8 s** vs ggml-base **2.4–3.7 s**. Window/interval tuning does not close the steady-state gap (1.0 s interval ≈ no change; 1.5 s worse at 24.2 s; 4 s window produced no captions) — the frozen 8 s/0.5 s config is already near-optimal for the faster-whisper path. Pre-warm would save only ~2.6 s. **Decision per user: `ggml-base` stays the production default; faster-whisper `small` int8 remains opt-in (`UC_STT_ENGINE=fasterwhisper`) until its steady-state latency can be materially reduced.** Accuracy winner: faster-whisper; responsiveness winner: ggml-base. Clean close: no production change, no forced promotion; the Tagalog accuracy gap on the ggml-base default remains acknowledged as open. Evidence: `artifacts/samples/firstcaption_{fw_small,i1_fw_small,base,w4_fw_small}.log`; findings in `BENCHMARK_REPORT.md` (Slice 9 decision-gate) + `TEST_REPORT.md`.

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

Approved: .NET 8 + WPF + NAudio + local Whisper behind streaming `ISpeechToTextEngine` (ADRs 0001–0005) + Argos Translate behind `ITranslationEngine` (ADR-0006, refined: contracts in Core, `UniversalCaptions.Translation` owns the engine + process seam; pair/protocol selection resolved by Slice 3 benchmark). Pipeline layers per `ARCHITECTURE.md`.

## Platform Status

Windows 10 target (build 17763+). Development environment: Windows with .NET SDK 8/10. NAudio 2.2.1 restored. Whisper.net 1.9.1 + Whisper.net.Runtime (CPU). No VB-CABLE. Whisper models cached in git-ignored `artifacts/models/` (tiny/base/small). Argos 1.11.0 in a dedicated Python 3.11 venv + `en/ja/tl` language packages under `artifacts/argos/` (git-ignored; dev machine venv created under the temp dir with the short 8.3 path to avoid Windows MAX_PATH limits during torch install).

## Current Blockers

**Original Tagalog recording for ADR-0007 acceptance** — the live evidence for the `"At gusto ko"` / `"Kaya"` / `"artipisyal na katalinuhan"` regression requires the original operator recording, which is unavailable in the workspace; per user, no substitute sample qualifies. ADR-0007 remains `Proposed` until it is supplied and validated through the real App (fragmentation, duplicates, missing words, Stop drain).

## Next Milestone

**Slice 6 is complete (close-out 2026-08-01)** (E2E metric, OFAT sweep + shortlist in `BENCHMARK_REPORT.md`, App-level SAPI E2E validation; baseline `base/8/1/st2` promoted to the App default — `StabilityWindow` 3→2, model `ggml-base` unchanged). **All MVP slices (0–6) are complete.** **Argos pre-warm closed out 2026-08-02** (v0.5.9) — first-caption latency ~23–30 s → ~3.8–6.85 s, verified live. **Slice 7 (caption overlay layout & stable incremental rendering) closed out 2026-08-02** — tests 267/267 (see CHANGELOG v0.5.10). **ADR-0007 Option B implemented + unit-tested (284/284) + live JFK verification passed (2026-08-04); final acceptance gated on the original Tagalog recording (Pending).** Next work after acceptance is from the roadmap Future list and the deferred Phase 2 real-app validation (YouTube/VLC/Zoom) reassessment per user. See `docs/implementation/BUILD_PLAN.md` and `docs/implementation/ROADMAP.md`.

## Last Build

2026-08-05 — `dotnet build UniversalCaptions.slnx` succeeded, 0 warnings, 0 errors. `dotnet test UniversalCaptions.slnx` passed **335/335** (77 Audio + 72 Captions + 77 Speech + 27 Translation + 82 App). **TD-005 closed (2026-08-05):** settings persistence (`UserSettings`/`ISettingsStore`/`SettingsStore`) at `%LocalAppData%\UniversalCaptions\settings.json`; 6 new `SettingsStoreTests`; `App.xaml.cs` loads settings before window construction; ControlWindow/Overlay apply on load + save on change; engine/env knobs not persisted. See CHANGELOG v0.5.16, TEST_REPORT (TD-005), CHANGE_IMPACT_ANALYSIS Entry 10. **TD-002 stays Open** pending the real hotplug acceptance test (contract + production wiring complete 2026-08-05, 329/329 before TD-005). ADR-0007 stays `Proposed` pending the original Tagalog recording; `ggml-base` stays the frozen default; faster-whisper stays opt-in.
