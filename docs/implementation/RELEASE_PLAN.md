# Universal Live Captions Release Plan — v0.5.39

Last updated: 2026-08-13

> **This document is the authoritative release-readiness checklist.** Per
> [`docs/ARTIFACT_REGISTRY.md`](../ARTIFACT_REGISTRY.md), the *Release decision* concern is
> owned by `RELEASE_PLAN.md` (not `PROJECT_STATUS.md`). `PROJECT_STATUS.md` is the
> current snapshot and cross-references this document for release readiness; it does
> not duplicate the checklist.

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define the v0.5.39 release artifact, the readiness checklist, the unblockers, and the final go/no-go decision |
| Scope | Everything that must be true before v0.5.39 ships: code freeze, installer, landing page, documentation, evidence, and clean-machine verification |
| Audience | Engineering, release engineering, and the operator cutting the GitHub tag |
| Owner | Engineering |
| Status | Active — v0.5.39 close-out 2026-08-13 (awaiting tag cut) |
| Related Documents | [CHANGELOG.md](CHANGELOG.md), [PROJECT_STATUS.md](PROJECT_STATUS.md), [ROADMAP.md](ROADMAP.md), [BUILD_PLAN.md](BUILD_PLAN.md), [TECHNICAL_DEBT.md](TECHNICAL_DEBT.md), [TEST_REPORT.md](../reports/TEST_REPORT.md), [INSTALLER_DISCOVERY.md](../reports/INSTALLER_DISCOVERY.md), [BENCHMARK_REPORT.md](../reports/BENCHMARK_REPORT.md) |

---

## 1. Release Decision

**Decision: READY — v0.5.39 close-out 2026-08-13 (awaiting tag cut and GitHub release).**

| Release | Tag | GitHub Release | Artifacts |
|---|---|---|---|
| **v0.5.33** — Gemini Live translation (parity acceptance) | `v0.5.33` (commit `d7333c1`) | [releases/tag/v0.5.33](https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.33) | `UniversalCaptions-Setup-0.5.33.exe` + `UniversalCaptions-0.5.33-win-x64-full.zip` |
| **v0.5.34** — Gemini API-key onboarding link | `v0.5.34` (commit `b50cc2a`) | [releases/tag/v0.5.34](https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.34) | `UniversalCaptions-Setup-0.5.34.exe` + `UniversalCaptions-0.5.34-win-x64-full.zip` |
| **v0.5.37** — Mixed-language history scrub on Translate OFF + target change | `v0.5.37` (commit `8b5dd53`) | [releases/tag/v0.5.37](https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.37) | `UniversalCaptions-Setup-0.5.37.exe` + `UniversalCaptions-0.5.37-win-x64-full.zip` |
| **v0.5.38** — Stable/unstable partial rendering | `v0.5.38` (commit `95f5049`) | [releases/tag/v0.5.38](https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.38) | `UniversalCaptions-Setup-0.5.38.exe` + `UniversalCaptions-0.5.38-win-x64-full.zip` |
| **v0.5.39** — Gemini goAway session-lifecycle fix (**Latest**) | `v0.5.39` (pending tag cut) | (pending) | `UniversalCaptions-Setup-0.5.39.exe` + `UniversalCaptions-0.5.39-win-x64-full.zip` |

- **v0.5.32 was intentionally not published.** It was an internal build milestone (built artifacts existed, no tag, no GitHub release) whose design was corrected by v0.5.33. The public release history is `v0.5.31 → v0.5.33 → v0.5.34 → v0.5.37 → v0.5.38 → v0.5.39`.
- **v0.5.35 and v0.5.36 are internal/measurement-only releases** (no tag, no GitHub release): v0.5.35 = runtime Gemini-toggle latency verification PASS (measurement only), v0.5.36 = Gemini `goAway` fix spike worktree (proved the fix; not published — the validated production fix ships as v0.5.39).
- Both artifacts for each release ship the **same staged closure** (single `Stage` tree → both outputs), per `packaging/build-package.ps1` v0.5.31+.
- Landing page (`landing/`) needs to be updated to v0.5.39 once the tag is cut.

