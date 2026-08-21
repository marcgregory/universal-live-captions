# Developer Setup — Build, Test, and Environment Knobs

Last updated: 2026-08-21

This document is for engineers building Universal Live Captions from source. End users installing the production build should read the [README](../README.md) and the in-app readme instead; nothing on this page is required to run the installed app.

---

## Prerequisites

| Tool | Minimum version | Notes |
| --- | --- | --- |
| .NET 8 SDK | 8.0 | Solution uses `net8.0-windows`; the SDK includes the runtime the App runs on. |
| Git | any | |
| Windows | 10 build 1809+ (64-bit) | WASAPI loopback is the capture path. |
| Gemini API key | free tier OK | For running the app with real captions; stored in Windows Credential Manager under `UniversalCaptions:GeminiApiKey`. |

The .NET 8 SDK installs side-by-side with any existing .NET runtimes and is the only .NET requirement on a dev machine. The installer for end users bundles its own runtime, so a clean consumer machine does not need .NET installed at all.

There are **no Python, Whisper-model, or Argos dependencies** anymore (ADR-0011): speech recognition and translation both run inside a Gemini Live session.

---

## Build, test, run

```bash
dotnet build UniversalCaptions.slnx           # build the whole solution
dotnet test  UniversalCaptions.slnx           # run the full test suite
dotnet run   --project src/UniversalCaptions.Diagnostics   # device-list + live meter smoke
dotnet format --verify-no-changes             # CI formatting gate
dotnet list  UniversalCaptions.slnx package --vulnerable   # vulnerability scan
```

Open `UniversalCaptions.slnx` in your IDE of choice (Rider / VS / VS Code with C# Dev Kit). All test projects are xUnit (`[Fact]` / `[Theory]`); test count is reported by `dotnet test` after each run.

---

## Gemini API key (dev)

The production App reads the key **only** from Windows Credential Manager (target `UniversalCaptions:GeminiApiKey`, per ADR-0009). Set it once from PowerShell:

```powershell
$key = Read-Host "Paste Gemini API key" -AsSecureString
# or use the in-app key panel in the Control Window
```

The legacy `UC_GEMINI_API_KEY` environment variable is **not** consulted by the production App (pinned by `LiveTranslationEngineFactoryTests.Create_Ignores_UC_GEMINI_API_KEY_EnvVar`). The developer spike runner (`tools/GeminiDirectWireSpike`) keeps an env-var path for wire testing only.

---

## Environment-variable reference

| Variable | Default | Purpose |
| --- | --- | --- |
| `UC_LOG_LEVEL` | (unset) | Diagnostic verbosity (e.g. `Information`, `Debug`). |

All former engine knobs (`UC_STT_ENGINE`, `UC_FW_*`, `UC_NATIVE_*`, `UC_ARGOS_*`, `ARGOS_PACKAGES_DIR`, HF offline flags) are gone with the local engines (ADR-0011). Settings persistence covers only UI preferences (schema v3); no engine knobs are persisted or read.

---

## Packaging (installer + portable ZIP)

See [docs/DEPLOYMENT.md](../DEPLOYMENT.md) for the distribution model. The reproducible build is one command:

```powershell
pwsh packaging/build-package.ps1 -Version 0.5.46
```

This produces two artifacts from a single staged closure:

- `packaging/output/UniversalCaptions-Setup-0.5.46.exe` — Inno Setup, per-user install.
- `packaging/output/UniversalCaptions-0.5.46-win-x64.zip` — portable ZIP with the same staged contents (~64 MB ZIP; ~145 MB unpacked).

Switches:

- `-SkipPublish` — reuse existing `Stage\UniversalCaptions`.
- `-SkipSetup` — build the staging only, skip ISCC.
- `-SkipZip` — build staging only, skip the portable ZIP stage.

The script requires Inno Setup 6 installed at `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe` for the Setup.exe stage. The portable ZIP stage uses `[System.IO.Compression.ZipFile]::CreateFromDirectory` and has no external dependency. Verify a package layout with `packaging/inspect-package.ps1`.

---

## Troubleshooting

- **No captions / empty captions**: verify the audio source plays through the selected output device. WASAPI loopback captures *what you hear*, not microphone input.
- **"API key not configured"**: add the key via the Control Window key panel (stored in Credential Manager).
- **Session errors / quota messages**: check network connectivity and your Google AI Studio quota; error classification surfaces user-readable guidance.
- **Installer acceptance failures**: see [docs/reports/INSTALLER_DISCOVERY.md](../reports/INSTALLER_DISCOVERY.md) for historical baselines.

For deeper investigation, see [docs/ARCHITECTURE.md](../ARCHITECTURE.md), the relevant ADRs (0001–0011 in `docs/adr/`), and the latest [docs/implementation/CHANGELOG.md](../implementation/CHANGELOG.md) entry.
