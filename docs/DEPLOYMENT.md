# Universal Live Captions Deployment

Last updated: 2026-08-21

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define packaging, distribution, and release process for the desktop application |
| Scope | Local development, packaging, and future distribution |
| Audience | Engineering |
| Owner | Engineering |
| Status | Active |
| Related Documents | [ARCHITECTURE.md](ARCHITECTURE.md), [RELEASE_PLAN.md](implementation/RELEASE_PLAN.md), [SECURITY_PLAN.md](SECURITY_PLAN.md), [ADR-0011](adr/ADR-0011-gemini-only-pipeline.md) |

---

## Target

Native Windows 10 (build 17763 / 1809+) desktop application distributed as a Windows application. No server, no database, no cloud infrastructure owned by the app. The app consumes Google's Gemini Live API at runtime (requires internet + user API key).

## Environments

| Environment | Purpose | Notes |
|---|---|---|
| Local dev | `dotnet run` / `dotnet build` | Primary development and test environment |
| Local packaged build | `dotnet publish` | Self-contained output for on-device verification |
| Release | Signed installer (future milestone) | Not in MVP scope |

## Required Services

- **Google Gemini Live API** (`generativelanguage.googleapis.com`) — the only network dependency, used while captions run. The user supplies a free API key stored in Windows Credential Manager.

## Environment Variables

| Variable | Purpose | Example |
|---|---|---|
| `UC_LOG_LEVEL` | Diagnostic verbosity | `Information` |

No engine/model env vars exist (ADR-0011). The production App never reads `UC_GEMINI_API_KEY`.

## Build and Release

- Build: `dotnet build UniversalCaptions.slnx`
- Test: `dotnet test UniversalCaptions.slnx`
- App (overlay + control window): `dotnet run --project src/UniversalCaptions.App`
- Diagnostics: `dotnet run --project src/UniversalCaptions.Diagnostics`

### Installer Strategy (Active)

The release ships **two artifacts** built from the **same staged closure** (single `Stage` tree → both outputs):

| Artifact | Audience | How it's built |
| --- | --- | --- |
| `UniversalCaptions-Setup-{Version}.exe` | **Recommended** — end users | Inno Setup, per-user install to `%LocalAppData%\UniversalCaptions` |
| `UniversalCaptions-{Version}-win-x64.zip` | Portable / advanced users | Extract anywhere, run `UniversalCaptions.App.exe` |

Both ship the same self-contained win-x64 .NET 8 app (~145 MB trimmed publish, measured 2026-08-21). There is no Python runtime, no model files, and no launcher script (ADR-0011). The Setup.exe adds a Start Menu shortcut, optional Desktop shortcut, and a clean uninstall entry; the portable ZIP adds no install steps.

Build with one command:

```powershell
pwsh packaging/build-package.ps1 -Version 0.5.44
```

Stages: publish → trim → manifest → portable ZIP → Inno Setup. See `docs/DEVELOPER_SETUP.md` for switch flags (`-SkipZip`, `-SkipSetup`, `-SkipPublish`). Verify layout with `packaging/inspect-package.ps1`. Signing is deferred (D4 in INSTALLER_DISCOVERY: SmartScreen warning for unsigned installers).

## Database Migrations

Not applicable — no database.

## Observability

MVP: in-memory diagnostics + console output from `UniversalCaptions.Diagnostics`. Latency timestamps recorded per chunk and reported in the control window / diagnostics.

## Rollback Plan

Local desktop app: rollback = restore the previous published output or source revision. No data migration involved.

## Security Checklist

- [ ] No secrets in the repository or published output
- [ ] Dependency scan clean (`dotnet list package --vulnerable`)
- [ ] Privacy model verified: no persistence; single network destination (Gemini endpoint); disclosure present
- [ ] Published output contains only approved binaries