---

## 2. Release Artifact

| Attribute | Value |
|---|---|
| Version | **v0.5.39** (see §1) |
| Release date | **2026-08-13** (close-out; tag cut pending) |
| Changelog entries | [CHANGELOG.md](CHANGELOG.md) `## v0.5.39 - 2026-08-13` |
| Source-tree anchors | v0.5.39 = commit `5ae30bc` (Gemini goAway session-lifecycle fix) |
| Installer source | `packaging/UniversalCaptions.iss` (Inno Setup 6.7.3, per-user, no admin, no UAC) |
| Installer builder | `packaging/build-package.ps1` → `packaging/output/UniversalCaptions-Setup-0.5.38.exe` |
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

(Per [INSTALLER_DISCOVERY.md](../reports/INSTALLER_DISCOVERY.md) §3–§4. Production-code changes from v0.5.25 onward are documented in [CHANGELOG.md](CHANGELOG.md) and the `UC_FW_MODEL` additive production seam was the only approved touch-point per Entry 17. The staged closure is unchanged between v0.5.31 and v0.5.34.)

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

**Full suite: 642/642 passing** (106 Audio + 89 Captions + 111 Speech + 42
Translation + 182 App + 112 Speech.Gemini) per [PROJECT_STATUS.md](PROJECT_STATUS.md)
"Last Build". Release build 0 warnings / 0 errors. `dotnet format --verify-no-changes`
clean. No vulnerable packages (the documented `dotnet list … --vulnerable` check;
transitive test-SDK `System.*` 4.3.0 advisories are a known false positive in test
projects only, not shipped).

### 3.5 v0.5.33 final real-world acceptance — 22/22 PASS on live WASAPI loopback (2026-08-12)

Per [CHANGELOG.md](CHANGELOG.md) v0.5.33. Harness
`acceptance-v0.5.33-translation-parity.ps1` drives the Release app + real WASAPI
loopback (looped `english_sustained_90s.wav`) through the full control surface in
one session, then flips the provider (Argos → Gemini) and repeats. Per provider,
while captions are RUNNING: Translate OFF → a new source-English caption appears,
control toggle reads off, Whisper keeps capturing; Translate ON → target language
returns; target `tl → ja → tl` updates immediately with no Stop/Start; STT worker
PIDs stay constant across every toggle/target change. **Argos 11/11 + Gemini 11/11
= 22/22.**

**Evidence files (committed):** `acceptance-v0.5.33-translation-parity.ps1`,
`v0533_parity_acceptance.log` (real CJK verified in-file).

### 3.6 v0.5.37 mixed-language history scrub smoke PASS (2026-08-13)

Per [CHANGELOG.md](CHANGELOG.md) v0.5.37. Release app + WASAPI loopback + Gemini
provider, single in-session sequence with no Stop/Start between transitions:

1. Translate OFF + English source captions visible (initial state — English STT
   accumulated history).
2. Translate ON → Tagalog → Tagalog captions appear.
3. Target switch `tl → ja` → previous Tagalog history cleared, new JA session
   starts (verified: Japanese captions appear, no Tagalog residue).
4. Translate OFF → committed Tagalog/Japanese history cleared; **English SourceStt
   history preserved** (the English captions that re-appear are the same STT output
   that was being captured while translation was ON — they are preserved
   `LineOrigin.SourceStt`, not retranslated/reprocessed versions of the old TL/JA
   lines).

No code change beyond the v0.5.37 caption-service API + state additions described
in [CHANGELOG.md](CHANGELOG.md) v0.5.37. Visual evidence captured in-session
(`v0537_mixed_history_smoke_*.png`).

### 3.7 v0.5.38 stable/unstable partial rendering smoke PASS (2026-08-13)

