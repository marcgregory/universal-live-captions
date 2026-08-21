# Universal Live Captions Architecture

Last updated: 2026-08-21

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define system architecture, component boundaries, data flow, and design decisions |
| Scope | All system components |
| Audience | Engineering, Architecture review |
| Owner | Engineering |
| Status | Active |
| Related Documents | [TECH_STACK.md](TECH_STACK.md), [SECURITY_PLAN.md](SECURITY_PLAN.md), [DEPLOYMENT.md](DEPLOYMENT.md), [ADRs](adr/README.md), [ADR-0011](adr/ADR-0011-gemini-only-pipeline.md) |

---

## Complexity Classification

**MVP.** A single-machine desktop application with a streaming audio pipeline. No server, no database, no auth, no multi-tenancy. Requires real production hygiene for privacy, error handling, and testing, but no distributed architecture.

## Architecture Summary

A native Windows desktop application organized as a layered, event-driven audio pipeline:

```text
Windows Application Audio
        │
        ▼
Windows Audio Capture (WASAPI loopback, NAudio)
        │
        ▼
Audio Processing
        │   ├── buffering
        │   ├── resampling
        │   └── voice activity detection
        ▼
Gemini Live session (ILiveAudioTranslationEngine)          TLS websocket
        │   ├── speech-to-text (inputAudioTranscription)  ──► generativelanguage.googleapis.com
        │   └── translation (outputAudioTranscription)
        ▼
Source + Transcribed/Translated Transcript events
        │
    ┌───┴──────────────┐
    │                  │
Translation OFF    Translation ON (pipeline gates translation-origin events)
    │                  │
    └───────┬──────────┘
            ▼
Caption State (partial/final transcripts, history)
        │
        ▼
Always-on-top Caption Overlay (WPF)
```

The pipeline is split into separate .NET projects with explicit interfaces at each stage. The overlay and the control window are separate WPF windows sharing state through the caption service.

## Recommended Architecture

- **Host**: .NET 8 desktop application (Windows 10, build 17763+).
- **UI**: WPF for both the control window and the caption overlay.
- **Capture**: `UniversalCaptions.Audio` wraps NAudio `WasapiLoopbackCapture` behind `IAudioCapture`.
- **Processing**: `UniversalCaptions.Audio` implements buffering (`IAudioBuffer`), sample-rate conversion, and voice activity detection (`IVoiceActivityDetector`).
- **Speech + Translation**: `UniversalCaptions.Speech.Gemini` owns `GeminiLiveTranslateEngine`, the single engine for both transcription and translation (per ADR-0011). The contract `ILiveAudioTranslationEngine` (`AudioChunk` in; `PartialTranscriptionAvailable`/`FinalTranscriptionAvailable` for source text and `PartialTranslationAvailable`/`FinalTranslationAvailable` for translated text out) lives in `UniversalCaptions.Core.Translation`. One Gemini Live session handles both surfaces in a single pass; audio streams over TLS to Google's endpoint while captions run.
- **Captions**: `UniversalCaptions.Captions` implements `ICaptionService` as a pure relay/state machine: source partials replace the active line, finals commit to a bounded sequence-ordered history; translation-origin lines follow the same rules gated by origin identity. The `CaptionLine`/`CaptionState`/`ICaptionService`/`CaptionServiceOptions` contracts live in `UniversalCaptions.Core.Captions`. The overlay consumes the resulting `CaptionState` via service events.
- **App / Overlay**: `UniversalCaptions.App` (WPF, `net8.0-windows`) is the DI composition root and the only WPF project. `CaptionPipeline` wires capture (`IAudioCapture`) → processor (`AudioProcessor`) → live engine (`ILiveAudioTranslationEngine` via `LiveTranslationEngineFactory`, which reads the API key from Windows Credential Manager) → `ICaptionService` via `Func` factories (test seams), exposing `StatusChanged`/`LatencyUpdated`/`EndToEndLatencyUpdated` events. The translation toggle (`SetTranslationEnabled`) gates translation-origin caption events without touching the running Gemini session; changing target language recycles the engine. `IOverlayService` owns overlay state per ADR-0004; `CaptionOverlayWindow` renders `CaptionState`; `ControlWindow` exposes capture/language/translation/start-stop/status/latency/overlay settings plus the Gemini key panel. Pipeline events are marshalled to the WPF dispatcher; UI code never calls NAudio or Gemini internals directly.

