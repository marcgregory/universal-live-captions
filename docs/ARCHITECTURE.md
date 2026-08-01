# Universal Live Captions Architecture

Last updated: 2026-08-01

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define system architecture, component boundaries, data flow, and design decisions |
| Scope | All system components |
| Audience | Engineering, Architecture review |
| Owner | Engineering |
| Status | Active |
| Related Documents | [TECH_STACK.md](TECH_STACK.md), [SECURITY_PLAN.md](SECURITY_PLAN.md), [DEPLOYMENT.md](DEPLOYMENT.md), [ADRs](adr/README.md) |

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
Streaming Speech-to-Text (ISpeechToTextEngine)
        │
        ▼
Source Transcript
        │
    ┌───┴──────────────┐
    │                  │
Translation OFF   Translation ON (ITranslationEngine)
    │                  │
    │                  ▼
    │          Translated Transcript
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
- **Speech**: `UniversalCaptions.Speech` owns the speech engines. The streaming contract `ISpeechToTextEngine` (`AudioChunk` in, `PartialTranscript`/`FinalTranscript` out) and the transcript/error types live in `UniversalCaptions.Core.Speech` (per ADR-0005 and the `REPOSITORY_STANDARDS.md` dependency table, so `UniversalCaptions.Captions` can consume transcripts while referencing only Core). The first engine is local Whisper.
- **Translation**: `UniversalCaptions.Core.Translation` defines `ITranslationEngine` (source transcript in, translated transcript out) plus `TranslationResult` and the translation error types (per ADR-0006 and the `REPOSITORY_STANDARDS.md` dependency table, so `UniversalCaptions.Captions` can consume translations while referencing only Core). `UniversalCaptions.Translation` owns the concrete engines; the first engine is Argos Translate. Because Argos is Python-based, it is never embedded in the .NET process; `ArgosTranslationEngine` communicates with a local Argos process (subprocess/service) over a simple newline-delimited JSON line protocol (see ADR-0006).
- **Captions**: `UniversalCaptions.Captions` implements `ICaptionService` for partial/final state transitions and history. The `CaptionLine`/`CaptionState`/`ICaptionService`/`CaptionServiceOptions` contracts live in `UniversalCaptions.Core.Captions` so the caption service depends only on Core; it consumes `PartialTranscript`/`FinalTranscript` (Speech) and optional `ITranslationEngine` (Translation) through those Core contracts. Partials replace the active line; finals commit to a bounded sequence-ordered history and are translated in the background when translation is enabled (a translation failure leaves the source caption intact). The overlay consumes the resulting `CaptionState` via service events.
- **App / Overlay**: `UniversalCaptions.App` (WPF, `net8.0-windows`) is the DI composition root and the only WPF project. `CaptionPipeline` wires capture (`IAudioCapture`/`WasapiLoopbackCaptureSource`) → processor (`AudioProcessor`) → STT (`ISpeechToTextEngine`/`WhisperSpeechToTextEngine`) → `ICaptionService` via `Func` factories (test seams), exposing `StatusChanged`/`LatencyUpdated` events. `IOverlayService` owns overlay state (visibility, position, opacity, font size, click-through) per ADR-0004; `CaptionOverlayWindow` renders `CaptionState` (active line verbatim from the latest partial; committed finals as bounded history; translated text replaces the source on a committed line only when translation completes) and `ControlWindow` exposes capture/language/translation/start-stop/status/latency/overlay settings. Pipeline events are marshalled to the WPF dispatcher; UI code never calls Whisper/Argos/NAudio internals directly.

## Rationale

- WASAPI loopback is the only mechanism that captures the system audio mix on Windows without a virtual audio cable; NAudio provides a mature, Windows-10-compatible wrapper (see ADR-0002).
- WPF provides native support for transparent, borderless, always-on-top windows with click-through (`WS_EX_TRANSPARENT`) and per-monitor DPI (see ADR-0004).
- A streaming, engine-neutral STT interface keeps the app independent of any single recognition provider and satisfies the privacy requirement of a local-first pipeline (see ADR-0003).
- A translation-engine abstraction keeps the app independent of any single translation provider. Argos Translate was chosen for the MVP because it is genuinely offline/local, supports pivoting through an intermediate language for pairs without a direct model, and runs on CPU/CUDA; because it is Python-based, it is isolated behind a local process so the C# app has no Python dependency (see ADR-0006).
- .NET 8 is LTS, installed in the development environment, and supports the Windows 10 minimum target (see ADR-0001).

## Rejected Alternatives

| Alternative | Reason Rejected |
|---|---|
| Rust native | Toolchain not installed; significantly more manual Win32/WASAPI interop for no MVP benefit (see ADR-0001) |
| WebView2 + React/TypeScript UI | Extra runtime dependency and complexity with no benefit for a caption overlay and a small control window |
| VB-CABLE as primary capture | Explicitly excluded by product brief (see ADR-0002) |
| Chrome Live Caption | Explicitly excluded by product brief |
| Electron / Chromium shell | Heavyweight, not justified for a native overlay |
| Cloud-only STT | Conflicts with privacy requirement (see ADR-0003) |
| Windows.Media.SpeechRecognition | Limited language set and no clean path to feed a continuous loopback stream; Whisper gives better control |
| Cloud translation (e.g., Azure Translator) | Sends transcripts off-machine; conflicts with the local-first privacy requirement (see ADR-0006) |
| Embedding Python directly into the .NET process | Fragile runtime coupling and packaging; rejected in favor of isolating Argos behind a local process (see ADR-0006) |
| Raw Marian NMT integration for translation | Considerably more model/runtime integration work than Argos with no MVP benefit (see ADR-0006) |