Per [CHANGELOG.md](CHANGELOG.md) v0.5.38. Release app + WASAPI loopback, live
partials, translation OFF. Two-tone evidence (stable white head + unstable green
tail on the same caption line) captured live with the config-only knob
`UC_NATIVE_PARTIAL_WINDOW=8` (the production default 4 s window rolls —
`SpeechSegmentDetector.TryGetPartial` snapshots the trailing window — so at 4 s
consecutive partials rarely share a displayed prefix and the two-tone is not
visible; at 8 s the head stays anchored). Verified sequence: first partial all
green → extension white head + green tail → head-revision whole line re-greens →
FINAL freeze all-white → Stop green 0.

**Evidence (untracked, kept locally):** `smoke_v0538_twotone_evidence.png`
(two-tone in-shot), `smoke-v0538.ps1` (harness with `-PartialWindow` param),
`smoke_v0538_captions_v0538c.txt` / `smoke_v0538_shots_v0538c/` (run evidence).

### 3.8 v0.5.39 Gemini goAway session-lifecycle regression PASS (2026-08-13)

Per [CHANGELOG.md](CHANGELOG.md) v0.5.39. Release app + real WASAPI loopback on
the default device + Gemini en→tl, `behavioral-interview.wav` looped, production
defaults, **no trace plumbing** (the v0.5.36 spike instrumentation is excluded from
the artifact). Full lifecycle driven in one session: start → first translated
Tagalog caption → natural Gemini `goAway` at ~9 min → failure status reaches the
Control Window → overlay stable (engine detached, no lines growing) → toggle
translation OFF then ON → **new Gemini session** produces translated captions again
→ status recovers to "Capturing system audio.". 6/6 checks PASS.

The two discriminating observables that prove the fix:
1. **Natural goAway surfaced as a failure status** — pre-fix the engine exited the
   receive loop silently, so the Control Window stayed on "Capturing system audio."
   forever while the overlay froze on the last translated sentence.
2. **Session lifecycle restoration** — OFF→ON after a natural goAway starts a fresh
   Gemini session (new sequence from `FRAG init`, seq reset) that produces captions.

**Evidence (untracked, kept locally):** `regression-v0539-goaway.ps1` (harness),
`regression_v0539_goaway.log` (check log), `regression_v0539_goaway_app_stderr.log`
(stderr; `[FW-DIAG]` decode rows continue after goAway — Whisper keeps running).

---

## 4. Landing-Page Status

