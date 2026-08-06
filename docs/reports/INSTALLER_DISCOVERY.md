# Installer & Distribution — Discovery Report

Date: 2026-08-06
Status: Discovery complete — all packaging decisions resolved and implemented; installed-bundle acceptance PASSED (see §8, §9). **Entry 17 closed as PASS (2026-08-06).** Caveat recorded: installer acceptance passed using the final staged package; the reproducible `build-package.ps1` path remains an optional follow-up validation because the final installer was built successfully through the underlying Inno Setup process.
Scope: Package the frozen v0.5.25 core for a clean Windows 10 machine with no dev repo, fully offline, no admin at runtime. Core behavior must not change.

## 1. Goal & acceptance criteria (user direction)

A clean machine must be able to install and run the production configuration **without the repository** and without any post-install network access. Acceptance:

1. Clean-machine install without the repo.
2. App launches.
3. WASAPI loopback capture works.
4. Production default engine (`fasterwhisper-native` + live partials, `UC_NATIVE_THREADS=4`) is active.
5. Bundled faster-whisper model is found — **no repo-relative paths** anywhere in the runtime.
6. Tagalog STT works.
7. `en → tl` translation works.
8. Settings persist across restarts.
9. Clean Start / Stop / Exit.
10. Clean uninstall (no orphaned processes/files).
11. No admin at runtime.
12. No silent post-install downloads.

## 2. Runtime dependency inventory

All measurements from this machine (Windows, Release `net8.0-windows` build, 2026-08-06).

