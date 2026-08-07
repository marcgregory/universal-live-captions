# Universal Live Captions Release Plan — v0.5.29

Last updated: 2026-08-07

> **This document is the authoritative release-readiness checklist for v0.5.29.** Per
> [`docs/ARTIFACT_REGISTRY.md`](../ARTIFACT_REGISTRY.md), the *Release decision* concern is
> owned by `RELEASE_PLAN.md` (not `PROJECT_STATUS.md`). `PROJECT_STATUS.md` is the
> current snapshot and cross-references this document for release readiness; it does
> not duplicate the checklist.

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define the v0.5.29 release artifact, the readiness checklist, the unblockers, and the final go/no-go decision |
| Scope | Everything that must be true before v0.5.29 ships: code freeze, installer, landing page, documentation, evidence, and clean-machine verification |
| Audience | Engineering, release engineering, and the operator cutting the GitHub tag |
| Owner | Engineering |
| Status | Active — release in flight |
| Related Documents | [CHANGELOG.md](CHANGELOG.md), [PROJECT_STATUS.md](PROJECT_STATUS.md), [ROADMAP.md](ROADMAP.md), [BUILD_PLAN.md](BUILD_PLAN.md), [TECHNICAL_DEBT.md](TECHNICAL_DEBT.md), [TEST_REPORT.md](../reports/TEST_REPORT.md), [INSTALLER_DISCOVERY.md](../reports/INSTALLER_DISCOVERY.md), [BENCHMARK_REPORT.md](../reports/BENCHMARK_REPORT.md) |

---

## 1. Release Decision

**Decision: NOT READY.**

The v0.5.29 source code, changelog, and project documentation are complete (see §2
and §3 below). The installer pipeline that produced the v0.5.26 acceptance-PASS
bundle is reproducible (see §5). What remains unblocked are two mechanical steps
that live outside the repository:

| Unblocker | Owner | Verifier |
|---|---|---|
| **Cut the v0.5.29 GitHub release tag** with the bundled `UniversalCaptions-Setup-0.5.29.exe` artifact attached, and the v0.5.29 entry from [CHANGELOG.md](CHANGELOG.md) as the release notes | Operator (manual `gh release create` or GitHub UI — no token available in the agent context) | Release tag visible at `https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.29`; installer downloadable from that URL |
| **Re-run the installed-bundle acceptance on a truly clean Windows 10 machine** (no dev/runtime state). The v0.5.26 acceptance already PASS on this machine, but [CHANGELOG.md](CHANGELOG.md) v0.5.26 and [PROJECT_STATUS.md](PROJECT_STATUS.md) record this caveat | Operator + the existing `installer_acceptance.ps1` harness | New `installer_acceptance_cleanbox_*` artifacts; append to this section as the final readiness check |

Once both unblockers are satisfied, this section flips to **READY** and the landing
page Download CTA (see §6) becomes live without risk of a 404.

---

## 2. Release Artifact