| Attribute | Value |
|---|---|
| Path | `landing/` (governed top-level; see [PROJECT_CONSTITUTION.md](../PROJECT_CONSTITUTION.md) §1 + [ARTIFACT_REGISTRY.md](../ARTIFACT_REGISTRY.md)) |
| Files | `landing/index.html`, `landing/styles.css`, `landing/script.js`, `landing/assets/capture-demo.webm`, `landing/assets/capture-poster.jpg`, `landing/assets/capture/frame_000..023.jpg` |
| Positioning (four angles, all live on the page) | (1) Offline-first / privacy (trust strip + #why) · (2) Live captions for any Windows app (hero + #how-it-works) · (3) English → Tagalog translation (step 3 + trust strip) · (4) Optional Gemini for higher-quality realtime translation (#gemini card — **OPTIONAL CLOUD UPGRADE**, shipped since v0.5.33; see §6) |
| Version tag | `v0.5.39` (matches the latest release; landing update pending tag cut) |
| CTA target | `https://github.com/marcgregory/universal-live-captions/releases/download/v0.5.39/UniversalCaptions-Setup-0.5.39.exe` — see §5 |

The page is **live** on GitHub Pages (legacy build from `main` root); the v0.5.39
download link becomes active once the v0.5.39 GitHub release is created.

---

## 5. Download CTA Target

| Attribute | Value |
|---|---|
| Primary CTA (hero, sticky nav, download section) | `https://github.com/marcgregory/universal-live-captions/releases/download/v0.5.39/UniversalCaptions-Setup-0.5.39.exe` |
| Portable ZIP link | `https://github.com/marcgregory/universal-live-captions/releases/download/v0.5.39/UniversalCaptions-0.5.39-win-x64-full.zip` |
| Fallback (footer "Release notes") | `https://github.com/marcgregory/universal-live-captions/releases` (always resolves to the latest tag) |
| GitHub repo | `https://github.com/marcgregory/universal-live-captions` |

The primary CTA points at the **specific v0.5.39 release tag**, not at the
`/releases` index. This is deliberate: it makes the user-visible release version
match the artifact that ships.

---

## 6. Gemini Optional-Mode Status

**Status: shipped (v0.5.33), available as an opt-in cloud upgrade.**

The App's default path remains fully offline: `WASAPI → Whisper
(fasterwhisper-native + live partials) → Argos OPUS-MT en→tl → Naturalizer →
Caption overlay`. Users can additionally opt in to Gemini Live translation
(`gemini-3.5-live-translate-preview`) by adding their own Gemini API key in the
control window (Windows Credential Manager, ADR-0009) and selecting **"Gemini
(cloud)"** as the translation provider. Runtime toggle + target-language changes
apply live while captions run; Argos and Gemini now behave identically from the
user's perspective (v0.5.33 parity, §3.5). While Gemini mode is active,
translation audio leaves the machine to Google's API; offline local translation
remains the default and requires no account.

---

## 7. API-Key Setup Documentation

**Status: written and available to end users.**

The Gemini API-key section in the control window shows a **"Get your API key from
Google AI Studio ↗"** link (v0.5.34) that opens Google's official key page. Keys
are stored in Windows Credential Manager (never embedded/hard-coded; ADR-0009) and
managed via the Add / Update / Remove flow in the control window. Engineering
detail lives in [CHANGELOG.md](CHANGELOG.md) v0.5.32 / v0.5.33 / v0.5.34 and
[ADR-0009](../adr/ADR-0009.md).

---

## 8. Demo Checklist

Drives §3.1 / §3.2 / §3.3 acceptance. Each row maps to an existing harness run.

- [x] Release App builds with 0 warnings / 0 errors (Release configuration).
- [x] `dotnet test UniversalCaptions.slnx` passes **642/642**.
- [x] `dotnet format --verify-no-changes` clean.
- [x] `dotnet list UniversalCaptions.slnx package --vulnerable` clean.
- [x] **Final real-world acceptance** (v0.5.25, production default): Tagalog leg PASS (Leg 1) + English + en→tl leg PASS (Leg 2). Per [PROJECT_STATUS.md](PROJECT_STATUS.md).
- [x] **v0.5.33 parity acceptance** (Argos + Gemini, 22/22 live-WASAPI checks). Per [CHANGELOG.md](CHANGELOG.md) v0.5.33 / §3.5.
- [x] **v0.5.37 mixed-language history scrub smoke** (in-session, no Stop/Start). Per [CHANGELOG.md](CHANGELOG.md) v0.5.37 / §3.6.
- [x] **v0.5.38 stable/unstable partial rendering smoke** (two-tone real-app evidence). Per [CHANGELOG.md](CHANGELOG.md) v0.5.38 / §3.7.
- [x] **Installed-bundle acceptance** (v0.5.26): install exit 0, launch via `launcher.cmd`, real WASAPI loopback, real en→tl, clean Start / Stop / Exit, 0 orphans, clean uninstall exit 0 (user-data preserved). Per [INSTALLER_DISCOVERY.md](../reports/INSTALLER_DISCOVERY.md) §9.
- [x] **App-by-app validation** (v0.5.26): Chrome / YouTube PASS, VLC PASS. Zoom recorded as environment-limited NOT VALIDATED (no UIA, no meeting). Teams N/A (not installed).
- [ ] **Clean-machine verification** — recorded as an ongoing follow-up (not a blocker to publishing; see §9).

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
default — unchanged through v0.5.34 (no perf-sensitive code changed in v0.5.26
onward).

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

Carried into v0.5.38 (all documented; none are regressions introduced by these
releases):

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
- **Gemini optional mode requires the user's own API key and a network
  connection while active.** Offline/local translation remains the default and
  requires no account. See §6.
- **Zoom Workplace validation — NOT VALIDATED (environment-limited).** Recorded
  in [CHANGELOG.md](CHANGELOG.md) v0.5.26. The WASAPI capture path is identical
  to the VLC / Chrome legs (both PASS).
- **Clean-machine install verification — not yet performed** (ongoing follow-up;
  not a blocker to publishing). Recorded in §9 and [CHANGELOG.md](CHANGELOG.md)
  v0.5.26 caveat.
- **Phase 2 real-app validation reassessment — deferred per user.** YouTube /
  Chrome / VLC / Zoom beyond the v0.5.26 evidence above is not re-run for these
  releases.

---

## 12. Final Release Checklist

Single signed-off list. Status legend: **DONE** = complete with evidence · **PENDING** = unblocker named · **N/A** = not in scope for this release.

| # | Item | Status | Evidence / Owner |
|---|---|---|---|
| 1 | Source tree frozen at v0.5.39 | DONE (commit `5ae30bc`) | [CHANGELOG.md](CHANGELOG.md) v0.5.39 |
| 2 | All tests passing | DONE | 645/645 ([PROJECT_STATUS.md](PROJECT_STATUS.md) "Last Build") |
| 3 | Release build 0 warnings / 0 errors | DONE | same |
| 4 | `dotnet format --verify-no-changes` clean | DONE | same |
| 5 | No vulnerable packages | DONE | same |
| 6 | Final real-world acceptance PASS (v0.5.33 parity 22/22) | DONE | §3.5 |
| 7 | v0.5.37 mixed-language history scrub smoke PASS | DONE | §3.6 |
| 8 | v0.5.38 stable/unstable partial rendering smoke PASS | DONE | §3.7 |
| 9 | v0.5.39 Gemini goAway session-lifecycle regression PASS | DONE | §3.8 |
| 9 | Installed-bundle acceptance PASS (v0.5.26 baseline) | DONE | §3.2 |
| 10 | Phase 2 app-by-app validation (Chrome / VLC) | DONE | §3.3 |
| 11 | Phase 2 Zoom validation | N/A (env-limited, recorded) | §3.3 |
| 12 | Landing page live + points at v0.5.39 assets | PENDING (landing update on tag cut) | §4, §5 |
| 13 | Landing-page Gemini section honest (OPTIONAL cloud upgrade, shipped v0.5.33) | DONE | §6 |
| 14 | v0.5.33 GitHub release created with artifacts | DONE | [releases/tag/v0.5.33](https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.33) |
| 15 | v0.5.34 GitHub release created with artifacts | DONE | [releases/tag/v0.5.34](https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.34) |
| 16 | v0.5.37 GitHub release created with artifacts | DONE | [releases/tag/v0.5.37](https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.37) |
| 17 | v0.5.38 GitHub release created with artifacts | DONE | [releases/tag/v0.5.38](https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.38) |
| 17a | v0.5.39 GitHub release created with artifacts (**Latest**) | PENDING (tag cut pending) | (pending) |
| 18 | v0.5.32 intentionally not published (internal milestone, corrected by v0.5.33) | DONE (recorded) | §1 |
| 19 | v0.5.35 / v0.5.36 internal/measurement-only (no tag, no release) | DONE (recorded) | §1 |
| 20 | Documentation updated (CHANGELOG, PROJECT_STATUS, TEST_REPORT, this RELEASE_PLAN) | DONE | [CHANGELOG.md](CHANGELOG.md), [PROJECT_STATUS.md](PROJECT_STATUS.md), [TEST_REPORT.md](../reports/TEST_REPORT.md), this file |
| 21 | Constitution + Registry alignment (`landing/`, `packaging/`, `artifacts/` governed) | DONE | [PROJECT_CONSTITUTION.md](../PROJECT_CONSTITUTION.md) §1, [ARTIFACT_REGISTRY.md](../ARTIFACT_REGISTRY.md) |
| 22 | Decision: **READY** | **DONE** | §1 |

This document is the single source of truth that the release is Ready. v0.5.39
documentation is in place; tag cut and GitHub release creation remain pending.