## Rationale

- WASAPI loopback is the only mechanism that captures the system audio mix on Windows without a virtual audio cable; NAudio provides a mature, Windows-10-compatible wrapper (see ADR-0002).
- WPF provides native support for transparent, borderless, always-on-top windows with click-through (`WS_EX_TRANSPARENT`) and per-monitor DPI (see ADR-0004).
- A single Gemini Live session produces both transcription and translation in one pass with state-of-the-art quality, and removes the entire local-model stack (Whisper binaries, Python venv, Argos packages) from build, packaging, and support surface (see ADR-0011).
- .NET 8 is LTS, installed in the development environment, and supports the Windows 10 minimum target (see ADR-0001).

## Rejected Alternatives

| Alternative | Reason Rejected |
|---|---|
| Rust native | Toolchain not installed; significantly more manual Win32/WASAPI interop for no MVP benefit (see ADR-0001) |
| WebView2 + React/TypeScript UI | Extra runtime dependency and complexity with no benefit for a caption overlay and a small control window |
| VB-CABLE as primary capture | Explicitly excluded by product brief (see ADR-0002) |
| Chrome Live Caption | Explicitly excluded by product brief |
| Electron / Chromium shell | Heavyweight, not justified for a native overlay |
| Local Whisper STT | Removed by ADR-0011: model download/support burden, Tagalog accuracy gap, steady-state latency issues; superseded by Gemini quality |
| Argos Translate local translation | Removed by ADR-0011: Python runtime isolation, MAX_PATH packaging pain, tl-source unsupported, latency; superseded by same-session Gemini translation |
| Windows.Media.SpeechRecognition | Limited language set and no clean path to feed a continuous loopback stream |

## Folder Structure

```text
docs/
src/
  UniversalCaptions.Core/          # interfaces, models, events (no NAudio/WPF)
  UniversalCaptions.Audio/         # WASAPI loopback capture, buffering, resampling, VAD, meters
  UniversalCaptions.Speech.Gemini/ # ILiveAudioTranslationEngine implementation (Gemini Live websocket)
  UniversalCaptions.Captions/      # caption state/service relay
  UniversalCaptions.App/           # WPF control window + overlay + pipeline composition
  UniversalCaptions.Diagnostics/   # diagnostic console apps (audio meter)
tests/
tools/
packaging/
landing/
```

See [REPOSITORY_STANDARDS.md](REPOSITORY_STANDARDS.md) for dependency rules.

## Package Boundaries

- `UniversalCaptions.Core`: pure contracts and value types. Zero third-party dependencies.
- `UniversalCaptions.Audio`: depends on Core + NAudio. Owns all capture/processing implementations.
- `UniversalCaptions.Speech.Gemini`: depends on Core (+ `System.Net.WebSockets`, `System.Text.Json`). Owns `GeminiLiveTranslateEngine` and the wire protocol.
- `UniversalCaptions.Captions`: depends on Core. Pure state logic.
- `UniversalCaptions.App`: depends on all src projects. WPF only. Owns the pipeline, factory, credential store, settings persistence.
- `UniversalCaptions.Diagnostics`: depends on Core + Audio.

## Feature Boundaries

- Capture features (device enumeration, loopback, failure mapping) live only in `UniversalCaptions.Audio`.
- Speech/translation features (session lifecycle, protocol, error classification) live only in `UniversalCaptions.Speech.Gemini`; the live-engine contract lives in Core.
- Caption presentation state lives only in `UniversalCaptions.Captions`; rendering lives only in `UniversalCaptions.App`.

