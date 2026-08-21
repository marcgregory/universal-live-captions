# CLAUDE.md

## Project

Universal Live Captions

## Purpose

Chrome-Live-Caption-like live captions for any Windows application. Captures system audio via WASAPI loopback (no VB-CABLE), streams it to a Gemini Live session that produces both the transcription and the translation, and renders an always-on-top caption overlay. Windows 10 target (build 17763+). Internet + a free Gemini API key required at runtime.

## Current Sprint

**Gemini-only pipeline — IMPLEMENTED 2026-08-21 (ADR-0011; v0.5.43; release gate CLOSED — real-wire
`inputTranscription` verification PASS).** Local
Whisper (`UniversalCaptions.Speech`), Argos Translate (`UniversalCaptions.Translation`),
`UniversalCaptions.Benchmarks`, and their test projects are **removed from the solution**, along with
all bundled models/Python runtime/Argos packages and `launcher.cmd`. One Gemini Live session per
capture produces **both** source transcription (`inputAudioTranscription`) and translation
(`outputAudioTranscription`) in a single pass over TLS to `generativelanguage.googleapis.com`.
Pipeline: `CaptionPipeline.Start(deviceId, sourceLanguage, targetLanguage, translationEnabled)` — the
session runs whenever capture runs; the Translate toggle gates translation-origin caption events
without touching the session (`SetTranslationEnabled`); target-language change recycles the engine
(`SetTargetLanguage`). API key comes **only** from Windows Credential Manager
(`LiveTranslationEngineFactory.GeminiApiKeyTarget == "UniversalCaptions:GeminiApiKey"`); the legacy
`UC_GEMINI_API_KEY` env var is ignored by the production App (pinned by test). Settings schema v3
dropped the provider concept entirely. Install: ~145 MB trimmed self-contained publish (measured
2026-08-21), no Python, no
models, no env-var knobs. **Full suite 528/528** (106 Audio + 69 Captions + 174 Speech.Gemini +
179 App), Debug + Release 0 warnings / 0 errors, `dotnet format --verify-no-changes` clean.
**RELEASE GATE CLOSED (2026-08-21):** real-wire `inputTranscription` verification PASS —
`tools/GeminiDirectWireSpike --ab` against the live API: variant B (setup + top-level
`inputAudioTranscription`) received 7–8 `serverContent.inputTranscription` frames per utterance with
real English source text; variant A (field not sent) also received them, i.e. the surface streams by
default for this model. Evidence: `artifacts/spike-result/ab-result.json`, TEST_REPORT.
RISK_REGISTER R-007 transcription-surface portion resolved.

Prior closed sprints (segmentation matrix + phrase-guard validation CLOSED 2026-08-14 "do not ship";
goAway lifecycle fix v0.5.39; two-tone partials v0.5.38) are recorded in
`docs/implementation/HISTORY.md`, `docs/implementation/investigations/`, and CHANGELOG entries
v0.5.33–v0.5.40. Note: those investigations measured the pre-ADR-0011 architecture; the segmentation
findings still apply to Gemini's streaming behavior but the local-engine references in them are
historical.

## Current Implementation Summary

- `UniversalCaptions.Core`: pure contracts — `IAudioCapture`, `IAudioBuffer`, `IAudioProcessor`, `IVoiceActivityDetector`, `AudioFormat`, `AudioChunk`, `AudioCaptureError`; `UniversalCaptions.Core.Translation` — **`ILiveAudioTranslationEngine`** (additive events: `PartialTranscriptionAvailable`/`FinalTranscriptionAvailable` for source text, `PartialTranslationAvailable`/`FinalTranslationAvailable` for translated text), `ServerContent(Text, IsPartial, TurnComplete, InputText, InputIsPartial)`, `LiveTranslationError`/`TranslationErrorKind`; `UniversalCaptions.Core.Captions` — `ICaptionService`, `CaptionLine`, `CaptionState`, `CaptionServiceOptions`.
- `UniversalCaptions.Audio`: `WasapiLoopbackCaptureSource` (NAudio), `LoopbackDeviceEnumerator`, `ByteToFloatConverter`, `PcmRingBuffer`, `SampleRateConverter`, `EnergyVad`, `AudioLevelMeter`.
- `UniversalCaptions.Speech.Gemini`: **the single STT + translation engine** — `GeminiLiveTranslateEngine` (one Live session per capture; setup frame always sends top-level `"inputAudioTranscription": {}`; two independent turn accumulators for the transcription and translation surfaces; goAway + StopAsync tail-flush both; single shared `_nextSequence`; classified failures via `LiveTranslationError`), `GeminiLiveTranslateProtocol` (setup/build helpers), `GeminiServerMessage` (parse).
- `UniversalCaptions.Captions`: `CaptionService` — pure relay/state machine: source partials replace the active line, finals commit to a bounded sequence-ordered history; translation-origin lines follow the same rules gated by origin identity; toggle scrubbing (`ClearTranslationHistory` on OFF / target change), stale-session guards via injectable clock; `CaptionLineUpdated` raised on publishes/commits; E2E latency stamping on translation lines (`TranslationStartedAtUtc` = capture timestamp, `TranslationCompletedAtUtc` = apply time).
- `UniversalCaptions.App`: WPF DI composition root — `IOverlayService`, `CaptionOverlayWindow` (auto-sized translucent pill; stable word head white / unstable partial tail green; single mutable active block), `CaptionPipeline` (capture → processor → live engine → caption service via `Func` factories; `StatusChanged`/`LatencyUpdated`/`EndToEndLatencyUpdated`; TD-002 device recovery wired), `ControlWindow` (audio source/language, translation on/off + target, Gemini key panel, status/latency + E2E latency, overlay sliders, Start/Stop), `LiveTranslationEngineFactory` (Credential-Manager-only key read; never throws; returns null when key missing), `WindowsCredentialStore`/`ICredentialStore` (advapi32 `CredWriteW`, target `UniversalCaptions:GeminiApiKey`), `SpeechEngineFactory` **removed** with the local engines. **Settings persistence (schema v3):** `UserSettings`/`ISettingsStore`/`SettingsStore` persist the six user-facing categories to `%LocalAppData%\UniversalCaptions\settings.json` (atomic write, unknown fields ignored, stale provider field tolerated); engine/env knobs no longer exist.
- `UniversalCaptions.Diagnostics`: console app listing output devices and rendering a live audio meter.
- Tests (**528 total, all passing**): `UniversalCaptions.Audio.Tests` (106), `UniversalCaptions.Captions.Tests` (69), `UniversalCaptions.Speech.Gemini.Tests` (174), `UniversalCaptions.App.Tests` (179) — see `docs/reports/TEST_REPORT.md`.
- Docs: bootstrap governance + MVP docs + ADRs 0001–0011 in `docs/`.

