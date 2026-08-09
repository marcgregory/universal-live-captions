# Developer Setup — Build, Test, and Environment Knobs

Last updated: 2026-08-10

This document is for engineers building Universal Live Captions from source. End users installing the production build should read the [README](../README.md) and the in-app readme instead; nothing on this page is required to run the installed app.

---

## Prerequisites

| Tool | Minimum version | Notes |
| --- | --- | --- |
| .NET 8 SDK | 8.0 | Solution uses `net8.0-windows`; the SDK includes the runtime the App runs on. |
| Python | 3.11 | Use a uv-managed standalone CPython (recommended) or system Python 3.11. |
| Git | any | |
| Windows | 10 build 1809+ (64-bit) | WASAPI loopback is the capture path. |

The .NET 8 SDK installs side-by-side with any existing .NET runtimes and is the only .NET requirement on a dev machine. The installer for end users bundles its own runtime, so a clean consumer machine does not need .NET installed at all.

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

## Speech-recognition dev venvs (`%TEMP%\fwv`, `%TEMP%\argosv`)

Speech recognition and (optional) translation run in small Python helper processes. For day-to-day `dotnet run` development, create two short-path venvs under `%TEMP%` (the `MAX_PATH` issue with long venv paths is recorded as **TD-011**; the short 8.3 path is the working solution).

```cmd
py -m venv "%TEMP%\fwv"
"%TEMP%\fwv\Scripts\pip" install faster-whisper openai-whisper torch --index-url https://download.pytorch.org/whl/cpu

py -m venv "%TEMP%\argosv"
"%TEMP%\argosv\Scripts\pip" install argostranslate==1.11.0
```

