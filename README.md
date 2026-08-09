# Universal Live Captions

Chrome-Live-Caption-like live captions for **any** Windows application. Captures system audio via WASAPI loopback (no virtual audio cable required), recognizes speech locally, and renders real-time captions in an always-on-top overlay.

## Install (Windows 10 64-bit, recommended)

1. Download `UniversalCaptions-Setup-*.exe`.
2. Run it and follow the installer.
3. Launch UniversalCaptions from the Start Menu.
4. Press **START**.

That's it. The installer bundles the .NET runtime, the Python runtime, the speech-recognition model, and the local translation packages. **No Python installation. No .NET installation. No internet connection required at runtime.**

## Portable (no installer)

Prefer no installer? Download `UniversalCaptions-*-win-x64-full.zip`, extract it anywhere, and run `launcher.cmd`. The portable and installed versions contain the same offline speech-recognition and translation components.

## Privacy

- No microphone capture.
- No raw audio is saved to disk.
- Speech recognition and translation run locally. Nothing leaves the machine.

## Documentation

- [docs/PRD.md](docs/PRD.md) — product behavior, users, requirements, and acceptance criteria.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — system design, boundaries, data flow, state, and privacy.
- [docs/SECURITY_PLAN.md](docs/SECURITY_PLAN.md) — threat model, privacy model, security controls.
- [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) — packaging, release process, and distribution artifacts.
- [docs/adr/](docs/adr/) — consequential architecture decisions (ADRs 0001–0009).
- [docs/reports/TEST_REPORT.md](docs/reports/TEST_REPORT.md) — test execution evidence.
- [docs/reports/BENCHMARK_REPORT.md](docs/reports/BENCHMARK_REPORT.md) — STT and translation benchmark results.
- [docs/reports/INSTALLER_DISCOVERY.md](docs/reports/INSTALLER_DISCOVERY.md) — installer / distribution discovery and decisions.

## Developer documentation

Building from source, running tests, environment-variable knobs, the `dotnet build` / `dotnet run` / `dotnet test` / `dotnet publish` quickstart, and developer troubleshooting live in **[docs/DEVELOPER_SETUP.md](docs/DEVELOPER_SETUP.md)**.

Other implementation references:

- [docs/implementation/ROADMAP.md](docs/implementation/ROADMAP.md) — what should be built.
- [docs/implementation/BUILD_PLAN.md](docs/implementation/BUILD_PLAN.md) — how the active and queued sprints will be built.
- [docs/implementation/PROJECT_STATUS.md](docs/implementation/PROJECT_STATUS.md) — current project snapshot.
- [docs/implementation/CHANGELOG.md](docs/implementation/CHANGELOG.md) — versioned history.
- [docs/implementation/TECHNICAL_DEBT.md](docs/implementation/TECHNICAL_DEBT.md) — cleanup list.
- [docs/implementation/RELEASE_PLAN.md](docs/implementation/RELEASE_PLAN.md) — definition of finished.
- [docs/REPOSITORY_STANDARDS.md](docs/REPOSITORY_STANDARDS.md) — folder layout, naming, import rules, dependency boundaries.
- [docs/CHANGE_IMPACT_PROCESS.md](docs/CHANGE_IMPACT_PROCESS.md) — pre-implementation impact analysis and the no-silent-assumptions policy.
