# CLAUDE.md

## Project

Universal Live Captions

## Purpose

Chrome-Live-Caption-like live captions for any Windows application. Captures system audio via WASAPI loopback (no VB-CABLE), runs local streaming speech-to-text, optionally translates locally, and renders an always-on-top caption overlay. Windows 10 target (build 17763+).

## Current Sprint

Slice 5 — WPF overlay + control window (render `CaptionState`, consume `ICaptionService` events on the dispatcher) — **complete (close-out 2026-08-01)**: implementation + unit tests complete; manual overlay/device verification completed; **real-Argos wiring verified end-to-end through the App (committed overlay lines translated to Tagalog)**. Post-close-out refinement Entry 7 (live active-line translation + Chrome-style overlay) is **implemented and closed out 2026-08-01** (224/224 tests; manual verification with real audio + real Argos complete — the in-progress overlay line reads Tagalog before commit). Slices 1–5 are complete; Slice 4 (Caption Service) closed out 2026-08-01. **Slice 6 (end-to-end latency/accuracy, Entry 8) is complete (close-out 2026-08-01)**: Phase 1a (E2E latency metric + tests, 238/238), Phase 1b (OFAT sweep + shortlist), and Phase 1c (App-level SAPI E2E validation) are all done; **the validated baseline `base/8/1/st2` was promoted to the App default (`StabilityWindow` 3→2, model `ggml-base` unchanged)**. **All MVP slices (0–6) are complete. Phase 2 (real-app validation) is deferred per user as a future reassessment pass.**

## Current Implementation Summary

- `UniversalCaptions.Core`: pure contracts — `IAudioCapture`, `IAudioBuffer`, `IAudioProcessor`, `IVoiceActivityDetector`, `AudioFormat`, `AudioChunk`, `AudioCaptureError`; `UniversalCaptions.Core.Translation` — `ITranslationEngine`, `TranslationResult`, `TranslationError`/`TranslationException`, `TranslationErrorKind`; `UniversalCaptions.Core.Captions` — `ICaptionService`, `CaptionLine`, `CaptionState`, `CaptionServiceOptions`.
- `UniversalCaptions.Audio`: `WasapiLoopbackCaptureSource` (NAudio), `LoopbackDeviceEnumerator`, `ByteToFloatConverter`, `PcmRingBuffer`, `SampleRateConverter`, `EnergyVad`, `AudioLevelMeter`.
- `UniversalCaptions.Speech`: `WhisperSpeechToTextEngine` (local Whisper.net) with the decode portion extracted to the `ISTTDecoder` seam — `WhisperCppDecoder` (default, frozen `ggml-base`) and `FasterWhisperDecoder` (opt-in `UC_STT_ENGINE=fasterwhisper`; persistent binary-framed Python worker `Server/faster_whisper_worker.py`, model loaded once, `small` int8); `StreamingTranscriptCommitter` (stability-based finals).
- `UniversalCaptions.Translation`: `ArgosTranslationEngine` (local Argos child process, line-protocol JSON over stdin/stdout), `ArgosTranslationEngineOptions`, bundled `argos_translate_server.py`.
- `UniversalCaptions.Captions`: `CaptionService` — partials replace the active line, finals commit to a bounded sequence-ordered history, optional background translation (failure preserves the source caption), cancellation, state events. **Live active-line translation**: the in-progress line is translated in the target language as the speaker is still talking via a single in-flight slot (Argos cannot be cancelled per partial — the slot serializes and self-replenishes to a newer partial); results are stale-guarded by line-instance identity (`CaptionState.ReplaceActiveLine`) and discarded when the line was superseded/committed or translation was disabled mid-flight. **E2E latency (Slice 6 Phase 1a)**: `CaptionLine.TranslationStartedAtUtc`/`TranslationCompletedAtUtc` stamped by an injectable clock (`utcNow`) — completion only when a result is actually applied (stale/disabled results stamp nothing); `CaptionPipeline.EndToEndLatencyUpdated` emits `EndToEndLatencySample` (Partial/Final) on every published translated caption (E2E = `CapturedAtUtc` → published; translation = request start → published); `LatencyUpdated` (STT-final) unchanged.
- `UniversalCaptions.App`: WPF DI composition root (Slice 5) — `IOverlayService` (visibility/position/opacity/font size/click-through), `CaptionOverlayWindow` (auto-sized translucent pill: white text, target-language badge, expand/collapse chevron for history, hide button; renders `CaptionState`), `CaptionPipeline` (capture → processor → STT → caption service via `Func` factories; `StatusChanged`/`LatencyUpdated`/`EndToEndLatencyUpdated`), `ControlWindow` (audio source/language, translation on/off + target, status/latency + E2E latency, overlay sliders, Start/Stop, "Show Captions" — Start also re-shows the overlay), `AudioSourceLoader` (device enumeration), `TranslationGuard` (source-equals-target rejection), `App.xaml.cs` (DI registration; STT knobs overridable via `UC_STT_WINDOW`/`UC_STT_INTERVAL`/`UC_STT_STABILITY`, built-in default = the validated Slice 6 baseline 8 s / 1 s / StabilityWindow 2, model `ggml-base`; optional faster-whisper path via `UC_STT_ENGINE=fasterwhisper` + `UC_FW_PYTHON`, default stays `ggml-base`). Q1 display policy: active line = latest partial, **live-translated** into the target language once its translation completes; finals = bounded history; translated text replaces source only when completed.
- `UniversalCaptions.Diagnostics`: console app listing output devices and rendering a live audio meter.
- `UniversalCaptions.Benchmarks`: STT model benchmark (`stt`) + translation benchmark (`translate`). STT mode is parameterized (Slice 6 Phase 1b): `--window`/`--interval`/`--stability`/`--feed realtime|fast`/`--sample <substr>`/`--csv <path>`; records full-file WER, streamed-finals WER (commit-rate proxy, not accuracy), first-partial/final latency, decode/stream CPU, RAM. OFAT sweep + shortlist in `docs/reports/BENCHMARK_REPORT.md` (Slice 6 section). Slice 9 records the faster-whisper worker round-trip characterization + the **decision-gate measurements** (startup decomposition, real-App first-caption/steady-state latency table, window/interval tuning) in the same file.
- Tests (302 total, all passing): `UniversalCaptions.Audio.Tests` (66), `UniversalCaptions.Captions.Tests` (72), `UniversalCaptions.Speech.Tests` (77), `UniversalCaptions.Translation.Tests` (27), `UniversalCaptions.App.Tests` (60) — see `docs/reports/TEST_REPORT.md`.
- Docs: bootstrap governance + MVP docs + ADRs 0001–0006 in `docs/`.