## Folder Structure

```text
docs/
src/
  UniversalCaptions.Core/          # interfaces, models, events (no NAudio/WPF)
  UniversalCaptions.Audio/         # WASAPI loopback capture, buffering, resampling, VAD, meters
  UniversalCaptions.Speech/        # ISpeechToTextEngine + engines (Slice 2)
  UniversalCaptions.Translation/   # translation engines (ArgosTranslationEngine) (Slice 3)
  UniversalCaptions.Captions/      # caption state/service (Slice 4)
  UniversalCaptions.App/           # WPF control window + overlay (Slice 5)
  UniversalCaptions.Diagnostics/   # diagnostic console apps (Slice 1: audio meter)
tests/
```

See [REPOSITORY_STANDARDS.md](REPOSITORY_STANDARDS.md) for dependency rules.

## Package Boundaries

- `UniversalCaptions.Core`: pure contracts and value types. Zero third-party dependencies.
- `UniversalCaptions.Audio`: depends on Core + NAudio. Owns all capture/processing implementations.
- `UniversalCaptions.Speech`: depends on Core (+ Whisper binding). Owns `WhisperSpeechToTextEngine` and other engines.
- `UniversalCaptions.Translation`: depends on Core. Owns `ArgosTranslationEngine` and other engines (Argos runs as a local process; the .NET project owns the process protocol and lifecycle). The `ITranslationEngine` contract lives in Core.
- `UniversalCaptions.Captions`: depends on Core. Pure state logic.
- `UniversalCaptions.App`: depends on all src projects. WPF only.
- `UniversalCaptions.Diagnostics`: depends on Core + Audio.

## Feature Boundaries

- Capture features (device enumeration, loopback, failure mapping) live only in `UniversalCaptions.Audio`.
- Speech features (engine selection, transcription events) live only in `UniversalCaptions.Speech`.
- Translation features (engine selection, translation events) live only in `UniversalCaptions.Translation`; the translation contract lives in Core.
- Caption presentation state lives only in `UniversalCaptions.Captions`; rendering lives only in `UniversalCaptions.App`.

## Application Boundaries

Single desktop process. One control window + one overlay window. Audio/STT run on background tasks; the UI thread only renders.

## Shared Packages

Not applicable — a single solution with project references.

## Data Model

In-memory value types only (no persistence in the MVP):

| Type | Purpose |
|---|---|
| `AudioFormat` | Sample rate, channels, bits per sample |
| `AudioChunk` | Float PCM samples + format + capture timestamp + sequence |
| `PartialTranscript` / `FinalTranscript` | Streaming recognition results |
| `TranslationResult` | Translated transcript + detected/pivoted language pair |
| `CaptionLine` / `CaptionState` | Render-ready caption model |
| `AudioCaptureError` | User-readable capture failure information |

## Database Recommendation

None. The MVP persists no data.

## API Architecture

No server API. Cross-component communication uses interfaces and events defined in `UniversalCaptions.Core`.

## Authentication and Authorization

None required — a local desktop application with no accounts.

## State Management

### Server State
None.

### Client State
- **Capture state**: idle/capturing/error — owned by `IAudioCapture`, reflected in the control window.
- **Caption state**: `ICaptionService` owns partial/final transition, ordering, duplicate prevention, and history, and whether the source or translated transcript is displayed.
- **Overlay state**: `IOverlayService` owns visibility, position, opacity, font size, click-through.

### Realtime State
- Streaming transcript flow is a realtime pipeline; `AudioChunk` timestamps flow from capture → STT → caption render to support latency measurement.

### Synchronization Rules
- UI thread never blocks on the audio pipeline.
- Audio/STT pipeline uses a dedicated background loop; caption events marshalled to the WPF dispatcher for rendering.

## Realtime Strategy

Event-driven streaming in-process: `IAudioCapture.AudioAvailable` → processor → `ISpeechToTextEngine` events → optional `ITranslationEngine` → `ICaptionService` → `IOverlayService`. No network transport in the MVP; translation stays local/offline.

## Background Jobs

One long-running background loop per capture session (audio pump). No job queues.

## Security Architecture

Local-only. Threat model, privacy model, and data classification are in [SECURITY_PLAN.md](SECURITY_PLAN.md).

## Observability

MVP: structured diagnostics via the diagnostic console and lightweight timestamps for latency measurement. Full observability is out of MVP scope (see [DEPLOYMENT.md](DEPLOYMENT.md) for notes).

## Deployment Architecture

Self-contained local application. Dev/debug via `dotnet run`; packaging via `dotnet publish` (self-contained, single-file option). Installer strategy documented in [DEPLOYMENT.md](DEPLOYMENT.md).

## Performance Targets

- Perceived caption latency < 1000 ms where practical — **measured**, not assumed (Slice 6).
- Translation latency/quality benchmarked against real captions (Slice 3/6).
- Sustained capture session without dropouts for typical playback workloads.

## Accessibility Requirements

- Captions must be readable: configurable font size, opacity, and background.
- Overlay must not block user input when in click-through mode.
- Keyboard accessibility for the control window (WPF defaults + Tab navigation).

## Architecture Risks

See [RISK_REGISTER.md](RISK_REGISTER.md). Notable: loopback does not capture protected/exclusive-mode audio; Whisper accuracy/latency tradeoff unmeasured until Slice 2/6; Argos translation latency/quality unmeasured until Slice 3/6; Python runtime isolation and startup cost for the local Argos process; Windows 10 device-state variance.
