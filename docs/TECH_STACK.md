# Universal Live Captions Technology Stack

Last updated: 2026-08-21

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Document technology decisions, rationale, and approved dependencies |
| Scope | All technology choices including frameworks, libraries, tools, and infrastructure |
| Audience | Engineering, Architecture review |
| Owner | Engineering |
| Status | Active |
| Related Documents | [ARCHITECTURE.md](ARCHITECTURE.md), [SECURITY_PLAN.md](SECURITY_PLAN.md), [DEPLOYMENT.md](DEPLOYMENT.md), [ADR-0011](adr/ADR-0011-gemini-only-pipeline.md) |

---

## Summary

Native Windows 10 desktop application in C# / .NET 8 with WPF for UI, NAudio for WASAPI loopback audio capture, and a single Gemini Live session (`ILiveAudioTranslationEngine`) for both speech-to-text and live translation. Approved by the user on 2026-07-31; Gemini-only pipeline approved by ADR-0011 (2026-08-21).

## Technology Decision Summary

### Recommended Stack

- **Language/Runtime**: C# / .NET 8 (LTS)
- **UI**: WPF
- **Audio capture**: NAudio 2.2.1 (WASAPI loopback)
- **Speech-to-text + translation**: `ILiveAudioTranslationEngine` abstraction; single engine `GeminiLiveTranslateEngine` (Gemini Live websocket API)
- **DI**: Microsoft.Extensions.DependencyInjection
- **Testing**: xUnit
- **Solution**: .NET solution with per-layer projects (`src/`, `tests/`)

### Chosen Stack

Approved as recommended.

### Approval Status

Originally approved by user on 2026-07-31 (.NET 8 + WPF + NAudio + local Whisper + Argos). Amended by ADR-0011 (2026-08-21): local Whisper and Argos removed; Gemini Live is the single STT + translation engine.

### Alternatives Considered

| Option | Consideration | Outcome |
|---|---|---|
| Rust + windows-rs | Not installed; more manual WASAPI/Win32 interop | Rejected (ADR-0001) |
| WebView2 + React/TypeScript | Extra runtime dependency, no benefit for overlay | Rejected (ADR-0001) |
| VB-CABLE virtual cable | Works but violates product requirement | Rejected (ADR-0002) |
| Local Whisper STT | Model download/support burden; Tagalog accuracy gap; steady-state latency regressions measured in Slices 6–9 | Removed (ADR-0011) |
| Argos Translate local translation | Python runtime isolation, MAX_PATH packaging pain, `tl`-as-source unsupported, latency | Removed (ADR-0011) |
| Windows.Media.SpeechRecognition | Limited languages; no clean loopback feed | Rejected (ADR-0003) |
| Cloud translation (Azure Translator) | Separate service/key; no streaming integration with recognition | Superseded by Gemini Live single-session design (ADR-0011) |
| WinForms | Weaker per-monitor DPI and transparency/click-through support | Rejected (ADR-0004) |

### Tradeoffs

- **.NET 8 over Rust**: less raw performance, far faster delivery and testability on the installed toolchain.
- **NAudio over raw WASAPI COM**: less control over edge cases, but battle-tested loopback support on Windows 10.
- **Gemini Live over local engines**: state-of-the-art quality for both transcription and translation in one pass, zero model downloads (~145 MB app vs multi-GB stacks), but requires internet + a free API key, and audio streams to Google while captions run (disclosed per SECURITY_PLAN).
- **WPF over WebView2**: native transparency/always-on-top primitives vs. web flexibility that is not needed.

## Application

| Concern | Choice |
|---|---|
| UI framework | WPF (net8.0-windows for `UniversalCaptions.App`; other projects target net8.0) |
| Overlay | Borderless WPF window, per-monitor DPI, layered/click-through (`WS_EX_TRANSPARENT`) |
| State/DI | Microsoft.Extensions.DependencyInjection |

`UniversalCaptions.App` is the DI composition root: it resolves the real pipeline once (factory → `CaptionService` → `AudioProcessor` → capture/live-engine factories → `CaptionPipeline` → overlay + control windows) and wires events; `IOverlayService` owns overlay state (visibility/position/opacity/font size/click-through per ADR-0004). `Microsoft.Extensions.DependencyInjection` 8.0.0 is pinned in `UniversalCaptions.App`. WPF windows render `CaptionState` only; UI code never calls engine internals.

## Backend

Not applicable — local desktop application. The only external service consumed is Google's Gemini Live websocket API.

## Database and Storage

No database. The only persisted data is UI preferences at `%LocalAppData%\UniversalCaptions\settings.json` (schema v3) and the Gemini API key in Windows Credential Manager.

## Authentication and Authorization

Not applicable locally. The Gemini session authenticates via the user's API key.

## Realtime and Background Jobs

In-process streaming events; one background audio pump plus one Gemini receive loop per session.

## Testing

| Concern | Choice |
|---|---|
| Framework | xUnit 2.x |
| Runner | `dotnet test` |
| Hardware boundaries | Fake implementations of `IWaveIn`-style boundaries in `UniversalCaptions.Audio.Tests` |
| Engine boundary | Fake `ILiveAudioTranslationEngine` implementations in App/Captions tests; protocol pins in Speech.Gemini tests |

## Development Tools

| Tool | Purpose |
|---|---|
| .NET SDK 8/10 | Build, test, run (SDK 10 installed; projects target net8.0) |
| dotnet CLI | All commands (no IDE required) |

## Deployment and Operations

- `dotnet publish` self-contained win-x64, trimmed (~145 MB); see [DEPLOYMENT.md](DEPLOYMENT.md)
- Packaging via `packaging/build-package.ps1` (portable ZIP + Inno Setup installer)

## Package Recommendations

| Package | Version | Where | Purpose |
|---|---|---|---|
| NAudio | 2.2.1 | UniversalCaptions.Audio | WASAPI loopback capture |
| Microsoft.Extensions.DependencyInjection | 8.0.0 | UniversalCaptions.App | DI composition root |
| xUnit | 2.x | tests/ | Unit testing |
| Microsoft.NET.Test.Sdk | latest stable | tests/ | Test runner |
| xunit.runner.visualstudio | latest stable | tests/ | VS test adapter |

There are no model binaries or Python runtimes anywhere in the stack (ADR-0011). The Gemini engine uses only BCL types (`System.Net.WebSockets`, `System.Text.Json`).

## Rejected Options

Recorded above in "Alternatives Considered" and in ADR-0001–0006, ADR-0011.