## Architecture Summary

Native .NET 8 desktop app. Pipeline: WASAPI loopback (NAudio) → audio processing (buffer, resample, VAD) → streaming `ISpeechToTextEngine` (local Whisper, Slice 2) → optional `ITranslationEngine` (local Argos process, Slice 3) → `ICaptionService` (Slice 4) → WPF overlay (Slice 5). Layered projects in `src/` with dependency rules in `docs/REPOSITORY_STANDARDS.md`. Approved stack and decisions in `docs/adr/`.

## Key Commands

```bash
dotnet build UniversalCaptions.slnx
dotnet test UniversalCaptions.slnx
dotnet run --project src/UniversalCaptions.Diagnostics
dotnet format --verify-no-changes
dotnet list UniversalCaptions.slnx package --vulnerable
```

## Governance

Before making any project decision, read the relevant governance document:

- [docs/PROJECT_CONSTITUTION.md](docs/PROJECT_CONSTITUTION.md) — Immutable project rules and policies (incl. privacy rules)
- [docs/ARTIFACT_REGISTRY.md](docs/ARTIFACT_REGISTRY.md) — Document ownership (every concept has one authoritative source)
- [docs/AGENT_DECISION_POLICY.md](docs/AGENT_DECISION_POLICY.md) — What agents may/must/must not decide
- [docs/REPOSITORY_STANDARDS.md](docs/REPOSITORY_STANDARDS.md) — Folder layout, naming, import rules, dependency boundaries
- [docs/CHANGE_IMPACT_PROCESS.md](docs/CHANGE_IMPACT_PROCESS.md) — Pre-implementation impact analysis and no-silent-assumptions policy

## Engineering Rules

### Product First Rule

Build user-facing value before internal polish. Every slice must produce a visible result (Slice 1: the diagnostic meter proved real capture).

### Single Sprint Rule

Only one slice/sprint may be active (Slice 5 now). Do not start future slice work until the active slice meets its Definition of Done.

### Definition of Done

A feature is complete only when:

- Acceptance criteria are satisfied
- Build passes with 0 warnings (warnings-as-errors)
- Unit and integration (fake-boundary) tests pass
- Manual device verification is recorded where hardware is involved
- Privacy rules are respected (no silent capture, no persistence)
- Code review is complete (self-review + fresh-context review for AI-generated code)
- Documentation is updated (CHANGELOG, PROJECT_STATUS, TEST_REPORT)
- Execution evidence is recorded

A feature must not be marked complete when required validation is **Pending** or **Not Tested**.

### Roadmap Discipline

