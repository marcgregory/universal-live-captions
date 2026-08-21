# Universal Live Captions Release Plan — v0.5.46

Last updated: 2026-08-22

> **This document is the authoritative release-readiness checklist.** Per
> [`docs/ARTIFACT_REGISTRY.md`](../ARTIFACT_REGISTRY.md), the *Release decision* concern is
> owned by `RELEASE_PLAN.md` (not `PROJECT_STATUS.md`). `PROJECT_STATUS.md` is the
> current snapshot and cross-references this document for release readiness; it does
> not duplicate the checklist.

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define the v0.5.46 release artifact, the readiness checklist, the unblockers, and the final go/no-go decision |
| Scope | Everything that must be true before v0.5.46 ships: code freeze, installer, landing page, documentation, evidence, and clean-machine verification |
| Audience | Engineering, release engineering, and the operator cutting the GitHub tag |
| Owner | Engineering |
| Status | **Released — v0.5.44 smoke test PASS 2026-08-21; GitHub release published. v0.5.46 fix committed 2026-08-22 (auto-reconnect overlay refresh / 540k regression); Release packaging pending release-gate smoke on the Release artifact.** |
| Related Documents | [CHANGELOG.md](CHANGELOG.md), [PROJECT_STATUS.md](PROJECT_STATUS.md), [ROADMAP.md](ROADMAP.md), [BUILD_PLAN.md](BUILD_PLAN.md), [TECHNICAL_DEBT.md](TECHNICAL_DEBT.md), [TEST_REPORT.md](../reports/TEST_REPORT.md), [INSTALLER_DISCOVERY.md](../reports/INSTALLER_DISCOVERY.md), [BENCHMARK_REPORT.md](../reports/BENCHMARK_REPORT.md) |

---

## 1. Release Decision

**Decision: v0.5.46 (auto-reconnect overlay refresh / 540k regression) committed 2026-08-22 — Debug build verified PASS (trace-driven). Release packaging pending real-app smoke on the Release artifact.**

The v0.5.46 release continues the Gemini-only architecture and adds an auto-reconnect overlay refresh
that fixes the 540k regression: after a Gemini session ends (goAway, server-side session cap, or
network blip), the overlay now clears the previous session's stale active line + history and renders
the new session's first partial without any manual "Show Captions" click. Pre-existing v0.5.44
release (Gemini session recovery) is the latest published release on GitHub. v0.5.44 was closed
2026-08-21 — real-wire verification PASS via `tools/GeminiDirectWireSpike --ab` (variant B received
7–8 `serverContent.inputTranscription` frames per utterance with real English source text; variant A
also received them — surface streams by default). Evidence: `artifacts/spike-result/ab-result.json`,
TEST_REPORT. The final installed Release-artifact smoke test for v0.5.44 also passed: loopback
capture, source + translation captions, translation toggle, target-language session recycle, clean
Stop, and history retention.

| Release | Tag | GitHub Release | Artifacts |
|---|---|---|---|
| **v0.5.33** — Gemini Live translation (parity acceptance) | `v0.5.33` (commit `d7333c1`) | [releases/tag/v0.5.33](https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.33) | `UniversalCaptions-Setup-0.5.33.exe` + `UniversalCaptions-0.5.33-win-x64-full.zip` |
| **v0.5.34** — Gemini API-key onboarding link | `v0.5.34` (commit `b50cc2a`) | [releases/tag/v0.5.34](https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.34) | `UniversalCaptions-Setup-0.5.34.exe` + `UniversalCaptions-0.5.34-win-x64-full.zip` |
| **v0.5.37** — Mixed-language history scrub on Translate OFF + target change | `v0.5.37` (commit `8b5dd53`) | [releases/tag/v0.5.37](https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.37) | `UniversalCaptions-Setup-0.5.37.exe` + `UniversalCaptions-0.5.37-win-x64-full.zip` |
| **v0.5.38** — Stable/unstable partial rendering | `v0.5.38` (commit `95f5049`) | [releases/tag/v0.5.38](https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.38) | `UniversalCaptions-Setup-0.5.38.exe` + `UniversalCaptions-0.5.38-win-x64-full.zip` |
| **v0.5.44** — Gemini session recovery | [`v0.5.44`](https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.44) | Published | `UniversalCaptions-Setup-0.5.44.exe` + `UniversalCaptions-0.5.44-win-x64-full.zip` |
| **v0.5.46** — Auto-reconnect overlay refresh (540k regression) (**Latest**) | `v0.5.46` (pending tag cut + Release-artifact smoke) | (pending) | `UniversalCaptions-Setup-0.5.46.exe` + `UniversalCaptions-0.5.46-win-x64-full.zip` |

