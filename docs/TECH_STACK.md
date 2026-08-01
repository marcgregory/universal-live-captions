# Universal Live Captions Technology Stack

Last updated: 2026-08-01

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Document technology decisions, rationale, and approved dependencies |
| Scope | All technology choices including frameworks, libraries, tools, and infrastructure |
| Audience | Engineering, Architecture review |
| Owner | Engineering |
| Status | Active |
| Related Documents | [ARCHITECTURE.md](ARCHITECTURE.md), [SECURITY_PLAN.md](SECURITY_PLAN.md), [DEPLOYMENT.md](DEPLOYMENT.md) |

---

## Summary

Native Windows 10 desktop application in C# / .NET 8 with WPF for UI, NAudio for WASAPI loopback audio capture, a local Whisper engine behind a streaming speech-to-text abstraction, and Argos Translate behind a local translation abstraction for offline live translation. Approved by the user on 2026-07-31.

## Technology Decision Summary

### Recommended Stack

- **Language/Runtime**: C# / .NET 8 (LTS)
- **UI**: WPF
- **Audio capture**: NAudio 2.2.1 (WASAPI loopback)
- **Speech-to-text**: `ISpeechToTextEngine` abstraction; first engine local Whisper (whisper.cpp binding)
- **Translation**: `ITranslationEngine` abstraction; first engine Argos Translate (offline/local, isolated behind a local process, CPU/CUDA)
- **DI**: Microsoft.Extensions.DependencyInjection
- **Testing**: xUnit
- **Solution**: .NET solution with per-layer projects (`src/`, `tests/`)

### Chosen Stack

Approved as recommended.

### Approval Status

Approved by user on 2026-07-31 (.NET 8 + WPF + NAudio + local Whisper behind `ISpeechToTextEngine` + Argos Translate behind `ITranslationEngine`; Whisper model size and Argos model/pair selection deferred pending latency/quality benchmarking).

### Alternatives Considered

| Option | Consideration | Outcome |
|---|---|---|
| Rust + windows-rs | Not installed; more manual WASAPI/Win32 interop | Rejected (ADR-0001) |
| WebView2 + React/TypeScript | Extra runtime dependency, no benefit for overlay | Rejected (ADR-0001) |
| VB-CABLE virtual cable | Works but violates product requirement | Rejected (ADR-0002) |
| Cloud streaming STT (Azure/OpenAI) | Audio leaves the machine | Rejected for MVP (ADR-0003) |
| Windows.Media.SpeechRecognition | Limited languages; no clean loopback feed | Rejected (ADR-0003) |
| Cloud translation (Azure Translator) | Transcripts leave the machine; API key/account needed | Rejected for MVP (ADR-0006) |
| Raw Marian NMT for translation | More model/runtime integration work than Argos | Rejected (ADR-0006) |
| WinForms | Weaker per-monitor DPI and transparency/click-through support | Rejected (ADR-0004) |

### Tradeoffs

- **.NET 8 over Rust**: less raw performance, far faster delivery and testability on the installed toolchain.
- **NAudio over raw WASAPI COM**: less control over edge cases, but battle-tested loopback support on Windows 10.
- **Local Whisper over cloud**: no upload/privacy concerns, but model size drives accuracy/latency tradeoff that must be benchmarked.
- **Argos over cloud translation**: genuinely offline/local and supports pivoting through an intermediate language, but is Python-based, so it is isolated behind a local process to keep the C# app Python-free; translation latency and per-pair model installs must be benchmarked.
- **WPF over WebView2**: native transparency/always-on-top primitives vs. web flexibility that is not needed.

## Application

| Concern | Choice |
|---|---|
| UI framework | WPF (net8.0-windows for `UniversalCaptions.App`; other projects target net8.0) |
| Overlay | Borderless WPF window, per-monitor DPI, layered/click-through (`WS_EX_TRANSPARENT`) |
| State/DI | Microsoft.Extensions.DependencyInjection |

`UniversalCaptions.App` (added Slice 5, 2026-08-01) is the DI composition root: it resolves the real pipeline once (Argos → `CaptionService` → `AudioProcessor` → capture/STT factories → `CaptionPipeline` → overlay + control windows) and wires events; `IOverlayService` owns overlay state (visibility/position/opacity/font size/click-through per ADR-0004). `Microsoft.Extensions.DependencyInjection` 8.0.0 is pinned in `UniversalCaptions.App`. WPF windows render `CaptionState` only; UI code never calls engine internals.

## Backend

Not applicable — local desktop application, no backend.

## Database and Storage

None in the MVP. No database, no persistent storage.

## Authentication and Authorization

Not applicable.

## Realtime and Background Jobs

In-process streaming events; one background audio pump per session.

## Testing

| Concern | Choice |
|---|---|
| Framework | xUnit 2.x |
| Runner | `dotnet test` |
| Hardware boundaries | Fake implementations of `IWaveIn`-style boundaries in `UniversalCaptions.Audio.Tests` |

## Development Tools

| Tool | Purpose |
|---|---|
| .NET SDK 8/10 | Build, test, run (SDK 10 installed; projects target net8.0) |
| dotnet CLI | All commands (no IDE required) |

## Deployment and Operations

- `dotnet publish` self-contained, single-file where practical (see [DEPLOYMENT.md](DEPLOYMENT.md))
- Installer/packaging strategy defined in [DEPLOYMENT.md](DEPLOYMENT.md)

## Package Recommendations

| Package | Version | Where | Purpose |
|---|---|---|---|
| NAudio | 2.2.1 | UniversalCaptions.Audio | WASAPI loopback capture |
| Microsoft.Extensions.DependencyInjection | 8.0.0 | UniversalCaptions.App | DI composition root |
| xUnit | 2.x | tests/ | Unit testing |
| Microsoft.NET.Test.Sdk | latest stable | tests/ | Test runner |
| xunit.runner.visualstudio | latest stable | tests/ | VS test adapter |

Whisper model binaries and Argos Translate language-model packages are runtime data, not NuGet packages; they are downloaded/installed under `artifacts/models/` (Whisper) and `artifacts/argos/` (Argos venv + packages), both git-ignored. The Argos Python runtime is installed separately (a dedicated `python -m venv` created in Slice 3) and launched as a local process by `ArgosTranslationEngine`; it is not embedded in the .NET process.

**Argos runtime (dev, Slice 3, verified 2026-08-01):** Argos Translate 1.11.0 on Python 3.11 in a dedicated venv (`artifacts/argos/venv`; on the dev machine created under the temp dir with the short 8.3 path to avoid Windows MAX_PATH limits during the torch install). Four direct language packages installed from the Argos index: `en→tl`, `tl→en`, `ja→en`, `en→ja`. Pivoting works for pairs without a direct model (verified `ja→tl` via `en`). **Known limitation:** Argos sentence-boundary detection does not support `tl` as a source language; the MVP pairs use `tl` only as a target. The line-protocol server script is bundled in the Translation project (`Server/argos_translate_server.py`) and copied to output. Translation model/package selection is now resolved per ADR-0006; no Argos package is a NuGet dependency.

## Rejected Options

Recorded above in "Alternatives Considered" and in ADR-0001, ADR-0002, ADR-0003, ADR-0004, ADR-0006.
