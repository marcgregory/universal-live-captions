# Universal Live Captions

Chrome-Live-Caption-like live captions for **any** Windows application. Captures system audio via WASAPI loopback (no VB-CABLE required), recognizes speech locally, and renders real-time captions in an always-on-top overlay.

## Product

- Target users: deaf/hard-of-hearing users, language learners, gamers, and anyone who wants captions for any Windows app
- Primary outcome: real-time captions for any Windows application that plays audio
- Features: WASAPI loopback capture (no VB-CABLE), local streaming Whisper speech-to-text, optional local Argos translation, live caption overlay + control window
- Current sprint: Slice 5 — WPF caption overlay + control window (implementation and tests complete; close-out in progress)
- Next milestone: Slice 6 — end-to-end verification (real audio latency/accuracy measurement)

## Documentation Map

- `docs/PRD.md` - product behavior, users, requirements, and acceptance criteria.
- `docs/PROJECT_SCOPE.md` - scope, non-goals, assumptions, risks, and constraints.
- `docs/ARCHITECTURE.md` - system design, boundaries, data flow, state, and privacy.
- `docs/TECH_STACK.md` - selected technologies, tools, packages, and rejected options.
- `docs/SECURITY_PLAN.md` - threat model, privacy model, security controls.
- `docs/DEPLOYMENT.md` - packaging, release process, operations.
- `docs/adr/` - consequential architecture decisions (ADRs 0001-0006).
- `docs/implementation/ROADMAP.md` - what should be built.
- `docs/implementation/BUILD_PLAN.md` - how the active and queued sprints will be built.
- `docs/implementation/PROJECT_STATUS.md` - current project snapshot.
- `docs/implementation/CHANGELOG.md` - versioned history.
- `docs/implementation/TECHNICAL_DEBT.md` - cleanup list.
- `docs/implementation/RELEASE_PLAN.md` - definition of finished.
- `docs/reports/TEST_REPORT.md` - test execution evidence.

## Commands

```bash
dotnet build UniversalCaptions.slnx
dotnet test UniversalCaptions.slnx
dotnet run --project src/UniversalCaptions.App
dotnet run --project src/UniversalCaptions.Diagnostics
dotnet format --verify-no-changes
dotnet list UniversalCaptions.slnx package --vulnerable
```

## Current Status

- Slices 1-4 **complete** (audio capture, streaming Whisper STT, Argos translation, caption service).
- Slice 5 (WPF overlay + control window) **complete** — manual overlay/device verification and real-Argos wiring verified end-to-end (recorded 2026-08-01). Post-close-out refinement (Entry 7: **live active-line translation** + Chrome-style overlay) **closed out 2026-08-01** — **224/224 tests passing**; Tagalog appears on the in-progress overlay line before commit. Next: Slice 6 (end-to-end latency/accuracy).
- Translation runs locally (Argos) and is Off by default; it requires the dev Argos venv (see `docs/TECH_STACK.md`).
- Privacy: no microphone capture, no raw audio persistence, local-first STT and translation.
