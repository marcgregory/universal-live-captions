# Universal Live Captions Deployment

Last updated: 2026-08-01

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define packaging, distribution, and release process for the desktop application |
| Scope | Local development, packaging, and future distribution |
| Audience | Engineering |
| Owner | Engineering |
| Status | Active |
| Related Documents | [ARCHITECTURE.md](ARCHITECTURE.md), [RELEASE_PLAN.md](implementation/RELEASE_PLAN.md), [SECURITY_PLAN.md](SECURITY_PLAN.md) |

---

## Target

Native Windows 10 (build 17763 / 1809+) desktop application distributed as a Windows application. No server, no database, no cloud infrastructure.

## Environments

| Environment | Purpose | Notes |
|---|---|---|
| Local dev | `dotnet run` / `dotnet build` | Primary development and test environment |
| Local packaged build | `dotnet publish` | Self-contained output for on-device verification |
| Release | Signed installer (future milestone) | Not in MVP scope |

## Required Services

None — fully local. The app runs offline; no network endpoints are opened by the MVP.

## Environment Variables

| Variable | Purpose | Example |
|---|---|---|
| `UC_STT_MODEL_PATH` | Override Whisper model directory (Slice 2+) | `C:\models\ggml-base.bin` |
| `UC_LOG_LEVEL` | Diagnostic verbosity | `Information` |

## Build and Release

- Build: `dotnet build UniversalCaptions.slnx`
- Test: `dotnet test UniversalCaptions.slnx`
- App (overlay + control window): `dotnet run --project src/UniversalCaptions.App`
- Diagnostics: `dotnet run --project src/UniversalCaptions.Diagnostics`
- Package: `dotnet publish src/UniversalCaptions.App -c Release -r win-x64 --self-contained true`

### Installer Strategy (Future Milestone)

The MVP ships as a self-contained published folder/executable. A signed installer (MSIX or Inno Setup) and update channel are a post-MVP decision recorded in [ROADMAP.md](implementation/ROADMAP.md) and tracked as a future ADR.

## Database Migrations

Not applicable — no database.

## Observability

MVP: in-memory diagnostics + console output from `UniversalCaptions.Diagnostics`. Latency timestamps recorded per chunk and reported in the control window / diagnostics.

## Rollback Plan

Local desktop app: rollback = restore the previous published output or source revision. No data migration involved.

## Security Checklist

- [ ] No secrets in the repository or published output
- [ ] Dependency scan clean (`dotnet list package --vulnerable`)
- [ ] Privacy model verified: no persistence, no network in MVP
- [ ] Published output contains only approved binaries and models