## Architecture Summary

Native .NET 8 desktop app. Pipeline: WASAPI loopback (NAudio) → audio processing (buffer, resample, VAD) → **`ILiveAudioTranslationEngine` (single Gemini Live session: STT + translation in one pass)** → `ICaptionService` → WPF overlay. Layered projects in `src/` with dependency rules in `docs/REPOSITORY_STANDARDS.md`. Approved stack and decisions in `docs/adr/` (ADR-0011 is the governing architecture decision).

## Key Commands

```bash
dotnet build UniversalCaptions.slnx
dotnet test UniversalCaptions.slnx
dotnet run --project src/UniversalCaptions.Diagnostics
dotnet format --verify-no-changes
dotnet list UniversalCaptions.slnx package --vulnerable
pwsh packaging/build-package.ps1 -Version 0.5.43   # portable ZIP + Inno Setup installer
```

## Governance

Before making any project decision, read the relevant governance document:

- [docs/PROJECT_CONSTITUTION.md](docs/PROJECT_CONSTITUTION.md) — Immutable project rules and policies (incl. privacy rules, amended 2026-08-21 for cloud disclosure)
- [docs/ARTIFACT_REGISTRY.md](docs/ARTIFACT_REGISTRY.md) — Document ownership (every concept has one authoritative source)
- [docs/AGENT_DECISION_POLICY.md](docs/AGENT_DECISION_POLICY.md) — What agents may/must/must not decide
- [docs/REPOSITORY_STANDARDS.md](docs/REPOSITORY_STANDARDS.md) — Folder layout, naming, import rules, dependency boundaries
- [docs/CHANGE_IMPACT_PROCESS.md](docs/CHANGE_IMPACT_PROCESS.md) — Pre-implementation impact analysis and no-silent-assumptions policy

## Engineering Rules

### Product First Rule

Build user-facing value before internal polish. Every slice must produce a visible result (Slice 1: the diagnostic meter proved real capture).

### Single Sprint Rule

Only one slice/sprint may be active (Gemini-only release gate now). Do not start future slice work until the active slice meets its Definition of Done.

### Definition of Done

A feature is complete only when:

- Acceptance criteria are satisfied
- Build passes with 0 warnings (warnings-as-errors)
- Unit and integration (fake-boundary) tests pass
- Manual device verification is recorded where hardware is involved
- Privacy rules are respected (no silent capture, no persistence, cloud disclosure present)
- Code review is complete (self-review + fresh-context review for AI-generated code)
- Documentation is updated (CHANGELOG, PROJECT_STATUS, TEST_REPORT)
- Execution evidence is recorded

A feature must not be marked complete when required validation is **Pending** or **Not Tested**.

### Roadmap Discipline

`docs/implementation/ROADMAP.md` only answers "What should be built?". Keep it limited to Completed, In Progress, Sprint Queue, Future, and Blocked.

### Architecture Rules

Follow `docs/ARCHITECTURE.md` and ADRs (ADR-0011 governs the current pipeline). Never put NAudio or WPF code in `UniversalCaptions.Core`. Never bypass the `ILiveAudioTranslationEngine` abstraction or put Gemini wire details outside `UniversalCaptions.Speech.Gemini`. Do not introduce infrastructure unless the active slice requires it.

