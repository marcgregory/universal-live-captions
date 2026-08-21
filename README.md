# Universal Live Captions

Chrome-Live-Caption-like live captions for **any** Windows application. Captures system audio via WASAPI loopback (no virtual audio cable required), streams it to a Gemini Live session that produces both the transcription and the translation, and renders real-time captions in an always-on-top overlay.

## Install (Windows 10 64-bit, recommended)

1. Download `UniversalCaptions-Setup-*.exe`.
2. Run it and follow the installer.
3. Launch UniversalCaptions from the Start Menu.
4. Add your free Gemini API key in the Control Window (stored in Windows Credential Manager).
5. Press **START**.

That's it. The installer bundles the .NET runtime — no Python, no models, no .NET installation. **An internet connection and a free Gemini API key are required at runtime** (speech recognition and translation run in a Gemini Live session).

## Portable (no installer)

Prefer no installer? Download `UniversalCaptions-*-win-x64.zip`, extract it anywhere, and run `UniversalCaptions.App.exe`. The portable and installed versions contain the same app (~90 MB).

## Privacy

- No microphone capture.
- No raw audio or transcripts are saved to disk.
- Audio is streamed **only** to Google's Gemini API while captions run — never recorded, never sent anywhere else.
- Your API key lives only in Windows Credential Manager on your PC.

## Documentation

- [docs/PRD.md](docs/PRD.md) — product behavior, users, requirements, and acceptance criteria.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — system design, boundaries, data flow, state, and privacy.
- [docs/SECURITY_PLAN.md](docs/SECURITY_PLAN.md) — threat model, privacy model, security controls.
- [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) — packaging, release process, and distribution artifacts.
- [docs/adr/](docs/adr/) — consequential architecture decisions (ADRs 0001–0011; ADR-0011 governs the Gemini-only pipeline).
- [docs/reports/TEST_REPORT.md](docs/reports/TEST_REPORT.md) — test execution evidence.
- [docs/reports/BENCHMARK_REPORT.md](docs/reports/BENCHMARK_REPORT.md) — historical STT and translation benchmark results.
- [docs/reports/INSTALLER_DISCOVERY.md](docs/reports/INSTALLER_DISCOVERY.md) — installer / distribution discovery and decisions.

## Developer documentation

Building from source, the Gemini API key setup for development, the `dotnet build` / `dotnet run` / `dotnet test` / `dotnet publish` quickstart, and developer troubleshooting live in **[docs/DEVELOPER_SETUP.md](docs/DEVELOPER_SETUP.md)**.

Other implementation references:

- [docs/implementation/ROADMAP.md](docs/implementation/ROADMAP.md) — what should be built.
- [docs/implementation/BUILD_PLAN.md](docs/implementation/BUILD_PLAN.md) — how the active and queued sprints will be built.
- [docs/implementation/PROJECT_STATUS.md](docs/implementation/PROJECT_STATUS.md) — current project snapshot.
- [docs/implementation/CHANGELOG.md](docs/implementation/CHANGELOG.md) — versioned history.
- [docs/implementation/TECHNICAL_DEBT.md](docs/implementation/TECHNICAL_DEBT.md) — cleanup list.
- [docs/implementation/RELEASE_PLAN.md](docs/implementation/RELEASE_PLAN.md) — definition of finished.
- [docs/REPOSITORY_STANDARDS.md](docs/REPOSITORY_STANDARDS.md) — folder layout, naming, import rules, dependency boundaries.
- [docs/CHANGE_IMPACT_PROCESS.md](docs/CHANGE_IMPACT_PROCESS.md) — pre-implementation impact analysis and the no-silent-assumptions policy.
