# Universal Live Captions Deployment

Last updated: 2026-08-10

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

### Installer Strategy (Active as of v0.5.31)

Starting with v0.5.31, the release ships **two artifacts** built from the **same staged closure** (single `Stage` tree → both outputs):

| Artifact | Audience | How it's built |
| --- | --- | --- |
| `UniversalCaptions-Setup-{Version}.exe` | **Recommended** — end users | Inno Setup, per-user, offline install to `%LocalAppData%\UniversalCaptions` |
| `UniversalCaptions-{Version}-win-x64-full.zip` | Portable / advanced users | Extract anywhere, run `launcher.cmd` |

Both ship the same self-contained win-x64 .NET 8 app, the relocatable Python runtime, the bundled faster-whisper `small` model, the pruned Argos `en→tl` packages, and the launcher. The Setup.exe adds a Start Menu shortcut, optional Desktop shortcut, and a clean uninstall entry; the portable ZIP adds no install steps.

Build with one command:

```powershell
pwsh packaging/build-package.ps1 -Version 0.5.31
```

This runs the seven reproducible stages (publish → trim → python runtime → stage models/Argos → manifest → portable ZIP → Inno Setup). See `docs/DEVELOPER_SETUP.md` for switch flags (`-SkipZip`, `-SkipSetup`, `-SkipPublish`). Signing is deferred (D4 in INSTALLER_DISCOVERY: SmartScreen warning for unsigned installers).

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