| Attribute | Value |
|---|---|
| Version | **v0.5.29** |
| Release date target | TBD (when the §1 unblockers land) |
| Changelog entry | [CHANGELOG.md](CHANGELOG.md) `## v0.5.29 - 2026-08-07` (already committed) |
| Source-tree anchor | Commit `4ca8245` "Close translation & naturalizer investigation: freeze production path, move to release/landing-page work (v0.5.29)" (see `git log --oneline -1`) |
| Installer source | `packaging/UniversalCaptions.iss` (Inno Setup 6.7.3, per-user, no admin, no UAC) |
| Installer builder | `packaging/build-package.ps1` → `packaging/output/UniversalCaptions-Setup-0.5.29.exe` |
| Installer launcher | `packaging/launcher.cmd` (process-scoped env only — no global pollution) |
| Install location | `%LocalAppData%\UniversalCaptions\` (per-user, `asInvoker` manifest preserved from v0.5.26) |
| Installed size (target) | ~1,634 MB (matches v0.5.26 measurement — see [INSTALLER_DISCOVERY.md](../reports/INSTALLER_DISCOVERY.md) §2 size summary) |
| Setup.exe size (target) | ~795.5 MB lzma2/ultra (matches v0.5.26 measurement) |
| Settings store | `%LocalAppData%\UniversalCaptions\settings.json` (TD-005, unchanged from v0.5.26) |

### Bundled contents (target, identical to v0.5.26 baseline)

- App — self-contained .NET 8 win-x64 publish (`-r win-x64`, framework-dependent satellites trimmed) → ~147 MB at `<install>\`.
- Shared Python runtime — uv standalone cpython-3.11 (relocatable) at `<install>\py\python.exe` → ~740 MB (torch 494 MB included; torch is required because `argostranslate/sbd.py` unconditionally `import stanza`, which imports torch at module load — verified at packaging per [INSTALLER_DISCOVERY.md](../reports/INSTALLER_DISCOVERY.md) §2).
- Faster-whisper **small** int8 model — bundled snapshot at `<install>\models\faster-whisper-small\` (worker `--model` points at the install-relative directory; HF cache bypassed entirely).
- `ggml-base.bin` fallback model — bundled at `<install>\models\ggml-base.bin` (used only when user sets `UC_STT_ENGINE=ggml-base`).
- Argos `en→tl` packages — pruned closure, **79.1 MB** at `<install>\argos-packages\` (the only pair the production default needs).
- `UniversalCaptions.App.exe`, `Server\faster_whisper_worker.py`, `Server\argos_translate_server.py`, runtime DLLs at `<install>\`.

### Env-knob wiring (target, identical to v0.5.26 baseline)

| Env knob | Install-time value | Effect |
|---|---|---|
| `UC_FW_PYTHON` | `<install>\py\python.exe` | faster-whisper worker python |
| `UC_ARGOS_PYTHON` | `<install>\py\python.exe` | Argos server python (same shared runtime) |
| `UC_FW_MODEL` | `<install>\models\faster-whisper-small\` | Offline model dir; flows to worker `--model` |
| `UC_STT_MODEL_PATH` | `<install>\models\ggml-base.bin` | Used only by the opt-in `ggml-base` fallback engine |
| `ARGOS_PACKAGES_DIR` | `<install>\argos-packages\` | Bundled pruned `en→tl` packages; user profile stays clean |
| `HF_HOME` + `HF_HUB_OFFLINE=1` + `TRANSFORMERS_OFFLINE=1` | `<install>\models\hf\` (empty) | Defense in depth — dir-path model bypasses HF entirely |
| `PYTHONDONTWRITEBYTECODE` | `1` | No `__pycache__` writes at runtime → uninstall is fully clean |
| `UC_STT_ENGINE` | unset | Production default applies: `fasterwhisper-native` + live partials, `UC_NATIVE_THREADS=4`, 8 s segment cap frozen |

(Per [INSTALLER_DISCOVERY.md](../reports/INSTALLER_DISCOVERY.md) §3–§4. Production-code changes from v0.5.25 → v0.5.29 are documented in [CHANGELOG.md](CHANGELOG.md) and the `UC_FW_MODEL` additive production seam was the only approved touch-point per Entry 17.)

---

## 3. Acceptance-Test Evidence

### 3.1 Final real-world acceptance PASS (2026-08-06, v0.5.25 production default)

Per [PROJECT_STATUS.md](PROJECT_STATUS.md) "Final real-world acceptance" and
[CHANGELOG.md](CHANGELOG.md) v0.5.25. Driven by `acceptance.ps1` (untracked) with the
Release App + VLC + real WASAPI loopback, per-poll CIM CPU sampling, UIA overlay
snapshots, 300 s legs.

| Leg | Content | STT worker CPU | App CPU | First caption | Overlay lines | Exit |
|---|---|---|---|---|---|---|
| 1 | Tagalog, translation OFF (`uc_video_full.m4a`, 288.79 s) | 31.8% mean (max 37.6%) | 0.9% | 3.27 s | max 33 | clean, 0 orphans |
| 2 | English + en→tl (`english_sustained_90s.wav` looped 300 s) | 33.5% mean (max 37.1%) + Argos 4.2% mean (max 21.6%) | 1.3% | 3.23 s | max 54 | clean, 0 orphans |

Overlay evidence: live partials grow in place (`Hello at malugod na tanggapin ang` →
full line), FINALs freeze into bounded history with the `EN || TL` badge, committed
lines are real Tagalog (`Ano ang pangalan mo?`, `Magandang umaga lahat.`), looped
corpus repeats correctly, Stop retains history with no stale partial. Clean
Stop/Exit measured ~5 s (`WM_CLOSE`); harness close budget 25 s.

**Evidence files (untracked):** `acceptance_summary.csv`, `acceptance_tl.csv`,
`acceptance_tl.log`, `acceptance_tl_captions.txt`, `acceptance_en2tl.csv`,
`acceptance_en2tl.log`, `acceptance_en2tl_captions.txt`.

### 3.2 Installed-bundle acceptance PASS (2026-08-06, v0.5.26 installer on this machine)

Per [INSTALLER_DISCOVERY.md](../reports/INSTALLER_DISCOVERY.md) §8–§9 and
[CHANGELOG.md](CHANGELOG.md) v0.5.26. Installed bundle at
`%LocalAppData%\UniversalCaptions`; `launcher.cmd` wires the env knobs in §2;
production default active; real audio via WASAPI loopback; real en→tl; worker
cmdlines are installed-only.

- Install / launch: clean install exit 0; launch via `launcher.cmd`; exe from the installed path.
- Worker paths: `py\python.exe … faster_whisper_worker.py --model <install>\models\faster-whisper-small --compute int8 --threads 4 --beam-size 5`; Argos `py\python.exe … argos_translate_server.py`. **No** `%TEMP%\fwv` / `%TEMP%\argosv` / `huggingface` / `artifacts\` / repo references in any cmdline.
- Captions: first caption ≈4.1–4.7 s (warm); live partials grow in place; committed translated Tagalog history (`EN || TL` badge; real lines e.g. `Ang pangalan ko ay Maria.`, `Ano ang pangalan mo?`); looped corpus repeats; settings persist.
- Lifecycle: clean Start / Stop / Exit; 0 orphaned workers; clean uninstall exit 0 leaving only the app's own `settings.json` (user data preserved; `PYTHONDONTWRITEBYTECODE=1` prevents `.pyc` leftovers). No UAC / admin (`asInvoker`).
- **Caveat (recorded, non-blocking, repeats in §1 unblocker #2):** acceptance passed using the final staged package on this machine; the reproducible `build-package.ps1` path was not separately re-run end-to-end (the underlying Inno Setup process produced the installer successfully). The next meaningful test before distribution is a truly clean Windows machine.

**Evidence files (untracked):** `installer_acceptance.ps1`, `installer_acceptance.log`,
`installer_acceptance.csv`, `installer_acceptance_captions.txt`.

### 3.3 Phase 2 app-by-app validation (2026-08-06, v0.5.26 installer, real apps)

Per [CHANGELOG.md](CHANGELOG.md) v0.5.26 "Verified (Phase 2 — app-by-app validation)".

| App | Result | Evidence |
|---|---|---|
| Chrome / YouTube | **PASS** — local-media first caption ≈2.5 s; YouTube playback first real caption ≈14 s after Start; live partials translate in place; `EN || TL` badge; committed translated Tagalog; clean exit; 0 orphans; worker cmdlines installed-only | `appval_chrome.csv`, `appval_chrome_captions.txt`, `app_validation.ps1` |
| VLC | **PASS** — first caption ≈4.6 s; live partials + committed translated Tagalog; loop repeats; POSTSTOP history retained; clean exit; 0 orphans | `appval_vlc.csv`, `appval_vlc_captions.txt` |
| Zoom | **NOT VALIDATED** (⚠️ environment limitation, NOT a defect). Zoom Workplace 7.0.6 is Chromium-based with no UIAutomation surface; no meeting/account available. Recorded as a known environment limitation; **not** upgraded to PASS | (no per-app evidence — recorded in [CHANGELOG.md](CHANGELOG.md) v0.5.26) |
| Teams | **N/A** (desktop client not installed) | (n/a) |

### 3.4 Test suite

**Full suite: 382/382 passing** (App 95; Audio 77; Captions 72; Speech 111;
Translation 27) per [PROJECT_STATUS.md](PROJECT_STATUS.md) "Last Build". Release
build 0 warnings / 0 errors. `dotnet format --verify-no-changes` clean. No
vulnerable packages.

---

## 4. Landing-Page Status

| Attribute | Value |
|---|---|
| Path | `landing/` (governed top-level; see [PROJECT_CONSTITUTION.md](../PROJECT_CONSTITUTION.md) §1 + [ARTIFACT_REGISTRY.md](../ARTIFACT_REGISTRY.md)) |
| Files | `landing/index.html`, `landing/styles.css`, `landing/script.js`, `landing/assets/capture-demo.webm`, `landing/assets/capture-poster.jpg`, `landing/assets/capture/frame_000..023.jpg` |
| Positioning (four angles, all live on the page) | (1) Offline-first / privacy (trust strip + #why) · (2) Live captions for any Windows app (hero + #how-it-works) · (3) English → Tagalog translation (step 3 + trust strip) · (4) Optional Gemini for higher-quality realtime translation (#gemini card — *Coming soon*; see §7) |
| Version tag | `v0.5.29` (matches this release) |
| CTA target | `https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.29` — see §6 and §1 unblocker #1 |

The page is **ready** as a static asset. Its live publication is gated on the §1
unblockers; the page does not depend on them being satisfied to render correctly.

---

## 5. Download CTA Target

| Attribute | Value |
|---|---|
| Primary CTA (hero, sticky nav, download section) | `https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.29` |
| Fallback (footer "Release notes") | `https://github.com/marcgregory/universal-live-captions/releases` (always resolves to the latest tag) |
| GitHub repo | `https://github.com/marcgregory/universal-live-captions` |