The App auto-detects these paths during development (see *Resolution chain* below). In the packaged install, the same venvs are not used — the install bundles a single relocatable Python runtime under `<install>\py\` that both workers share.

> **Note:** these dev venvs are NOT used in the installed bundle. They exist only for `dotnet run` development. End users do not need to install Python or create these venvs.

### When to recreate the venvs

- After a `pip` upgrade that broke torch / ctranslate2.
- After upgrading Python.
- When you see `ModuleNotFoundError` for a worker dependency on startup.

To wipe and recreate: delete `%TEMP%\fwv` (or `argosv`) and rerun the `py -m venv` commands above. Stale `__pycache__` inside the venv is regenerated on first use.

---

## Resolution chain — where the App finds Python

`src/UniversalCaptions.App/InstallPathResolver.cs` defines a single resolution chain used by both the faster-whisper worker and the Argos server. The order is:

1. `UC_FW_PYTHON` / `UC_ARGOS_PYTHON` environment variable — set by `packaging/launcher.cmd` for installed launches; also the recommended override for dev.
2. Bundled install: `<install-root>\py\python.exe` (the relocatable CPython runtime staged into the install). Used automatically by portable-ZIP users who run `UniversalCaptions.App.exe` directly without `launcher.cmd`.
3. Legacy dev venv: `%TEMP%\fwv\Scripts\python.exe` / `%TEMP%\argosv\Scripts\python.exe`.
4. `python` on `PATH` as a last resort.

The dev-venv step is preserved deliberately so existing developers with a pre-staged `%TEMP%\fwv` keep working unchanged. Override the chain by setting `UC_FW_PYTHON` (or `UC_ARGOS_PYTHON`) — that wins.

---

## Environment-variable reference

| Variable | Default | Purpose |
| --- | --- | --- |
| `UC_STT_ENGINE` | (unset → `fasterwhisper-native`) | Engine selection. Values: `fasterwhisper-native` (production default + live partials), `ggml-base` (local-Whisper fallback), `fasterwhisper` (windowed faster-whisper, opt-in). See ADR-0008. |
| `UC_STT_MODEL_PATH` | (unset → `artifacts\models\ggml-base.bin`) | Whisper model file for the `ggml-base` engine only. |
| `UC_FW_MODEL` | (unset → `small`) | Bundled faster-whisper model directory. Set by the installer launcher to `<install>\models\faster-whisper-small` for offline use. |
| `UC_FW_PYTHON` | (auto-resolved) | Python interpreter for the faster-whisper worker. See *Resolution chain* above. |
| `UC_NATIVE_THREADS` | `4` | Decode-thread cap for the native engine (clamped to `[1, ProcessorCount]`). Entry 16 CPU optimization. |
| `UC_NATIVE_PARTIAL_INTERVAL` | `1` | Seconds between partial decodes. Set to `0` for FINAL-only behavior. |
| `UC_NATIVE_PARTIAL_WINDOW` | `4` | Trailing audio seconds per partial decode. |
| `UC_NATIVE_MIN_SPEECH` | `0.3` | Minimum speech duration (seconds) before a partial segment can fire. |
| `UC_NATIVE_HANGOVER` | `0.7` | Silence hangover (seconds) before segment close. |
| `UC_NATIVE_MAX_SEGMENT` | `8` | Maximum segment duration (seconds). **Frozen at 8 s — Slice 11 decision.** |
| `UC_STT_WINDOW` | `8` | Windowed-engine audio window (seconds). |
| `UC_STT_INTERVAL` | `0.5` | Windowed-engine decode interval (seconds). |
| `UC_STT_MIN_AUDIO` | `0.5` | Windowed-engine minimum audio before first decode. |
| `UC_STT_STABILITY` | `2` | Streaming committer stability window. Promoted baseline (Slice 6). |
| `UC_ARGOS_PYTHON` | (auto-resolved) | Python interpreter for the Argos server. See *Resolution chain* above. |
| `ARGOS_PACKAGES_DIR` | (unset → `%USERPROFILE%\.local\share\argos-translate\packages`) | Argos package directory. Set by the installer launcher to the bundled `argos-packages` for offline use. |
| `HF_HOME` | (unset) | Optional; the launcher sets it to `<install>\models\hf` for the installed bundle. |
| `HF_HUB_OFFLINE` | (unset → `0`) | The launcher sets this to `1` so the bundled model is used without touching the HuggingFace cache. |
| `TRANSFORMERS_OFFLINE` | (unset → `0`) | The launcher sets this to `1` for the same reason. |
| `PYTHONDONTWRITEBYTECODE` | (unset → `0`) | The launcher sets this to `1` so `__pycache__` is never written into the install dir (clean uninstall). |
| `UC_LOG_LEVEL` | (unset) | Diagnostic verbosity (e.g. `Information`, `Debug`). |

---

## Packaging (installer + portable ZIP)

See [docs/DEPLOYMENT.md](../DEPLOYMENT.md) for the distribution model. The reproducible build is one command:

```powershell
pwsh packaging/build-package.ps1 -Version 0.5.31
```

This produces two artifacts from a single staged closure:

- `packaging/output/UniversalCaptions-Setup-0.5.31.exe` — Inno Setup, per-user, offline install.
- `packaging/output/UniversalCaptions-0.5.31-win-x64-full.zip` — portable ZIP with the same staged contents.

Switches:

- `-SkipPublish` — reuse existing `Stage\UniversalCaptions`.
- `-SkipSetup` — build the staging only, skip ISCC.
- `-SkipZip` — build staging only, skip the portable ZIP stage.

The script requires Inno Setup 6 installed at `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe` for the Setup.exe stage. The portable ZIP stage uses `[System.IO.Compression.ZipFile]::CreateFromDirectory` (built into .NET 8) and has no external dependency.

---

## Troubleshooting

- **`ModuleNotFoundError: faster_whisper`** when running `dotnet run`: the `%TEMP%\fwv` venv is missing or its packages are incomplete. Recreate it (see above).
- **No captions / empty captions**: verify the audio source plays through the selected output device. WASAPI loopback captures *what you hear*, not microphone input.
- **Worker process stays alive after Stop / Exit**: the App stops both workers on shutdown; if you see a stuck python.exe, check that nothing in your test harness is keeping a handle on the worker.
- **STT accuracy is poor for Tagalog**: switch to `UC_STT_ENGINE=fasterwhisper-native` (the production default), then `UC_STT_ENGINE=ggml-base` only as the explicit fallback.
- **Installer acceptance failures**: see [docs/reports/INSTALLER_DISCOVERY.md](../reports/INSTALLER_DISCOVERY.md) §9 for the entry-17 baseline and the v0.5.31 follow-up.

For deeper investigation, see [docs/ARCHITECTURE.md](../ARCHITECTURE.md), the relevant ADR (0001–0009 in `docs/adr/`), and the latest [docs/implementation/CHANGELOG.md](../implementation/CHANGELOG.md) entry.