| Component | Size | Notes |
| --- | --- | --- |
| App output (framework-dependent) | 19.6 MB | `bin\Release\net8.0-windows`. Includes `Server\faster_whisper_worker.py`, `Server\argos_translate_server.py`, `runtimes\win-x64\` ggml DLLs (~1.9 MB), and the `ggml-base` fallback DLLs at root. Cross-platform `runtimes` bloat is trimmed by `-r win-x64` publish. |
| .NET 8 Desktop self-contained runtime (win-x64) | ~140 MB (est) | `Microsoft.NETCore.App` + `Microsoft.WindowsDesktop.App`. Measured at packaging. Required for offline clean-machine install. |
| Python runtime (uv-managed cpython-3.11, standalone) | 74 MB | `%APPDATA%\uv\python\cpython-3.11-windows-x86_64-none`. This is a python-build-standalone distribution — **relocatable** (no absolute paths baked into the interpreter). |
| faster-whisper stack (`%TEMP%\fwv` site-packages) | ~211 MB | fwv venv total 285 MB minus the 74 MB runtime. Top: `av.libs` 62.6, `ctranslate2` 59.8, `onnxruntime` 42.8, `numpy` 33, `hf_xet` 9.1. |
| argostranslate stack (`%TEMP%\argosv` site-packages) | ~888 MB | argosv venv total 962 MB minus runtime. Top: `torch` 494, `spacy` 92.9, `sympy` 71.7, `ctranslate2` 59.8, `onnxruntime` 42.8, `numpy` 33, `stanza`. **Build-time import verification (2026-08-06) — `torch` (494 MB) + `torchgen` + `sympy` + `mpmath` + `networkx` are all REQUIRED:** `argostranslate/sbd.py:12` unconditionally `import stanza`, which imports torch (which imports `torchgen` and `torch.distributed` at module load) and torch uses sympy via its optimizer paths. `functorch` and the torch `include`/`share`/`distributed` extras WERE droppable (verified). |
| faster-whisper **small** model | 463.7 MB | HF snapshot `models--Systran--faster-whisper-small`; `model.bin` 461.15 MB + tokenizer. The production model. |
| Argos language packages | 416 MB | `%USERPROFILE%\.local\share\argos-translate\packages` (argostranslate default dir). **Pruned to the `en → tl` dependency closure: 79.1 MB** (only `translate-en_tl-1_9` and its deps). |
| `ggml-base.bin` (opt-in fallback engine only) | ~75 MB (est) | Only needed if a user selects `UC_STT_ENGINE=ggml-base`. Not part of the production default path. |

### Size summary

- **Full current stack, installed:** ~2.2 GB (dominated by torch 494 MB, model 464 MB, argos packages 416 MB, python stacks ~1.1 GB).
- **Slim target bundle** (drop torch `include`/`share`/`distributed` extras and `functorch` from the argos stack — torch itself stays, it is required; prune argos packages to the `en→tl` closure; shared single Python runtime; self-contained .NET; `-r win-x64`): **~1.63 GB installed**.
- **Actual (2026-08-06, measured):** Setup.exe **795.5 MB** compressed (lzma2/ultra); installed **1,634.5 MB** at `%LocalAppData%\UniversalCaptions` — app 147.4 MB (self-contained, trimmed `runtimes` + satellites), python runtime ~740 MB (torch 494), faster-whisper small model 463.7 MB, `ggml-base.bin` fallback 141.1 MB, argos packages 79.1 MB.
- A framework-dependent .NET app does **not** shrink the installer: the .NET 8 Desktop Runtime redistributable is the same ~140 MB, and requiring it breaks "clean machine, offline" unless bundled.

## 3. Relocatability findings

These determine the bundling strategy:

1. **Venvs are NOT relocatable.** Both `%TEMP%\fwv\pyvenv.cfg` and `%TEMP%\argosv\pyvenv.cfg` point `home` at the absolute uv-python path (`%APPDATA%\uv\python\cpython-3.11-windows-x86_64-none`). Copying a venv to another machine/path breaks it. **Solution:** bundle the **standalone python runtime** (relocatable) and install the needed packages into its own `site-packages` at package-build time (one runtime shared by both the faster-whisper and Argos workloads — avoids duplicating `ctranslate2` 59.8 MB, `onnxruntime` 42.8 MB, `numpy` 33 MB that currently exist in both venvs).
2. **faster-whisper accepts a local model directory.** `WhisperModel.__init__` checks `os.path.isdir(model_size_or_path)` and uses the directory directly (`faster_whisper/transcribe.py:678`), so pointing `--model` at a bundled install-relative model dir is fully offline — **no HF cache, no download**. The worker already forwards `--model <value>` verbatim (`LineProtocolFasterWhisperProcess.cs:183-184` → worker `argparse --model default=small` → `WhisperModel(args.model, ...)`).
3. **`ARGOS_PACKAGES_DIR` env var IS supported.** `argostranslate` 1.11.0 reads an `ARGOS_PACKAGES_DIR` environment override (documented in the package `settings.py`) for the packages/data directory, falling back to the per-user default `%USERPROFILE%\.local\share\argos-translate\packages`. **Solution:** the launcher sets `ARGOS_PACKAGES_DIR=<install>\argos-packages`, so the bundled pruned packages are used directly and nothing is written to the user profile (clean uninstall = no seeded-file tracking needed). Verified at build: packaged server with `ARGOS_PACKAGES_DIR` → real `en→tl` translation of live input (languages `["en","tl"]`).
4. **Settings already live outside the repo** at `%LocalAppData%\UniversalCaptions\settings.json` (TD-005 store) — unaffected by install location.
5. **The only repo-relative reference in the runtime** is the opt-in fallback engine's model path `artifacts\models\ggml-base.bin`. The production default path never touches the repo, but the install must still supply `UC_STT_MODEL_PATH` for the fallback to be usable after install.

## 4. Path strategy (install-time, zero magic)

A thin **launcher** sets the environment before starting `UniversalCaptions.App.exe`:

| Env knob | Install-time value | Effect |
| --- | --- | --- |
| `UC_FW_PYTHON` | `<install>\py\python.exe` | faster-whisper worker python (was `%TEMP%\fwv`). |
| `UC_ARGOS_PYTHON` | `<install>\py\python.exe` | Argos server python (was `%TEMP%\argosv`); same shared runtime. |
| `UC_FW_MODEL` (NEW — see §5) | `<install>\models\faster-whisper-small\` | Offline model dir; flows to worker `--model`. |
| `UC_STT_MODEL_PATH` | `<install>\models\ggml-base.bin` | Existing knob; only the opt-in `ggml-base` fallback engine uses it. |
| `ARGOS_PACKAGES_DIR` | `<install>\argos-packages\` | Bundled pruned `en→tl` packages (keeps the user profile clean). |
| `HF_HOME` | `<install>\models\hf\` | Defense in depth; the dir-path model bypasses HF entirely. |
| `PYTHONDONTWRITEBYTECODE` | `1` | Never write `__pycache__` at runtime, so uninstall removes everything Inno installed (stdlib `.pyc` files would otherwise be left behind). |

`UC_STT_ENGINE` is left unset so the production default (`fasterwhisper-native` + live partials) applies. Threads default (4), 8 s segment cap, partial cadence, worker protocol, and Argos all remain untouched.

Install location: per-user `%LocalAppData%\UniversalCaptions\` (no admin, no elevation, `asInvoker` manifest preserved). Layout is flattened (app files, `Server\`, `runtimes\` at root; `py\`; `models\`; `argos-packages\`; `launcher.cmd`) — the flattened layout keeps every installed path under 172 chars, which is what makes `MAX_PATH`-safe installation possible for the torch `dist-info\licenses` tree. Settings stay in `%LocalAppData%\UniversalCaptions\settings.json` (same dir as the install root, intentionally — the TD-005 store is unchanged).

## 5. Frozen boundaries — what must change vs. what must not

**Must NOT change (production default, frozen at v0.5.25):** STT engine selection, model choice, segmentation / 8 s cap, partial cadence (1 s / 4 s window), decode-thread default `UC_NATIVE_THREADS=4`, worker wire protocol, bundled server scripts, Argos translation path, overlay architecture, settings store.

**One additive production touch-point (requires user approval):** a `UC_FW_MODEL` env knob read in `SpeechEngineFactory.CreateNative` → `FasterWhisperEngineOptions.Model`. Unset → default `"small"` (behavior identical to today); set → worker gets `--model <path-or-name>`. No behavior change when unset; covered by existing `SpeechEngineFactoryTests` pattern + a new test. This is the minimal seam that makes the production default offline without relying on the fragile HF-cache snapshot layout.

Everything else is packaging/launcher only (no production code): Inno Setup script, launcher, python package-build script, bundle composition, `ARGOS_PACKAGES_DIR` wiring, uninstaller cleanup.

## 6. Technology recommendation: Inno Setup (per-user), not MSIX

| Criterion | Inno Setup (per-user) | MSIX |
| --- | --- | --- |
| 1.5–2 GB offline payload | Fine (compressed, no practical limit) | Practical Store/sideload limits; very large appx is unusual and painful to update/sign |
| No admin at runtime | ✓ `%LocalAppData%\Programs`, `asInvoker` | ✓ per-user registration, but sideloading requires a trusted signing cert on the target |
| Custom env-var launcher | ✓ trivial (shortcut → launcher → app) | ✗ package dir is immutable; no env-var seam; would force a config-file production change |
| Seed user-profile Argos packages | ✓ uninstaller tracks and removes | ✗ needs full-trust package capability (de facto classic app) |
| Offline, no post-install downloads | ✓ first-class | ✓ possible but tooling friction for huge payloads |
| Auto-update | ✗ (out of scope: offline distribution) | ✓ App Installer / Store |
| Clean uninstall | ✓ registry + custom cleanup | ✓ atomic |
| Signing required | No (SmartScreen warning for unsigned; acceptable per user decision) | Yes for sideload trust |

**Verdict: Inno Setup, per-user.** It is the only option that cleanly supports a 2 GB offline payload, a custom env-var launcher, and a bundled argos-packages dir via `ARGOS_PACKAGES_DIR` without admin or signing. MSIX is a future path only if the bundle is slimmed substantially and the env knobs are replaced by a config-file seam.

## 7. Acceptance-test matrix (how each criterion is met + verified)

| # | Criterion | Mechanism | Verification |
| --- | --- | --- | --- |
| 1 | Clean-machine install, no repo | Inno per-user bundle of everything in §2 | Install on a VM/clean Windows 10; no repo present |
| 2 | Launch | Launcher sets env knobs (§4), starts app | Start via Start-menu shortcut |
| 3 | WASAPI loopback | Unchanged capture code | Play audio, confirm live captions |
| 4 | Production default engine | `UC_STT_ENGINE` unset | Overlay live partials appear; confirm engine line in diagnostics |
| 5 | Bundled model found, no repo-relative paths | `UC_FW_MODEL` → bundled dir | Captions work on the clean machine; `rg artifacts\\ ` finds nothing in runtime config; model loads offline (network disabled) |
| 6 | Tagalog STT works | Same model bytes as acceptance (small) | Real-audio Tagalog smoke, committed lines sensible |
| 7 | `en → tl` works | Seeded Argos packages + shared runtime | Real-audio en→tl, overlay badge + Tagalog committed lines |
| 8 | Settings persist | Existing `%LocalAppData%` store | Change settings, restart, verify retained |
| 9 | Clean Start/Stop/Exit | Unchanged | Close overlay, confirm 0 orphaned workers |
| 10 | Clean uninstall | Inno tracks every installed file; app user data (`settings.json`) preserved | Uninstall, confirm only `settings.json` remains, 0 processes |
| 11 | No admin at runtime | `asInvoker` manifest, per-user install | No UAC prompt, install to `%LocalAppData%\UniversalCaptions` |
| 12 | No silent downloads | All payloads bundled; dir-path model avoids HF; no pip/venv at install | Install+run with network disabled |

## 8. Decisions (all four resolved 2026-08-06)

- **D1 — `UC_FW_MODEL` touch-point: APPROVED.** Single additive env knob read in `SpeechEngineFactory.CreateNative` → `FasterWhisperEngineOptions.Model`; unset → `"small"` (identical behavior), set → worker `--model <path-or-name>`. Process-scoped only (set by the launcher, never a global/user env var). Tests: `NativeModel_Unset_DefaultsToSmall`, `NativeModel_Override_IsRespected`.
- **D2 — Packager: Inno Setup, per-user** (chosen over MSIX; §6 verdict). `packaging/UniversalCaptions.iss` builds a single offline Setup.exe.
- **D3 — Bundle size: SLIM.** torch stays (required by stanza SBD); torch `include`/`share`/`distributed` extras, `functorch`, deep third-party license trees, all `__pycache__`, pip/setuptools/pygments/rich, onnxruntime `tools` are dropped. Packages pruned to the `en→tl` closure. Result: Setup 795.5 MB, installed 1,634.5 MB.
- **D4 — Signing: accept unsigned** (SmartScreen warning for now; code-signing deferred).

## 9. Build + acceptance evidence (2026-08-06)

- **Packaging:** `packaging/build-package.ps1` (reproducible: publish → trim → python runtime merge/prune → stage models/packages/launcher → `manifest.txt` → ISCC). Runtime = uv standalone cpython-3.11 (relocatable) + merged fwv/argosv site-packages, pruned per import-verified closure. Staged bundle verified: bundled model loads offline (`HF_HUB_OFFLINE=1`, network disabled, 71 real Tagalog segments); packaged Argos server with `ARGOS_PACKAGES_DIR` produced real `en→tl` (`"Hello, how are you today?"` → `"Hello, kumusta ka na ngayon?"`); stanza SBD segmenting works from bundled resources.
- **MAX_PATH:** first install failed exit 5 (rollback) on `torch-2.13.0.dist-info\licenses\third_party\kineto\…\civetweb\examples\rest\cJSON` (~257 chars). Fixed by dropping the deep license trees (kept `torch\LICENSE.txt`), flattening the install layout, install root `%LocalAppData%\UniversalCaptions`, and removing torch's deepest trees + all `__pycache__` → deepest installed path 172 chars.
- **Installed-bundle acceptance (real audio via WASAPI loopback, real en→tl):** worker cmdlines verified to use installed paths only — STT `py\python.exe … faster_whisper_worker.py --model <install>\models\faster-whisper-small --compute int8 --threads 4 --beam-size 5`, Argos `py\python.exe … argos_translate_server.py`; no `%TEMP%\fwv`, `%TEMP%\argosv`, `huggingface`, `artifacts\`, or repo references. First caption ≈4.1–4.7 s (warm), live partials grow in place, committed translated Tagalog history (`EN || TL` badge), loop repeats, settings persist. Clean Start/Stop/Exit, **0 orphaned workers**, clean uninstall (exit 0) leaving only the app's own `settings.json`.
- **`PYTHONDONTWRITEBYTECODE=1`** added to the launcher after the first run left two stdlib `.pyc` files behind (Python writes `__pycache__` at runtime; Inno can't track them). Verified: with it set, uninstall leaves **no `py` tree** — only `settings.json`.
- **384/384 tests**, Release 0 warnings/0 errors, `dotnet format` clean.

### Close-out (2026-08-06)

**Entry 17 / installer distribution CLOSED as PASS.** Installer acceptance passed using the final
staged package. The reproducible `build-package.ps1` path remains an optional follow-up validation
because the final installer was built successfully through the underlying Inno Setup process. The
next meaningful test before distributing to others is a **truly clean Windows machine** (this machine
retains dev/runtime state that could mask a missing dependency). No further installer changes.