The primary CTA points at the **specific v0.5.29 release tag**, not at the
`/releases` index. This is deliberate: it makes the user-visible release version
match the artifact that ships. Before the tag is cut (per §1 unblocker #1), the
primary CTA will 404 — the page should not be advertised or linked until the tag
exists.

---

## 6. Gemini Optional-Mode Status

**Status: described on the landing page; not yet implemented in the App.**

The App today ships only the offline path: `WASAPI → Whisper (fasterwhisper-native
+ live partials) → Argos OPUS-MT en→tl → 13-rule deterministic naturalizer →
Caption overlay` (see [CHANGELOG.md](CHANGELOG.md) v0.5.29 and
[PROJECT_STATUS.md](PROJECT_STATUS.md) "Current Sprint"). The Gemini Live
Translate option is described on the landing page as a *coming soon* cloud
upgrade that the user would opt into with their own Gemini API key.

What this means for the v0.5.29 release:

- The landing-page Gemini card carries a **"Coming soon — toggle in Settings once
  your API key is added"** line so the not-yet-shipped state is explicit. No
  reader will conclude the App ships Gemini today.
- No production-code path implements the Gemini toggle in v0.5.29. Shipping it
  is a separate piece of work that should be tracked in [ROADMAP.md](ROADMAP.md)
  (Future list) and gated by its own ADR before any implementation begins.
- The API-key setup documentation for the future Gemini path is **not written
  yet**. When the implementation lands, the docs will be owned by a yet-to-be-
  created `docs/USER_GUIDE_GEMINI.md` (cross-reference, not duplicate).

---

## 7. API-Key Setup Documentation

**Status: not yet written.** Required for the (future) Gemini optional mode (§6).

The API-key setup flow will be documented in `docs/USER_GUIDE_GEMINI.md` once the
Gemini path is implemented. Until then, the landing-page Gemini card and the
"coming soon" line are the only user-facing references. **Do not duplicate** that
content elsewhere; cross-reference only.

---

## 8. Demo Checklist

Drives §3.1 / §3.2 / §3.3 acceptance. Each row maps to an existing harness run.

- [x] Release App builds with 0 warnings / 0 errors (Release configuration).
- [x] `dotnet test UniversalCaptions.slnx` passes **382/382**.
- [x] `dotnet format --verify-no-changes` clean.
- [x] `dotnet list UniversalCaptions.slnx package --vulnerable` clean.
- [x] **Final real-world acceptance** (v0.5.25, production default): Tagalog leg PASS (Leg 1) + English + en→tl leg PASS (Leg 2). Per [PROJECT_STATUS.md](PROJECT_STATUS.md).
- [x] **Installed-bundle acceptance** (v0.5.26): install exit 0, launch via `launcher.cmd`, real WASAPI loopback, real en→tl, clean Start / Stop / Exit, 0 orphans, clean uninstall exit 0 (user-data preserved). Per [INSTALLER_DISCOVERY.md](../reports/INSTALLER_DISCOVERY.md) §9.
- [x] **App-by-app validation** (v0.5.26): Chrome / YouTube PASS, VLC PASS. Zoom recorded as environment-limited NOT VALIDATED (no UIA, no meeting). Teams N/A (not installed).
- [ ] **Clean-machine verification** — pending §1 unblocker #2.

---

## 9. Clean-Install Verification

**Goal:** install on a truly clean Windows 10 (build 17763+) machine with no dev /
runtime state, run the App, uninstall, and confirm everything per §8 holds.

| Check | Status |
|---|---|
| Install on this machine (post-dev, retained state) | **PASS** — [INSTALLER_DISCOVERY.md](../reports/INSTALLER_DISCOVERY.md) §9 |
| Install on a truly clean Windows 10 machine | **PENDING** — §1 unblocker #2 |
| Install on Windows 11 (matrix) | **PENDING** — deferred per user; Windows 10 is the documented target |
| Uninstall cleanup (exit 0; only `settings.json` remains) | **PASS on this machine** — same evidence as install |
| No orphaned processes after uninstall | **PASS on this machine** — same evidence as install |

**Why this matters:** this machine retains dev runtime state (`%TEMP%\fwv`,
`%TEMP%\argosv`, `huggingface` cache, `artifacts\` model snapshots) that could mask
a missing bundled dependency. A clean box is the only way to verify the bundle
is self-sufficient.

---

## 10. Performance / Latency Evidence

From §3.1 final real-world acceptance (2026-08-06) at the v0.5.25 production
default — unchanged through v0.5.29 (no perf-sensitive code changed in v0.5.26 /
v0.5.27 / v0.5.28 / v0.5.29).

| Metric | Value | Source |
|---|---|---|
| First visible caption (Tagalog, translation OFF) | **3.27 s** | `acceptance_tl.csv` |
| First visible caption (English, en→tl ON) | **3.23 s** | `acceptance_en2tl.csv` |
| First visible partial (`fasterwhisper-native` + partials ON) | ≈5.59 s after speech onset | [CHANGELOG.md](CHANGELOG.md) v0.5.21 / v0.5.25 |
| Steady-state STT worker CPU (system mean) | 31.8% / 33.5% | §3.1 |
| Steady-state App CPU (system mean) | 0.9% / 1.3% | §3.1 |
| Realtime multiplier (`sttnative` gate) | 1.18× | [CHANGELOG.md](CHANGELOG.md) v0.5.21 / v0.5.24 |
| Decode-thread cap | `UC_NATIVE_THREADS=4` (default) | [CHANGELOG.md](CHANGELOG.md) v0.5.24 |

These numbers match the in-harness evidence and the formal `sttnative` benchmark
gates recorded in [BENCHMARK_REPORT.md](../reports/BENCHMARK_REPORT.md) Slices 11
and 12 / Entry 16.

---

## 11. Known Limitations

Carried into v0.5.29 (all documented; none are regressions introduced by this
release):

- **Argos `tl`-as-source unsupported** (stanza SBD) and `ja→tl` requires an `en`
  pivot (~1050 ms/call). MVP pairs use `tl` as a *target* only. See
  [ADR-0006](../adr/ADR-0006.md).
- **Tagalog `one`-for-`ako` quirks remain.** A handful of en→tl outputs render
  the first-person pronoun as the English word `one`; observed in the production
  default and recorded in [CHANGELOG.md](CHANGELOG.md) v0.5.25 evidence.
- **TD-002 device-change notifications — frozen/Open** until a second WASAPI
  device is available for the real hotplug acceptance test. Contract + production
  wiring complete (2026-08-05); the test itself is the unblocker. See
  [TECHNICAL_DEBT.md](TECHNICAL_DEBT.md).
- **ADR-0007 boundary-preserving fallback — Proposed** until the original
  operator Tagalog recording is supplied. Implementation + unit tests + JFK
  controlled-verification PASS are recorded; the original Tagalog recording is
  the final acceptance evidence. Per user direction, no substitute sample
  qualifies. See [ADR-0007](../adr/ADR-0007.md).
- **Gemini optional mode — not implemented in v0.5.29.** Described on the
  landing page as *coming soon*. See §6.
- **Zoom Workplace validation — NOT VALIDATED (environment-limited).** Recorded
  in [CHANGELOG.md](CHANGELOG.md) v0.5.26. The WASAPI capture path is identical
  to the VLC / Chrome legs (both PASS).
- **Installer reproducible build path (`build-package.ps1` end-to-end) — not
  re-run** since the v0.5.26 acceptance. Recorded in [CHANGELOG.md](CHANGELOG.md)
  v0.5.26 caveat; non-blocking.
- **Phase 2 real-app validation reassessment — deferred per user.** YouTube /
  Chrome / VLC / Zoom beyond the v0.5.26 evidence above is not re-run for
  v0.5.29.

---

## 12. Final Release Checklist

Single signed-off list. Status legend: **DONE** = complete with evidence · **PENDING** = unblocker named · **N/A** = not in scope for this release.

| # | Item | Status | Evidence / Owner |
|---|---|---|---|
| 1 | Source tree frozen at v0.5.29 | DONE | Commit `4ca8245`; [CHANGELOG.md](CHANGELOG.md) v0.5.29 |
| 2 | All tests passing | DONE | 382/382 ([PROJECT_STATUS.md](PROJECT_STATUS.md) "Last Build") |
| 3 | Release build 0 warnings / 0 errors | DONE | same |
| 4 | `dotnet format --verify-no-changes` clean | DONE | same |
| 5 | No vulnerable packages | DONE | same |
| 6 | Final real-world acceptance PASS | DONE | §3.1 |
| 7 | Installed-bundle acceptance PASS | DONE | §3.2 |
| 8 | Phase 2 app-by-app validation (Chrome / VLC) | DONE | §3.3 |
| 9 | Phase 2 Zoom validation | N/A (env-limited, recorded) | §3.3 |
| 10 | Landing page ready as static asset | DONE | §4 |
| 11 | Landing-page CTA points at v0.5.29 release tag | DONE (link target set) | §5 |
| 12 | Landing-page Gemini section honest (no over-promise) | DONE (*Coming soon* line) | §6 |
| 13 | Documentation updated (CHANGELOG, PROJECT_STATUS, this RELEASE_PLAN) | DONE | [CHANGELOG.md](CHANGELOG.md), [PROJECT_STATUS.md](PROJECT_STATUS.md), this file |
| 14 | Constitution + Registry alignment (`landing/`, `packaging/`, `artifacts/` governed) | DONE | [PROJECT_CONSTITUTION.md](../PROJECT_CONSTITUTION.md) §1, [ARTIFACT_REGISTRY.md](../ARTIFACT_REGISTRY.md) |
| 15 | Clean-machine install verification | PENDING | §1 unblocker #2 |
| 16 | Cut v0.5.29 GitHub release tag | PENDING | §1 unblocker #1 |
| 17 | Decision: **READY** | PENDING | Flips when #15 and #16 are done |

When #15 and #16 close, this document is the single source of truth that the
release is Ready. The landing page becomes public; no other artifact needs to
change.