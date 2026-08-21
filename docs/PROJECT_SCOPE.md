# Universal Live Captions Project Scope

Last updated: 2026-08-21

> **Amendment (2026-08-21, ADR-0011):** speech recognition and translation now run in a single
> Gemini Live session. Local Whisper and Argos Translate are removed; an internet connection and a
> free Gemini API key are required. Sections below reflect this direction.

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

Build a native Windows 10 desktop application that captures system audio through WASAPI loopback, streams it to a Gemini Live session for speech recognition and translation, and renders real-time captions in an always-on-top overlay. The project was delivered in vertical slices: audio capture spike → STT spike → translation spike → caption service → overlay → end-to-end → Gemini-only pipeline (ADR-0011).

## In Scope

- WASAPI loopback system-audio capture (no VB-CABLE)
- PCM buffering, sample-rate conversion, voice activity detection
- Streaming speech-to-text + translation in a single Gemini Live session behind the `ILiveAudioTranslationEngine` abstraction
- Source/target language selection for translation
- Caption service managing partial/final transcript state and history
- Always-on-top caption overlay (borderless, transparent background, draggable, resizable, opacity, font size, multi-monitor, click-through)
- Minimal control window (audio source, language, translation on/off + target language, Gemini key panel, start/stop, status, latency display, settings)
- Error handling and graceful recovery for device/session failures (classified Gemini errors)
- Latency instrumentation and measurement
- Automated tests for all pipeline stages using fakes at hardware/network boundaries
- Windows 10 support (build 17763 / 1809+)

## Out of Scope (MVP)

- Microphone capture (not enabled by default; explicit future decision)
- Offline/local speech recognition or translation (removed by ADR-0011)
- Audio/transcript file recording and export
- Per-application audio source separation
- System tray / global hotkeys
- Signed installer and store distribution (packaging strategy documented; release packaging is a later milestone)
- Copy-protected and exclusive-mode audio content (OS limitation)
- Languages beyond those supported by the Gemini Live API

## Assumptions

| # | Assumption | Source |
|---|---|---|
| A-1 | Default render device loopback captures the audio users expect (system mix). | Product brief; loopback semantics |
| A-2 | Gemini Live provides acceptable transcription/translation latency and quality for live captions. | Real-wire runs recorded in docs/spikes/GEMINI_MODEL_DISCOVERY.md |
| A-3 | Users accept streaming audio to Google while captions run, given clear disclosure. | ADR-0011; SECURITY_PLAN privacy model |
| A-4 | Windows 10 build 17763+ has all required APIs (WASAPI loopback via NAudio, .NET 8). | .NET 8 support matrix; NAudio Windows 10 support |
| A-5 | The development machine is the primary test environment for hardware-dependent verification. | Repo environment |

## Constraints

- Target OS: Windows 10, build 17763 (1809) or later; Windows 11 also supported
- No VB-CABLE requirement for the MVP
- No dependency on Chrome or Chrome Live Caption
- Internet connection + free Gemini API key required at runtime
- Audio streams only to the Gemini endpoint; no raw audio persistence
- Use only approved stack in [TECH_STACK.md](TECH_STACK.md)
- Follow repository governance ([PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md), [REPOSITORY_STANDARDS.md](REPOSITORY_STANDARDS.md))

## Dependencies

- .NET 8 SDK / runtime on target machines
- NAudio 2.2.1 (WASAPI loopback)
- Google Gemini Live API (user-supplied free API key)
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
