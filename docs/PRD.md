# Universal Live Captions PRD

Last updated: 2026-08-21

> **Amendment (2026-08-21, ADR-0011):** the product now uses a single Gemini Live session for both
> speech-to-text and translation. Local Whisper and Argos Translate are removed. Audio is streamed
> to Google's API while captions run (disclosed); there is no offline mode. Sections below reflect
> this direction.

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define product requirements, user stories, acceptance criteria, and success metrics |
| Scope | Product features, user experience, non-functional requirements |
| Audience | Engineering, Product, Design, QA |
| Owner | Engineering |
| Status | Active |
| Related Documents | [PROJECT_SCOPE.md](PROJECT_SCOPE.md), [QUALITY_ASSURANCE.md](QUALITY_ASSURANCE.md), [RISK_REGISTER.md](RISK_REGISTER.md), [ARCHITECTURE.md](ARCHITECTURE.md) |

---

## Product Summary

**Universal Live Captions** is a native Windows desktop application that provides Chrome-Live-Caption-like functionality for **any** Windows application. It captures audio playing through Windows via WASAPI loopback (no VB-CABLE required), streams it to a Gemini Live session that produces both the transcription and the translation, and renders real-time captions in an always-on-top, configurable overlay window.

## Target Users

- People who are deaf or hard of hearing and need captions for apps that do not provide them
- Users of video conferencing (Zoom, Teams), web browsers, media players, and games on Windows 10
- People who prefer captioning for comprehension in meetings, lectures, and videos

## Problem

Many Windows applications do not provide live captions. Chrome offers Live Caption, but only inside Chrome. Deaf and hard-of-hearing users, and users who benefit from captions, cannot get captions for content played through arbitrary Windows applications.

## Goals

- Provide real-time captions for any audio playing through Windows
- Work without Chrome and without Chrome Live Caption
- Require no VB-CABLE or other virtual audio cable for normal system-audio capture
- Support Windows 10
- Be transparent about cloud processing: audio streams only to the Gemini endpoint while captions run, never recorded, never sent elsewhere
- Render captions in an always-on-top overlay that is configurable (opacity, size, position, multi-monitor, click-through)

## Non-Goals

- Not a general speech-to-text transcription recorder (no file output of transcripts in the MVP)
- Not a microphone-based assistant (microphone capture is explicitly out of scope unless enabled later)
- No per-application audio separation in the MVP (loopback captures the system mix)
- No offline mode (ADR-0011 removed local engines)

## Personas

| Persona | Description | Primary Needs |
|---|---|---|
| **Alex (deaf/hard of hearing)** | Uses Zoom, YouTube, and VLC daily. Cannot understand speech without captions. | Captions for every app, low latency, readable overlay, no setup burden |
| **Priya (second-language)** | Watches lectures and meetings; captions improve comprehension. | Accuracy, configurable source/target language, live translation on/off, adjustable font size and position |
| **Marcus (gamer)** | Watches game dialog and streaming content; captions help when audio is unclear. | Works while gaming, overlay does not block input, minimal performance impact |

## Core Use Cases

1. User starts captions; system audio is captured and captions appear in the overlay.
2. User moves/resizes/configures the overlay (opacity, font, position, monitor, click-through).
3. User stops captions; capture ends and audio is not persisted.
4. Audio device disconnects mid-session; the app recovers gracefully with a readable error.
5. User selects caption language.
6. User enables live translation and selects target language; the caption overlay shows the translated transcript alongside/instead of the source.

## Functional Requirements

| ID | Requirement |
|---|---|
| FR-1 | Capture system audio via WASAPI loopback without VB-CABLE |
| FR-2 | Continuously buffer and process captured PCM audio |
| FR-3 | Detect speech (VAD) and stream audio to the speech engine |
| FR-4 | Produce partial (in-progress) transcripts and final transcripts |
| FR-5 | Maintain caption state: partial → final transitions, ordering, no duplicates, history |
| FR-6 | Render captions in a borderless, always-on-top, draggable, resizable overlay |
| FR-7 | Overlay supports configurable opacity, font size, position, multi-monitor, and click-through mode |
| FR-8 | Provide a minimal control window to start/stop captions and adjust settings |
| FR-9 | Clearly indicate when audio capture is active |
| FR-10 | Surface user-readable errors for capture/device/session failures (classified Gemini errors: auth, quota, network) |
| FR-11 | Support configurable speech language |
| FR-12 | Optionally translate the source transcript to a target language in the same Gemini Live session (`ILiveAudioTranslationEngine` abstraction) |
| FR-13 | Select source language and target language |
| FR-14 | Toggle translation on/off while captions are active; translated captions replace or supplement source captions in the overlay |
| FR-15 | Stream audio only to the configured Gemini endpoint while captions run; never record audio or transcripts to disk |