`docs/implementation/ROADMAP.md` only answers "What should be built?". Keep it limited to Completed, In Progress, Sprint Queue, Future, and Blocked.

### Architecture Rules

Follow `docs/ARCHITECTURE.md` and ADRs 0001–0006. Never put NAudio or WPF code in `UniversalCaptions.Core`. Never bypass the STT or translation abstraction. Do not introduce infrastructure unless the active slice requires it.

### State Management Rules

Separate capture state, caption state, and overlay state (see ARCHITECTURE.md). UI thread never blocks on the audio pipeline.

### Package Boundaries

`Core` is a pure contract layer. `Audio`/`Speech`/`Translation`/`Captions` depend only on Core. `App` depends on all. `Diagnostics` depends on Core + Audio. See `docs/REPOSITORY_STANDARDS.md`.

### Documentation Discipline

Keep each document in its lane: PRD for behavior, scope for boundaries, architecture for design, build plan for execution, roadmap for backlog, changelog for history, project status for now, technical debt for cleanup, release plan for done.

### Testing Rules

`dotnet test` must pass after every change. Hardware boundaries are tested with fakes (`IWaveIn`); real device/model verification is manual and recorded in `TEST_REPORT.md`. Never mark a check **Passed** without execution evidence.

### Code Review Rules

Every meaningful change must pass review. For AI-generated code, run a fresh-context review pass that reports findings before modifying. Evaluate correctness, privacy, error handling, tests, architecture compliance, and documentation.

### Security Rules

Follow `docs/SECURITY_PLAN.md`. Privacy is immutable policy: no silent capture, no raw audio persistence, no microphone capture, local-first STT and translation. Never claim latency or Windows 10 compatibility without measurement.

### Observability Rules

MVP uses in-memory diagnostics + console output. Latency timestamps flow with each `AudioChunk` for later measurement.

### AI Engineering Rules

Follow `docs/AI_ENGINEERING_GUIDELINES.md`. Do not hallucinate APIs, invent business rules, or fake test results. Verify NAudio/.NET APIs against the installed package. Reuse before creating.

### Release Rules

Do not mark a release Ready unless release criteria, quality gates, and blocking issues are reviewed in `docs/implementation/RELEASE_PLAN.md`.

## Known Gaps

- Slice 5 (WPF overlay + control window) is **complete (close-out 2026-08-01)**: unit tests complete, manual overlay/device verification completed, and **real-Argos wiring verified end-to-end through the App (committed overlay lines translated to Tagalog)**.
- Slice 5 open question (change-impact Q1) is **resolved and refined (Entry 7)**: active caption = latest partial, **live-translated** into the target language while the speaker is still talking (`CaptionState.ActiveLine`); finals = bounded history; translated text replaces source only when completed (PRD FR-5/FR-14).
- Post-close-out refinement (Entry 7, live active-line translation + Chrome-style overlay) is implemented with **224/224 tests passing** and its **manual verification with real audio + Argos is complete (close-out 2026-08-01)** — auto-size pill, chevron, hide button, `TL` badge, and live-translated active line all verified; the in-progress overlay line reads Tagalog before commit.
- **Slice 6 (Entry 8) is complete (close-out 2026-08-01)**: E2E latency metric (caption translation timestamps + `EndToEndLatencyUpdated`, 238/238 tests), the OFAT sweep (window/interval/stability × base/tiny × jfk/OSR) with **shortlist base 8 s/1 s/st2, tiny 8 s/1 s/st2, base 8 s/1 s/st3**, and the App-level SAPI E2E validation (baseline + shortlist × 3 runs each through the real App, every run publishing real translated Tagalog) — findings in `docs/reports/BENCHMARK_REPORT.md`, evidence in `docs/reports/TEST_REPORT.md`. Streamed-finals WER is a commit-rate proxy, not accuracy (trailing audio stays partial by design; committer occasionally re-emits overlapping text across epochs — TD-006/007). **The validated Slice 6 baseline `base/8/1/st2` was promoted to the App default: `StabilityWindow` 3→2 (`WhisperEngineOptions` + App + benchmark, one authoritative config); model default `ggml-base` unchanged.** Latency winner `tiny/8/1/st2` (E2E final median 16.25 s incl. Argos cold start, warm last-final 7.45 s, STT 3.61 s, 18 translated finals). Fresh-context review of the Phase 1a metric code completed clean. **Phase 2 real-app validation (YouTube/Chrome, VLC, Zoom) is deferred per user** — a future reassessment pass over the baseline defaults.
- Argos `tl`-as-source unsupported (stanza SBD) and `ja→tl` requires a pivot via `en` (~1050 ms/call); MVP pairs use `tl` as a target only (ADR-0006).
- **Faster-whisper selectable engine (Slices 8–9, 2026-08-04):** a faster-whisper `ISpeechToTextEngine` (`UC_STT_ENGINE=fasterwhisper`, persistent Python worker, `small` int8) validates through the real App with clean bilingual Tagalog + no `1.` hallucination at 10.7–11.7 s STT. **Decision-gate closed: NOT promoted** — `ggml-base` stays the production default because faster-whisper's steady-state STT latency (13.7–15.8 s vs ggml-base 2.4–3.7 s) is a live-caption responsiveness regression; first caption 16.5 s vs 25.0 s and pre-warm ~2.6 s don't compensate; window/interval tuning is already near-optimal and doesn't close the gap. **faster-whisper stays opt-in until its steady-state latency can be materially reduced. Tagalog accuracy gap on the `ggml-base` default remains acknowledged as open.**
- Argos dev venv lives outside the repo (`MAX_PATH` limit on in-repo venv) — re-creatable under a short 8.3 path (TD-011; current dev venv at `C:\Users\TOGODB~1\AppData\Local\Temp\argosv`, argostranslate 1.11.0); this machine has no argostranslate on system Python, so translation defaults Off unless the venv `Scripts` dir is prepended to PATH.
- Resampler quality not yet benchmarked against speech (TD-001).
- Device-change notifications not yet wired (TD-002).