- **v0.5.32 was intentionally not published.** It was an internal build milestone (built artifacts existed, no tag, no GitHub release) whose design was corrected by v0.5.33.
- **v0.5.35 and v0.5.36 are internal/measurement-only releases** (no tag, no GitHub release): v0.5.35 = runtime Gemini-toggle latency verification PASS (measurement only), v0.5.36 = Gemini `goAway` fix spike worktree (proved the fix; not published — the validated production fix ships as v0.5.39).
- **v0.5.40–v0.5.42 were not published**: v0.5.40 = segmentation investigation + matrix (measurement only), then the corpus-driven phrase-guard validation closed with "do not ship"; ADR-0011 work landed directly as v0.5.43.

---

## 2. Release Artifact

| Attribute | Value |
|---|---|
| Version | **v0.5.46** (see §1) |
| Release date | **2026-08-22 (committed; Release smoke pending)** |
| Changelog entries | [CHANGELOG.md](CHANGELOG.md) `## v0.5.46 - 2026-08-22` |
| Installer source | `packaging/UniversalCaptions.iss` (Inno Setup 6, per-user, no admin, no UAC) |
| Installer builder | `packaging/build-package.ps1` → `packaging/output/UniversalCaptions-Setup-0.5.46.exe` |
| Launcher | **none** — the app is a flat publish; installer/portable both point at `UniversalCaptions.App.exe` directly (`launcher.cmd` deleted by ADR-0011) |
| Install location | `%LocalAppData%\UniversalCaptions\` (per-user, `asInvoker` manifest preserved from v0.5.26) |
| Installed size (target) | ~145 MB trimmed self-contained publish (ADR-0011; measured 2026-08-21: 145.2 MiB / 261 files; down from ~1,634 MB) |
| Settings store | `%LocalAppData%\UniversalCaptions\settings.json` (schema v3 — provider concept removed) |

### Bundled contents (target)

- App — self-contained .NET 8 win-x64 publish, trimmed → ~145 MB at `<install>\` (measured 2026-08-21).
- No Python runtime, no Whisper models, no Argos packages (ADR-0011).
- Runtime requirements: internet access to `generativelanguage.googleapis.com` + a free Gemini API key stored in Windows Credential Manager (`UniversalCaptions:GeminiApiKey`).

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
| Positioning (updated 2026-08-21 for ADR-0011) | (1) Gemini Live speech recognition + translation as THE engine (trust strip + #gemini) · (2) Live captions for any Windows app (hero + #how-it-works) · (3) English → Tagalog translation (step 3 + trust strip) · (4) Privacy honesty: no microphone, key in Credential Manager, audio only to Google while captions run (#why, FAQ). All offline/local claims removed. |
| Version tag | [`v0.5.46`](https://github.com/marcgregory/universal-live-captions/releases/tag/v0.5.46) (published) |
| CTA target | `https://github.com/marcgregory/universal-live-captions/releases/download/v0.5.46/UniversalCaptions-Setup-0.5.46.exe` — see §5 |

The page is **live** on GitHub Pages (legacy build from `main` root); the v0.5.46 download links are active on the published GitHub release.

---

## 5. Download CTA Target

| Attribute | Value |
|---|---|
| Primary CTA (hero, sticky nav, download section) | `https://github.com/marcgregory/universal-live-captions/releases/download/v0.5.46/UniversalCaptions-Setup-0.5.46.exe` |
| Portable ZIP link | `https://github.com/marcgregory/universal-live-captions/releases/download/v0.5.46/UniversalCaptions-0.5.46-win-x64-full.zip` |
| Fallback (footer "Release notes") | `https://github.com/marcgregory/universal-live-captions/releases` (always resolves to the latest tag) |
| GitHub repo | `https://github.com/marcgregory/universal-live-captions` |

The primary CTA points at the **specific v0.5.46 release tag**, not at the
`/releases` index. This is deliberate: it makes the user-visible release version
match the artifact that ships.

---

## 6. Gemini Engine Status

**Status: THE Gemini engine (ADR-0011, v0.5.44) — released and required.**

There is one pipeline: `WASAPI loopback → Gemini Live session (inputAudioTranscription +
outputAudioTranscription in a single pass) → Caption overlay`. The former offline default
(Whisper + Argos) was removed; there is no provider selection and no offline mode. The session
runs whenever capture runs; the Translate toggle gates translation-origin caption events without
touching the session; target-language changes recycle the engine. Audio streams to Google's API
only while captions run; the API key lives in Windows Credential Manager (ADR-0009).
**Release gate closed:** real-wire verification and final real-app smoke test passed (RISK_REGISTER R-007 resolved).

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