## Non-Functional Requirements

| ID | Requirement | Target |
|---|---|---|
| NFR-1 | Runs on Windows 10 (build 17763 / version 1809 or later) | Must be verified |
| NFR-2 | Perceived caption latency | < 1 second where practical; must be measured, not assumed |
| NFR-3 | Continuous streaming with no stutter under normal load | Must sustain capture for extended sessions |
| NFR-4 | No raw audio or transcripts persisted | Privacy |
| NFR-5 | Cloud processing disclosed; single network destination | Gemini endpoint only (ADR-0011) |
| NFR-6 | Graceful recovery from device/session failure | No crashes on device loss |
| NFR-7 | Automated tests for capture, buffering, conversion, VAD, engine contract, caption state, overlay | Per QUALITY_ASSURANCE.md |

## User Stories

| Story | Acceptance Criteria |
|---|---|
| As a user, I can start captions for system audio and see captions appear. | Capture starts; partial captions appear within the latency target while audio plays. |
| As a user, I can stop captions at any time. | Capture stops; no further audio processing; overlay clears or stays as configured. |
| As a user, I can move and resize the caption overlay. | Overlay drags and resizes; settings persist across restarts (MVP: in-process). |
| As a user, I can configure opacity, font size, and click-through. | Changes apply immediately to the overlay. |
| As a user, I can choose a caption language. | Language selection is passed to the Gemini session. |
| As a user, I can enable live translation and pick a target language. | Target selection is passed to the Gemini session; translated captions appear in the overlay. |
| As a user, I can turn translation off mid-session. | Captions revert to the source-language transcript without restarting capture or the session. |
| As a user, I see a readable message when an audio device is unavailable or disconnects. | No crash; user-readable error and recovery/retry. |
| As a user, I am clearly informed when audio capture is active. | Visible capture indicator in the control window. |
| As a user, I know where my audio goes. | Documentation and landing page disclose that audio streams to Google's Gemini API while captions run; nothing is recorded; no other destination exists. |

## Acceptance Criteria (MVP Definition of Done)

- Application runs on Windows 10
- System audio captured through WASAPI loopback without VB-CABLE
- Audio processed continuously
- STT produces partial transcripts; final transcripts replace/complete partials correctly
- Translation produces translated captions in the same Gemini session when enabled (source transcript → target language)
- Captions render in an always-on-top, configurable overlay
- Capture can be started and stopped explicitly
- Failure states handled with user-readable errors
- Automated tests pass
- Build/type checks pass
- Documentation updated
- Bootstrap validation passes
- No unrelated files modified
- Architecture decisions documented per repository governance

## Metrics

| Metric | Definition | Target |
|---|---|---|
| Perceived caption latency | Capture timestamp → caption render timestamp | < 1000 ms (measured in Slice 6) |
| Partial transcript availability | Time to first partial after speech onset | Measured in Slice 2/6 |
| Translation latency | Source transcript → translated caption | Measured end-to-end (`EndToEndLatencyUpdated`) |
| Translation quality | Human-evaluated fidelity of translations | Gemini Live quality; spot-checked against real content |
| Uptime stability | Continuous capture session without crash | 60+ minutes in manual test |
| Test pass rate | Passing tests / total automated tests | 100% at each slice |

## Risks and Open Questions

See [RISK_REGISTER.md](RISK_REGISTER.md) for the full register. Headline risks: Windows 10 API variance, Gemini availability/quota dependence (network required; free-tier limits), real-wire verification that `inputTranscription` texts stream back (release gate), loopback excludes some exclusive-mode/copied-protected audio, privacy perception of a global audio capturer.