### State Management Rules

Separate capture state, caption state, and overlay state (see ARCHITECTURE.md). UI thread never blocks on the audio pipeline.

### Package Boundaries

`Core` is a pure contract layer. `Audio`/`Speech.Gemini`/`Captions` depend only on Core. `App` depends on all. `Diagnostics` depends on Core + Audio. See `docs/REPOSITORY_STANDARDS.md`.

### Documentation Discipline

Keep each document in its lane: PRD for behavior, scope for boundaries, architecture for design, build plan for execution, roadmap for backlog, changelog for history, project status for now, technical debt for cleanup, release plan for done.

### Testing Rules

`dotnet test` must pass after every change. Hardware boundaries are tested with fakes (`IWaveIn`); the network boundary is tested with fake `ILiveAudioTranslationEngine` implementations and protocol pins; real device/session verification is manual and recorded in `TEST_REPORT.md`. Never mark a check **Passed** without execution evidence.

### Code Review Rules

Every meaningful change must pass review. For AI-generated code, run a fresh-context review pass that reports findings before modifying. Evaluate correctness, privacy, error handling, tests, architecture compliance, and documentation.

### Security Rules

Follow `docs/SECURITY_PLAN.md`. Privacy is immutable policy: no silent capture, no raw audio/transcript persistence, no microphone capture; audio streams **only** to the configured Gemini endpoint while captions run and this must stay disclosed (constitution §10 as amended by ADR-0011). The API key lives only in Windows Credential Manager — never in settings.json, logs, or source. Never claim latency or Windows 10 compatibility without measurement.

### Observability Rules

MVP uses in-memory diagnostics + console output. Latency timestamps flow with each `AudioChunk` for later measurement; E2E samples flow through `EndToEndLatencyUpdated`.

### AI Engineering Rules

Follow `docs/AI_ENGINEERING_GUIDELINES.md`. Do not hallucinate APIs, invent business rules, or fake test results. Verify NAudio/.NET APIs against the installed package. Reuse before creating.

### Release Rules

Do not mark a release Ready unless release criteria, quality gates, and blocking issues are reviewed in `docs/implementation/RELEASE_PLAN.md`. The v0.5.43 blocking gate is the real-wire `inputTranscription` verification.

## Known Gaps

- **Real-wire transcription surface (RESOLVED 2026-08-21):** the setup frame's top-level `inputAudioTranscription: {}` is accepted AND `serverContent.inputTranscription.text` frames stream back — verified end-to-end via `tools/GeminiDirectWireSpike --ab` against the live API (variant B: 7–8 frames/utterance with real English source text; variant A also received them — surface streams by default). Evidence: `artifacts/spike-result/ab-result.json`, TEST_REPORT.
- **Network/quota dependence:** captions require internet + free-tier Gemini quota; outages stop captions (classified errors surface guidance). No offline mode exists (deliberate, ADR-0011).
- **Device-change notifications (TD-002):** contract + production wiring complete, **frozen/Open pending the real hotplug acceptance test** once a second device is available.
- **Phase 2 real-app validation (YouTube/Chrome, VLC, Zoom) is deferred per user.**
- **Empty delete-pending stub dirs** of the removed projects remain on this dev machine (handle-locked `obj` husks; harmless, TD-018).
- Historical ADR-0007 (Tagalog committer fallback) stays Proposed; its subject matter predates ADR-0011.

## Technical Debt

See `docs/implementation/TECHNICAL_DEBT.md`. With ADR-0011, all Argos/Whisper/engine-knob items
(TD-006–TD-017) are **obsolete** — their subject code was removed; they remain in the register as
historical record. Active: TD-002 (hotplug acceptance test, frozen/Open), TD-018 (delete-pending stub dirs).

## Next Priority

**v0.5.43 release, in order:**
1. ~~Real-wire `inputTranscription` verification~~ — **DONE 2026-08-21, PASS** (gate closed; see
   Current Sprint and `artifacts/spike-result/ab-result.json`).
2. **Real-app smoke** on the Release artifact: loopback → captions appear → translation toggle ON/OFF
   mid-session → target-language switch recycles the session → goAway recovery (status surfaces,
   toggle restarts session).
3. **Cut tag + GitHub release** with the new packaging (`packaging/build-package.ps1 -Version 0.5.43`,
   verify with `packaging/inspect-package.ps1`); landing page links already point at v0.5.43.

Remaining work beyond the gate is feature-level/product-level: Phase 2 real-app validation stays
deferred per user; TD-002 stays frozen until a second device exists.

Historical close-outs (Entry 16 `Threads=4`, Entry 15 overlay live-line, Entry 14 default promotion,
Slices 10–12, Slice 5/6, TD-001/005/016) are recorded in `docs/implementation/HISTORY.md`. The
pre-ADR-0011 production default (`fasterwhisper-native` + partials) no longer exists in the codebase;
those records are historical.
