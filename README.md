# Universal Live Captions

Chrome-Live-Caption-like live captions for **any** Windows application. Captures system audio via WASAPI loopback (no VB-CABLE required), recognizes speech locally, and renders real-time captions in an always-on-top overlay.

## Product

- Target users: deaf/hard-of-hearing users, language learners, gamers, and anyone who wants captions for any Windows app
- Primary outcome: real-time captions for any Windows application that plays audio
- Current sprint: Slice 1 — Audio Capture Spike (WASAPI loopback → PCM → diagnostic meter)
- Next milestone: Slice 2 — streaming `ISpeechToTextEngine` + local Whisper integration

## Documentation Map

- `docs/PRD.md` - product behavior, users, requirements, and acceptance criteria.
- `docs/PROJECT_SCOPE.md` - scope, non-goals, assumptions, risks, and constraints.
- `docs/ARCHITECTURE.md` - system design, boundaries, data flow, state, and privacy.
- `docs/TECH_STACK.md` - selected technologies, tools, packages, and rejected options.
- `docs/SECURITY_PLAN.md` - threat model, privacy model, security controls.
- `docs/DEPLOYMENT.md` - packaging, release process, operations.
- `docs/adr/` - consequential architecture decisions (ADRs 0001-0005).
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
dotnet run --project src/UniversalCaptions.Diagnostics
dotnet format --verify-no-changes
```

## Current Status

Slice 1 (audio capture spike) in progress. The diagnostics console captures real system audio via WASAPI loopback and renders a live meter — no VB-CABLE required.