## Application Boundaries

Single desktop process. One control window + one overlay window. Audio/engine run on background tasks; the UI thread only renders.

## Shared Packages

Not applicable — a single solution with project references.

## Data Model

In-memory value types only (no persistence beyond UI preferences):

| Type | Purpose |
|---|---|
| `AudioFormat` | Sample rate, channels, bits per sample |
| `AudioChunk` | Float PCM samples + format + capture timestamp + sequence |
| `ServerContent` / transcript+translation events | Streaming recognition/translation results from the Gemini session |
| `LiveTranslationError` / `TranslationErrorKind` | Classified session failures |
| `CaptionLine` / `CaptionState` | Render-ready caption model |
| `EndToEndLatencySample` | E2E + translation latency measurement |
| `AudioCaptureError` | User-readable capture failure information |

## Database Recommendation

None. The app persists no data beyond UI preferences (`settings.json`, schema v3).

## API Architecture

No server API owned by the app. Cross-component communication uses interfaces and events defined in `UniversalCaptions.Core`. The app consumes Google's Gemini Live websocket API as a client.

## Authentication and Authorization

None required locally. The Gemini session authenticates with the user's API key from Windows Credential Manager.

## State Management

### Server State
The Gemini Live session (setup → streaming → goAway/close). Owned by `GeminiLiveTranslateEngine`; recycled by the pipeline when target language changes.

### Client State
- **Capture state**: idle/capturing/error — owned by `IAudioCapture`, reflected in the control window.
- **Caption state**: `ICaptionService` owns partial/final transition, ordering, duplicate prevention, history, and origin gating (source vs translation).
- **Overlay state**: `IOverlayService` owns visibility, position, opacity, font size, click-through.

### Realtime State
- Streaming flow is a realtime pipeline; `AudioChunk` timestamps flow from capture → engine → caption render to support latency measurement.

### Synchronization Rules
- UI thread never blocks on the audio pipeline.
- Audio/engine pipeline uses dedicated background loops; caption events marshalled to the WPF dispatcher for rendering.

## Realtime Strategy

Event-driven streaming: `IAudioCapture.AudioAvailable` → processor → `ILiveAudioTranslationEngine` events → `ICaptionService` → `IOverlayService`. The only network transport is the TLS websocket to the Gemini endpoint.

## Background Jobs

One long-running background loop per capture session (audio pump) plus one Gemini receive loop per session. No job queues.

## Security Architecture

Cloud-processing disclosure model. Threat model, privacy model, and data classification are in [SECURITY_PLAN.md](SECURITY_PLAN.md); key management in ADR-0009.

## Observability

MVP: structured diagnostics via the diagnostic console and lightweight timestamps for latency measurement. Full observability is out of MVP scope (see [DEPLOYMENT.md](DEPLOYMENT.md) for notes).

## Deployment Architecture

Self-contained local application (~145 MB trimmed publish). Dev/debug via `dotnet run`; packaging via `packaging/build-package.ps1` (portable ZIP + Inno Setup installer). Installer strategy documented in [DEPLOYMENT.md](DEPLOYMENT.md).

## Performance Targets

- Perceived caption latency < 1000 ms where practical — **measured**, not assumed.
- Translation latency measured end-to-end (`EndToEndLatencyUpdated`).
- Sustained capture session without dropouts for typical playback workloads.

## Accessibility Requirements

- Captions must be readable: configurable font size, opacity, and background.
- Overlay must not block user input when in click-through mode.
- Keyboard accessibility for the control window (WPF defaults + Tab navigation).

## Architecture Risks

See [RISK_REGISTER.md](RISK_REGISTER.md). Notable: loopback does not capture protected/exclusive-mode audio; Gemini availability/quota dependence (network required); real-wire verification that `inputTranscription` texts stream back remains a release gate; Windows 10 device-state variance.