## Technical Debt

See `docs/implementation/TECHNICAL_DEBT.md`. TD-001 resampler benchmark, TD-002 device-change notifications, TD-003 DI composition, TD-004 coverage tooling, TD-005 settings persistence, TD-006 committer word-boundary edge, TD-007 immutable-final revision, TD-008 STT backpressure, TD-009 benchmark harness, TD-010 Argos `tl`-source/pivot latency, TD-011 Argos venv MAX_PATH, TD-012 Argos identical-input caching, TD-013 LineProtocolArgosProcess direct tests, TD-014 Dispose races `_gate`, TD-015 unbounded stderr, **TD-016 closed (2026-08-04) — faster-whisper protocol-contract suite** (`LineProtocolFasterWhisperProcessProtocolTests`, 9 tests, injectable-stream seam; full suite 302/302).

## Next Priority

Slice 5 + Entry 7 are **complete (close-out 2026-08-01)**: manual overlay/device verification + real-Argos E2E wiring + live active-line translation verified end-to-end through the App (evidence in TEST_REPORT.md). **Slice 6 (Entry 8) is complete (close-out 2026-08-01)**: Phase 1a (E2E latency metric + tests), Phase 1b (OFAT sweep + shortlist), and Phase 1c (App-level SAPI E2E validation) — 238/238 tests, findings + shortlist in `BENCHMARK_REPORT.md`, Phase 1c evidence (baseline + shortlist × 3 runs through the real App, every run publishing real translated Tagalog) in `TEST_REPORT.md`. **The validated Slice 6 baseline `base/8/1/st2` was promoted to the App default: `StabilityWindow` 3→2 (`WhisperEngineOptions` + App + benchmark, one authoritative config); model default `ggml-base` unchanged.** **Slice 8 (Tagalog model-selection isolation) and Slice 9 (faster-whisper selectable engine) are complete (2026-08-04):** faster-whisper `small` int8 validated end-to-end through the real App (clean bilingual Tagalog, no `1.` hallucination, STT latency 10.7–11.7 s); **decision-gate closed: NOT promoted** — `ggml-base` stays the production default (faster-whisper steady-state STT latency 13.7–15.8 s vs ggml-base 2.4–3.7 s is a live-caption responsiveness regression; first caption 16.5 s vs 25.0 s and pre-warm ~2.6 s don't compensate). faster-whisper stays opt-in (`UC_STT_ENGINE=fasterwhisper`) until steady-state latency is materially reduced. 293/293 tests; evidence + findings in `BENCHMARK_REPORT.md` (Slices 8–9) + `TEST_REPORT.md`. **All MVP slices (0–6) are complete.** Phase 2 real-app validation (YouTube/Chrome, VLC, Zoom) is **deferred per user** — a future reassessment pass over the baseline defaults. **Technical-debt sprint (2026-08-04) started per user; TD-016 (faster-whisper protocol-contract suite) closed — full suite 302/302.** Remaining TD order per user: TD-001 (resampler benchmark) → TD-002 (device-change notifications) → TD-005 (settings persistence) → Phase 2. ADR-0007 stays Proposed/blocked until the original Tagalog recording is available. See `docs/implementation/BUILD_PLAN.md` and `docs/implementation/ROADMAP.md`.
