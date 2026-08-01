# CLAUDE.md

## Project

Universal Live Captions

## Purpose

Chrome-Live-Caption-like live captions for any Windows application. Captures system audio via WASAPI loopback (no VB-CABLE), runs local streaming speech-to-text, optionally translates locally, and renders an always-on-top caption overlay. Windows 10 target (build 17763+).

## Current Sprint

Slice 5 — WPF overlay + control window (render `CaptionState`, consume `ICaptionService` events on the dispatcher) — **complete (close-out 2026-08-01)**: implementation + unit tests complete; manual overlay/device verification completed; **real-Argos wiring verified end-to-end through the App (committed overlay lines translated to Tagalog)**. Slices 1–5 are complete; Slice 4 (Caption Service) closed out 2026-08-01.

## Current Implementation Summary

- `UniversalCaptions.Core`: pure contracts — `IAudioCapture`, `IAudioBuffer`, `IAudioProcessor`, `IVoiceActivityDetector`, `AudioFormat`, `AudioChunk`, `AudioCaptureError`; `UniversalCaptions.Core.Translation` — `ITranslationEngine`, `TranslationResult`, `TranslationError`/`TranslationException`, `TranslationErrorKind`; `UniversalCaptions.Core.Captions` — `ICaptionService`, `CaptionLine`, `CaptionState`, `CaptionServiceOptions`.
- `UniversalCaptions.Audio`: `WasapiLoopbackCaptureSource` (NAudio), `LoopbackDeviceEnumerator`, `ByteToFloatConverter`, `PcmRingBuffer`, `SampleRateConverter`, `EnergyVad`, `AudioLevelMeter`.
- `UniversalCaptions.Speech`: `WhisperSpeechToTextEngine` (local Whisper.net), `StreamingTranscriptCommitter` (stability-based finals).
- `UniversalCaptions.Translation`: `ArgosTranslationEngine` (local Argos child process, line-protocol JSON over stdin/stdout), `ArgosTranslationEngineOptions`, bundled `argos_translate_server.py`.
- `UniversalCaptions.Captions`: `CaptionService` — partials replace the active line, finals commit to a bounded sequence-ordered history, optional background translation (failure preserves the source caption), cancellation, state events.
- `UniversalCaptions.App`: WPF DI composition root (Slice 5) — `IOverlayService` (visibility/position/opacity/font size/click-through), `CaptionOverlayWindow` (borderless/transparent/always-on-top; renders `CaptionState`), `CaptionPipeline` (capture → processor → STT → caption service via `Func` factories; `StatusChanged`/`LatencyUpdated`), `ControlWindow` (audio source/language, translation on/off + target, status/latency, overlay sliders, Start/Stop), `AudioSourceLoader` (device enumeration), `TranslationGuard` (source-equals-target rejection), `App.xaml.cs` (DI registration). Q1 display policy resolved: active line = verbatim latest partial; finals = bounded history; translated text replaces source only when completed.
- `UniversalCaptions.Diagnostics`: console app listing output devices and rendering a live audio meter.
- `UniversalCaptions.Benchmarks`: STT model benchmark (`stt`) + translation benchmark (`translate`).
- Tests (209 total, all passing): `UniversalCaptions.Audio.Tests` (66), `UniversalCaptions.Speech.Tests` (41), `UniversalCaptions.Translation.Tests` (21), `UniversalCaptions.Captions.Tests` (45), `UniversalCaptions.App.Tests` (36) — see `docs/reports/TEST_REPORT.md`.
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
- Slice 5 open question (change-impact Q1) is **resolved**: active caption = verbatim latest partial (`CaptionState.ActiveLine`); finals = bounded history; translated text replaces source only when completed (PRD FR-5/FR-14).
- Argos `tl`-as-source unsupported (stanza SBD) and `ja→tl` requires a pivot via `en` (~1050 ms/call); MVP pairs use `tl` as a target only (ADR-0006).
- Argos dev venv lives outside the repo (`MAX_PATH` limit on in-repo venv) — re-creatable under a short 8.3 path (TD-011; current dev venv at `C:\Users\TOGODB~1\AppData\Local\Temp\argosv`, argostranslate 1.11.0); this machine has no argostranslate on system Python, so translation defaults Off unless the venv `Scripts` dir is prepended to PATH.
- Resampler quality not yet benchmarked against speech (TD-001).
- Device-change notifications not yet wired (TD-002).

## Technical Debt

See `docs/implementation/TECHNICAL_DEBT.md`. TD-001 resampler benchmark, TD-002 device-change notifications, TD-003 DI composition, TD-004 coverage tooling, TD-005 settings persistence, TD-006 committer word-boundary edge, TD-007 immutable-final revision, TD-008 STT backpressure, TD-009 benchmark harness, TD-010 Argos `tl`-source/pivot latency, TD-011 Argos venv MAX_PATH, TD-012 Argos identical-input caching, TD-013 LineProtocolArgosProcess direct tests, TD-014 Dispose races `_gate`, TD-015 unbounded stderr.

## Next Priority

Slice 5 is **complete (close-out 2026-08-01)**: implementation + unit tests complete; Q1 display policy resolved (verbatim latest partial as the active line; finals as history; translated text replaces source only when completed); manual overlay/device verification complete and recorded in TEST_REPORT.md; **real-Argos wiring verified end-to-end through the App (committed overlay lines translated to Tagalog)**; fresh-context review completed; Entry 6 close-out record finalized. Next: Slice 6 — end-to-end latency/accuracy on real audio. See `docs/implementation/BUILD_PLAN.md`.
