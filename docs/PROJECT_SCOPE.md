# Universal Live Captions Project Scope

Last updated: 2026-07-31

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define project boundaries, in-scope and out-of-scope items, assumptions, and constraints |
| Scope | Product scope, feature boundaries, assumptions, and exclusions |
| Audience | Engineering, Product, Stakeholders |
| Owner | Engineering |
| Status | Active |
| Related Documents | [PRD.md](PRD.md), [ARCHITECTURE.md](ARCHITECTURE.md), [ROADMAP.md](implementation/ROADMAP.md), [RISK_REGISTER.md](RISK_REGISTER.md) |

---

## Scope Summary

Build a native Windows 10 desktop application that captures system audio through WASAPI loopback, recognizes speech locally, optionally translates the transcript locally, and renders real-time captions in an always-on-top overlay. The project is delivered in vertical slices: audio capture spike → STT spike → translation spike → caption service → overlay → end-to-end.

## In Scope

- WASAPI loopback system-audio capture (no VB-CABLE)
- PCM buffering, sample-rate conversion, voice activity detection
- Streaming speech-to-text behind an engine abstraction; first engine is local Whisper
- Local/offline translation behind a translation-engine abstraction; first engine is Argos Translate running as a local process
- Source-language auto-detection and source/target language selection for translation (pivoting through an intermediate language when needed)
- Caption service managing partial/final transcript state and history
- Always-on-top caption overlay (borderless, transparent background, draggable, resizable, opacity, font size, multi-monitor, click-through)
- Minimal control window (audio source, language, engine, translation on/off + language pair, start/stop, status, latency display, settings)
- Error handling and graceful recovery for device/engine failures
- Latency instrumentation and measurement
- Automated tests for all pipeline stages using fakes at hardware boundaries
- Windows 10 support (build 17763 / 1809+)

## Out of Scope (MVP)

- Microphone capture (not enabled by default; explicit future decision)
- Cloud speech recognition (interface-compatible, but not enabled)
- Cloud translation (interface-compatible, but not enabled in the MVP)
- Audio/transcript file recording and export
- Per-application audio source separation
- System tray / global hotkeys
- Signed installer and store distribution (packaging strategy documented; release packaging is a later milestone)
- Copy-protected and exclusive-mode audio content (OS limitation)
- Languages beyond those supported by the selected STT and translation engines

## Assumptions

| # | Assumption | Source |
|---|---|---|
| A-1 | Default render device loopback captures the audio users expect (system mix). | Product brief; loopback semantics |
| A-2 | A local Whisper model can meet the < 1 s latency target with acceptable accuracy. | Approved STT direction; model size and accuracy to be validated by benchmark in Slice 2/6 |
| A-3 | Argos Translate, isolated behind a local process, provides acceptable translation latency and quality for sentence-length captions. | Approved translation direction; latency/quality to be validated by benchmark in Slice 3/6 |
| A-4 | Windows 10 build 17763+ has all required APIs (WASAPI loopback via NAudio, .NET 8). | .NET 8 support matrix; NAudio Windows 10 support |
| A-5 | The development machine is the primary test environment for hardware-dependent verification. | Repo environment |

## Constraints

- Target OS: Windows 10, build 17763 (1809) or later; Windows 11 also supported
- No VB-CABLE requirement for the MVP
- No dependency on Chrome or Chrome Live Caption
- Local/private processing preferred; no raw audio persistence by default
- Use only approved stack in [TECH_STACK.md](TECH_STACK.md)
- Follow repository governance ([PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md), [REPOSITORY_STANDARDS.md](REPOSITORY_STANDARDS.md))

## Dependencies

- .NET 8 SDK / runtime on target machines
- NAudio 2.2.1 (WASAPI loopback)
- Local Whisper model binaries (downloaded at development/runtime per Slice 2; git-ignored under `artifacts/models/`)
- Argos Translate runtime + language model packages (installed per Slice 3; git-ignored under `artifacts/models/`)
- xUnit test framework

## Stakeholders

- End users (deaf/hard-of-hearing, language learners, gamers) — see PRD personas
- Engineering (owner of delivery)

## Success Criteria

- MVP Definition of Done in [PRD.md](PRD.md) is satisfied
- Slice 1 criterion met: the application detects and receives Windows system audio without VB-CABLE
- Translation latency/quality benchmark recorded in the translation slice
- Latency measured (not assumed) in Slice 6
- All automated tests pass; build clean
- Bootstrap validation passes

## Risks

See [RISK_REGISTER.md](RISK_REGISTER.md).
