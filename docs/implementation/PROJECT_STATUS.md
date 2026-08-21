# Universal Live Captions Project Status

Last updated: 2026-08-22 (Gemini-only pipeline â€” ADR-0011 implemented; local Whisper + Argos removed; v0.5.46 540k auto-reconnect overlay refresh CLOSED)

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Provide a current snapshot of project state, sprint progress, and blockers |
| Scope | Current sprint status, build status, blockers, and next milestone |
| Audience | Engineering |
| Owner | Engineering |
| Status | Active |
| Related Documents | [BUILD_PLAN.md](BUILD_PLAN.md), [ROADMAP.md](ROADMAP.md), [CHANGELOG.md](CHANGELOG.md), [RELEASE_PLAN.md](RELEASE_PLAN.md), [TECHNICAL_DEBT.md](TECHNICAL_DEBT.md) |

---

This document is a snapshot. It is not a changelog.

## Current Sprint

**Gemini-only pipeline â€” IMPLEMENTED 2026-08-21 (ADR-0011; v0.5.44).** Local Whisper
(`UniversalCaptions.Speech`) and Argos Translate (`UniversalCaptions.Translation`) are **removed**
from the solution along with `UniversalCaptions.Benchmarks`, their test projects, all bundled
models/Python runtime/Argos packages, and `launcher.cmd`. One Gemini Live session per capture now
produces **both** source transcription (`inputAudioTranscription`) and translation
(`outputAudioTranscription`) in a single pass. Pipeline: `Start(deviceId, sourceLanguage,
targetLanguage, translationEnabled)`; the session runs whenever capture runs; the Translate toggle
gates caption events without touching the session; target-language change recycles the engine.
API key comes only from Windows Credential Manager (`UniversalCaptions:GeminiApiKey`); settings
schema v3 dropped the provider concept. Install: ~145 MB trimmed self-contained publish (measured 2026-08-21), no Python,
no models, no env-var knobs. **Full suite 528/528** (106 Audio + 69 Captions + 174 Speech.Gemini +
179 App), Release 0 warnings / 0 errors, `dotnet format` clean. **RELEASE GATE CLOSED
(2026-08-21): real-wire `inputTranscription` verification PASS** — `tools/GeminiDirectWireSpike --ab`
against the live API: variant B (setup + top-level `inputAudioTranscription`) received 7–8
`serverContent.inputTranscription` frames per utterance with real English source text, and variant A
(field not sent) also received them — the surface streams by default for this model. Evidence:
`artifacts/spike-result/ab-result.json`, TEST_REPORT. Remaining before tag cut: real-app smoke on the
Release artifact (loopback ? captions ? translation toggle ? goAway recovery). Docs updated to the
new direction: constitution §10 amended, SECURITY_PLAN, ARCHITECTURE, TECH_STACK,
REPOSITORY_STANDARDS, DEVELOPER_SETUP, DEPLOYMENT, PRD, PROJECT_SCOPE, RISK_REGISTER, CHANGELOG
v0.5.44.

**Corpus-driven phrase-guard validation â€” CLOSED 2026-08-14 (decision: INSUFFICIENT EVIDENCE â€” do not
ship; no production change; v0.5.40 gate untouched; no v0.5.41).** Second, corpus-driven validation
authorized by the closed segmentation-matrix decision. `PhraseGuardCorpusValidationTests.cs` (11 tests,
43-case labeled corpus: observed Cat 2 evidence, unseen variants, genuine sentence-start readings, the 8
Cat 3 pairs, punctuation/capitalization/length axes, English equivalents) drove the real engine gate per
case (baseline, measured) and layered a **test-side** phrase guard, computing **false-split reduction âˆ’
over-join cost** per candidate. **Measured:** all 7 observed Cat 2 false splits FLUSH under the current
gate (gap real, pinned); every Tagalog phrase guard nets positive â€” `At pagkatapos` **+4** (best),
`Sige, gawin` +2, `At makinig`/`Kaya kailangan`/`Pero pagkatapos`/`Dahil dito`/`Hindi <fragment>` +1 â€”
while the rejected bare `at|kaya|sige|hindi` allowlist over-joins **8** genuine new sentences (negative
control; strictly worse than every phrase guard); multi-word guards are exact-token (`At bukasâ€¦`,
`Kaya naritoâ€¦`, `Sige, magsisimulaâ€¦` NOT caught); **English equivalents all net 0**; two **irreducible
same-surface ambiguities** proven (`Kaya kailangan nating magmadali.` is both the fix and a genuine
new-sentence reading; `Hindi` fixes `Hindi Lunes.` but over-joins `Hindi ko alam kung saan ito.`).
**Decision (user gate): do not ship** â€” the validation proved the guard's mechanics but not the
real-world over-join cost; the over-join cases are constructed, not frequency-measured. **Established:**
bare-word allowlist = reject (unsafe); English equivalents = no net benefit; phrase guard = technically
reduces observed Cat 2 failures; same-surface ambiguity = irreducible with lexical info alone;
**frequency-weighted real-world cost = unknown** (the deciding unknown). **Do not keep expanding the
lexical phrase list** until that frequency question is answered. Full suite **711/711** (106 Audio + 89
Captions + 111 Speech + 42 Translation + 184 App + 179 Speech.Gemini; the 49 matrix tests stay green),
Release 0 warnings/0 errors, `dotnet format` clean. Evidence + results + decision:
`docs/implementation/investigations/phrase-guard-validation.md`.

**Segmentation-guard unit-test matrix â€” CLOSED 2026-08-14 (decision: production gate unchanged).**
The agreed decision-gate suite (`SegmentationGuardMatrixTests.cs`, 48 runs: **41 PASS / 7 FAIL**,
measurement only, no production code changed) drove the current flush gate with 24 annotated cases:
Cat 1 lowercase continuation (3) â†’ APPEND âœ“ PASS; **Cat 2 capitalized continuation idiom (7) â†’ FLUSH
âœ— RED** (the measured v0.5.40 gap â€” `Hindi Lunes.` len-12 regression, `At pagkataposâ€¦`, `At
makinigâ€¦`, `Kaya kailanganâ€¦`, `Sige, gawinâ€¦`, `Pero pagkataposâ€¦`, `Dahil ditoâ€¦`); Cat 3 bare-starter
pairs (8) â†’ both members identical (provably ambiguous â€” a bare `At|Kaya|Sige|Hindi â†’ APPEND`
allowlist is **unsafe**, it would over-join the new-sentence reading of each pair); Cat 4 genuine new
sentence (6) â†’ FLUSH âœ“ PASS. **Conclusion: the dangerous axis is insufficient context, not
capitalization.** The seven Cat 2 cases are known defects with a candidate mitigation (phrase-level
idiom guard) but are not sufficient evidence to ship it. **Production gate stays unchanged.** A second
smaller **corpus-driven validation** (observed idioms â†’ APPEND; same idioms in genuine sentence-start
contexts â†’ FLUSH; unseen variants; short fragments; punctuation/capitalization variations; English
equivalents; negative over-join cases) must establish **false-split reduction âˆ’ over-join cost** before
any guard touches production. Recommended state: **investigation COMPLETE â†’ matrix COMPLETE â†’ root
cause confirmed â†’ production gate unchanged â†’ phrase-level guard remains a candidate pending broader
corpus validation.** Evidence: `docs/implementation/investigations/gemini-segmentation.md`,
ROADMAP (matrix CLOSED), TEST_REPORT.

**v0.5.39 â€” Gemini live-session lifecycle fix: graceful goAway surfaced (close-out 2026-08-13).** The Gemini server ends a Live session with a graceful `goAway` frame (~9 min of continuous audio â€” 537.0 s investigation run, 521.9 s earlier evidence). The released baseline tail-flushed the accumulator and exited the receive loop **silently** â€” no failure event, no status change â€” so the pipeline kept the engine attached and the overlay froze on the last translated sentence while Whisper kept running. **Fix (code-behind only, 4 production files):** the `GoAway` branch raises `TranslationFailed(ServerError, "Live translation session ended by server.")` through the standard failure chokepoint; `CaptionPipeline.OnLiveTranslationFailed` clears the caption service's translation active line (new `ICaptionService.ClearLiveTranslationActiveLine()`) before detaching the engine and raising the error status. Committed Tagalog history stays visible by live-translation display policy; the user toggles translation OFF to return to source captions and re-enabling starts a fresh session. `ClearTranslationHistory` (v0.5.37) untouched. **645/645 tests** (106 Audio + 89 Captions + 111 Speech + 42 Translation + 184 App + 113 Speech.Gemini) â€” 3 new tests, Release 0 warnings / 0 errors, `dotnet format` clean. **Real-app goAway regression PASS (2026-08-13, v0.5.39 artifact, no trace plumbing):** continuous Gemini enâ†’tl, natural goAway at ~9 min â†’ Control Window status "Live translation unavailable: Live translation session ended by server." (the pre-fix silent freeze is gone); overlay stable after goAway (engine detached); OFFâ†’ON toggle starts a **new Gemini session** producing translated captions again; status recovers to "Capturing system audio.". 6/6 checks PASS. Evidence: CHANGELOG v0.5.39, `regression-v0539-goaway.ps1`/`regression_v0539_goaway.log` (untracked). **Fix isolated from the v0.5.36 spike worktree that proved it** (trace instrumentation + harness artifacts excluded). Commit `5ae30bc`.

**v0.5.38 â€” stable/unstable partial rendering (close-out 2026-08-13).** The source-STT active line now paints in two tones â€” the **stable word head** stays white and the **unstable partial tail** renders in a subtle green (`PartialUnstableBrush`, frozen `#9EC99E`), so the user can see which words Whisper has already committed to vs the speculative tail a later partial is likely to revise. Pure `CaptionPartialStability.StableWordCount` (previous partial's word head vs current) + `SplitAtWord`; `CaptionOverlayWindow` paints the two tones in a single mutable active block (`CreateActiveCaptionBlock`/`PaintActiveCaptionBlock`/`GetBlockText`). Head-revision re-greens the whole line; FINAL freezes all-white; Stop â†’ green 0. **642/642 tests** (106 Audio + 89 Captions + 111 Speech + 42 Translation + 182 App + 112 Speech.Gemini) â€” 21 new App tests (16 `CaptionPartialStabilityTests`; `CaptionRenderIdentityTests` rewritten 6â†’11 inline-aware), Release 0 warnings / 0 errors, `dotnet format` clean. **Real-app smoke PASS (2026-08-13):** two-tone evidence captured live via config-only `UC_NATIVE_PARTIAL_WINDOW=8` (the production 4 s window rolls â€” `TryGetPartial` snapshots the trailing window â€” so the head rarely stays anchored at 4 s); verified sequence first-partial all-green â†’ extension white head + green tail â†’ head-revision re-green â†’ FINAL all-white â†’ Stop green 0. Evidence: CHANGELOG v0.5.38, RELEASE_PLAN Â§3.7, `smoke_v0538_twotone_evidence.png` (untracked). Commit `95f5049`. **Implementation, tests, verification, docs â€” complete; tag cut and GitHub release pending.**

**v0.5.37 â€” mixed-language history scrub on Translate OFF and target-language change (published 2026-08-13).** A real-app smoke FAIL after v0.5.36 surfaced a UX defect: toggling Translate OFF after a Tagalog/Japanese session left translated `LineOrigin.Translation` history lines mixed with new English source captions â€” the visible "previous Tagalog captions became English" symptom. Root cause: turning translation off dropped the active translation line but never scrubbed the committed translation history. A related second symptom: switching target language while Translate was ON (`tl â†’ ja`) left the prior target's history alongside the new target's history. **Fix:** new language-agnostic `ICaptionService.ClearTranslationHistory()` removes every `LineOrigin.Translation` entry from the committed history (`LineOrigin.SourceStt` preserved); `CaptionState.ClearTranslationHistory()` returns the removed count so the service can decide whether to raise `StateChanged`. Hooked into both transitions â€” `SetTranslationEnabled(false)` clears the active translation line AND the translation history; `SetTranslationEnabled(true, target)` where `target` differs from the current target while translation is already ON clears the previous target's history (same-target is a no-op; setting true after a previous OFF is also a no-op for the history scrub). **621/621 tests** (107 Audio + 81 Captions + 111 Speech + 42 Translation + 161 App + 119 Speech.Gemini) â€” 8 new tests, Release 0 warnings / 0 errors, `dotnet format` clean. **Real-app smoke PASS (2026-08-13):** Release app + WASAPI loopback + Gemini provider, in-session sequence with no Stop/Start â€” (1) Translate OFF + English source captions visible; (2) Translate ON â†’ Tagalog â†’ Tagalog captions appear; (3) target switch `tl â†’ ja` â†’ previous Tagalog history cleared, new JA session starts; (4) Translate OFF â†’ committed Tagalog/Japanese history cleared, **English SourceStt history preserved** (the English captions that re-appear are the same STT output that was being captured while translation was ON â€” preserved `LineOrigin.SourceStt`, not retranslations). Evidence: CHANGELOG v0.5.37, RELEASE_PLAN Â§3.6. **Implementation, tests, verification, docs â€” complete; tag cut and GitHub release pending.**

**Runtime Gemini-toggle latency verification â€” PASS (2026-08-12, measurement only).** Real WASAPI two-mode measurement (Release app + loopback English audio; LEG1 Translate OFF, then runtime toggle to Gemini ENâ†’TL for LEG2, no Stop/Start) confirmed identical Whisper STT FINAL latency in both modes: Translate OFF mean **11.8 s** (6.3â€“17.0 s) vs Gemini ON mean **11.4 s** (7.5â€“13.9 s). The stderr trace proves Gemini is fully detached when translation is OFF â€” the first translation request fired only at the runtime toggle (52.1 s); zero translation requests in the English-only leg. **Conclusion: Gemini does not make English-only slower; it masks Whisper's committed-FINAL cadence by streaming partial translations (Gemini partial â‰ˆ11.5 s â‰ˆ Whisper FINAL).** No code changes. Evidence: CHANGELOG v0.5.35, `latency_mode_compare.log`. Next real UX/perf investigation: **Gemini streaming caption segmentation**.

**Common translation state made provider-agnostic (2026-08-12): the v0.5.32 design correction is
complete â€” `TranslationEnabled` / `TargetLanguage` always reflect the user's Translate checkbox +
target for BOTH providers; the provider decides only the translation MECHANISM.** Root problem:
v0.5.32 fed the Gemini policy result (always false) into the caption service's common translation
state, so every Gemini session had `TranslationEnabled == false` and the display inferred the live
session + badge from line origins â€” breaking Argos parity (Translate toggle / target dropdown did
nothing while a Gemini session was live). Fix: `SetCaptionLineTranslation` gates only the Argos
caption-line path; live audio engines relay translation-origin lines; `TranslationProviderPolicy`
lost its `enabled` parameter. Runtime reconfiguration via `CaptionPipeline.SetLiveTranslation`
(toggle-off stops the Gemini engine + clears the translation active line; toggle-on/target-change
creates a new engine; failed swap raises error without stopping Whisper). Display keys off common
state only. **610/610 tests** (106 Audio + 78 Captions + 111 Speech + 42 Translation + 161 App + 112
Speech.Gemini), Release 0 warnings / 0 errors, `dotnet format` clean. Evidence: CHANGELOG v0.5.33,
TEST_REPORT (2026-08-12 summary).

**Final real-world acceptance PASS â€” v0.5.33 is close-out / release-level (2026-08-12).** Harness
`acceptance-v0.5.33-translation-parity.ps1` (untracked) drove the Release app over real WASAPI
loopback (looped English audio) through the full control surface for BOTH providers in one session:
**22/22 PASS (Argos 11/11 + Gemini 11/11).** While captions are RUNNING, for Argos and Gemini alike:
Translate OFF â†’ a genuinely new source-English caption appears (control toggle reads off, Whisper
keeps capturing); Translate ON â†’ target language returns; target `tl â†’ ja â†’ tl` updates immediately
with no Stop/Start; STT worker PIDs stay constant across every runtime change. Gemini additionally
spawned a **fresh worker set** after the Argos session fully exited (no reuse). Real CJK verified
in-file (ãƒŽãƒ¼ãƒˆãƒ–ãƒƒã‚¯ / ã‚ã‚ŠãŒã¨ã†ã”ã–ã„ã¾ã™ / æ¥é€±ã®ã”æ¥åº—ã‚’ãŠå¾…ã¡ã—ã¦ãŠã‚Šã¾ã™), Argos
requestâ†’result **0.088 s**, no orphaned workers, clean exit. Three harness honesty fixes were applied
before the result was trusted (toggle-OFF now waits for a new non-translated caption; the overlay
badge is not UIA-exposed so badge behavior moved to unit tests + control-toggle assertion; fresh-set
check waits out the up-to-10 s worker Stop budget) â€” no product code changed for the close-out.
Evidence: CHANGELOG v0.5.33, `v0533_parity_acceptance.log` (untracked). **v0.5.33 is READY for
release.** Next: install/bundle this build (RELEASE_PLAN), and Phase 2 real-app validation (YouTube,
VLC, Zoom) remains deferred per user.

**Translation & naturalizer investigation CLOSED (2026-08-07); next phase = release/landing-page
work.** The user closed both the offline-MT search and the naturalizer-model search. Conclusive
evidence: Argos + deterministic 13 rules (0/23 unseen recall), M2M-100-418M (0/16 + 20â€“40Ã— slower),
Qwen2.5-1.5B-Instruct naturalizer (15/16 worse + contract violation), NLLB-600M (quality-excellent
but CC-BY-NC â†’ not production-eligible), Gemini Live (cloud quality/realtime reference). **Frozen
production path (no code changes):** `WASAPI â†’ Whisper â†’ Argos OPUS-MT enâ†’tl â†’ 13-rule
deterministic naturalizer â†’ Caption overlay`. Optional experimental path: `Audio â†’ Gemini Live
Translate` (user's own API key; naturalness + realtime vs offline + privacy + cost). Larger-LLM /
Tagalog-fine-tune explicitly deferred as a future research project, not an MVP optimization.
Evidence: BENCHMARK_REPORT (Final Decision â€” Translation & Naturalizer Investigation CLOSED),
CHANGELOG v0.5.29.

**Release/landing-page work in progress (2026-08-07) â€” see
[RELEASE_PLAN.md](RELEASE_PLAN.md) for v0.5.29 readiness.** Per
[ARTIFACT_REGISTRY.md](../ARTIFACT_REGISTRY.md), release-readiness content lives in `RELEASE_PLAN.md`
(not duplicated here). The `landing/` + `packaging/` + `artifacts/` top-level directories are now
explicitly governed by [PROJECT_CONSTITUTION.md](../PROJECT_CONSTITUTION.md) Â§1; the landing page's
primary Download CTA points at the v0.5.29 release tag. **Decision (in RELEASE_PLAN.md Â§1):
NOT READY pending the v0.5.29 GitHub release tag + a clean-machine install verification.**

**Small-model Tagalog naturalizer â€” quality probe FAILED (2026-08-07).** Per the user's next
experiment, tested whether a small permissive instruction-following model can naturalize Argos
enâ†’tl output (contract: improve naturalness while preserving meaning; guardrails enforced in the
prompt). **Qwen/Qwen2.5-1.5B-Instruct** (Apache-2.0, ungated, 1.5B) given the Argos Tagalog line
only, greedy deterministic decode, on the same 16 unseen lines vs 4 columns (Argos / Argos + frozen
13 rules / Argos + small model / Gemini reference). **DECISIVE FAIL at the quality gate (user's
rule: stop if not visibly better):** 15/16 lines are invalid Tagalog or meaning-destroyed; #7
violates the output contract (English + added explanation); inference ~11 s/line mean. Frozen-rule
column parity-verified against all 13 C# test vectors (0/16 rewrites, consistent with 0/23 unseen).
**No production change** â€” baseline remains `Whisper â†’ Argos â†’ frozen 13-rule naturalizer â†’
Caption`. The naturalization gap now has three independent failure lines: deterministic rules
(0/23 unseen recall), small instruction-following model (15/16 worse), M2M family (0/16). Remaining
untested options would need a materially larger permissive LLM (contra the user's "very small
model" preference) or a dedicated Tagalog-rewrite fine-tune (new training experiment). Evidence:
BENCHMARK_REPORT (Small-Model Tagalog Naturalizer section), `naturalizer_qwen2.5-1.5b_instruct_
2026-08-07.json`, commits `100fbae`.

**Translation research phase â€” offline model-selection investigation CLOSED (2026-08-07).** User
decision: **stop searching for another offline MT model.** Evidence chain (all recorded in
`BENCHMARK_REPORT.md`): Argos/OPUS-MT enâ†’tl (production offline baseline, frozen, ~0.11 s/line),
NLLB-200-distilled-600M (quality ceiling but CC-BY-NC â†’ not production-eligible), MADLAD-400-3B-MT
(rejected 2026-08-06: slow/verbose/2.8 GB), M2M-100-418M (rejected 2026-08-07: lost 0/16 unseen
lines to Argos, ~2.76 s/line mean), Gemini Live Translate (experimental quality/realtime reference â€”
cloud/privacy/cost tradeoff), frozen 13-rule naturalizer (fixes known Argos artifacts, ~0 unseen-set
recall). **Three-track conclusion:** (1) keep Argos + naturalizer as the production offline baseline
(`Whisper â†’ Argos/OPUS-MT enâ†’tl â†’ frozen 13-rule naturalizer â†’ Caption`); (2) keep Gemini as the
experimental reference (naturalness + realtime vs offline + privacy + cost); (3) stop the offline-
model hunt unless a new candidate materially changes the constraints. **Next experiment (user
direction): small-model Tagalog naturalization** â€” whether a small, permissively-licensed,
instruction-following/rewriting model can act as a Tagalog naturalization/correction layer over
Argos (a different experiment from another MT sweep). The second blind scorer of the unseen
worksheet is supporting evidence only and no longer blocks the direction. Evidence: BENCHMARK_REPORT
(Unseen-set generalization test + M2M probe + Final Decision), commits `98ab405`/`100fbae`.

**v0.5.26 â€” Core + Installer + Phase 2 app validation DONE (2026-08-06).** App-by-app validation of
the installed v0.5.26 bundle (`launcher.cmd`, `%LocalAppData%\UniversalCaptions`, no repo/admin/dev
env) against real apps, real WASAPI loopback, real enâ†’tl. **Chrome / YouTube â€” PASS:** local-media
first caption â‰ˆ2.5 s; YouTube playback first real caption â‰ˆ14 s after Start, live partials translate
in place, `EN || TL` badge, committed translated Tagalog, 0 orphans. **VLC â€” PASS:** first caption
â‰ˆ4.6 s, live partials + committed translated Tagalog, loop repeats, POSTSTOP history retained,
0 orphans. **Zoom â€” NOT VALIDATED (âš ï¸ limited evidence):** Zoom Workplace 7.0.6 is Chromium-based
with no UIAutomation surface and no available meeting/account â€” recorded as an environment limitation,
NOT an app defect and NOT upgraded to PASS (the run included no live meeting interaction). **Teams â€”
N/A** (desktop client not installed). Worker cmdlines installed-only throughout; no production-code or
installer changes. Evidence: TEST_REPORT Â§App-by-app validation â€” Phase 2, CHANGELOG v0.5.26,
`appval_*.{log,csv,txt}` (untracked).

**Installer & distribution â€” Entry 17 CLOSED as PASS (2026-08-06), post-core-done.** The frozen
v0.5.25 core is now
deployable to a clean Windows 10 machine with **no repo, no admin, no network**: Inno Setup (per-user)
+ self-contained .NET 8 win-x64 publish + a bundled pruned Python runtime + bundled faster-whisper
small model + bundled pruned Argos `enâ†’tl` packages, wired by `packaging/launcher.cmd`
(process-scoped env only). One approved additive production seam: **`UC_FW_MODEL`** in
`SpeechEngineFactory.CreateNative` (unset â†’ `"small"`, unchanged; set â†’ worker `--model <path>`),
covered by two new tests. Setup.exe **795.5 MB**; installed **1,634.5 MB** at
`%LocalAppData%\UniversalCaptions` (flattened layout = `MAX_PATH` fix for the torch license tree that
rolled back the first install). **Installed-bundle acceptance PASS** (real audio via WASAPI loopback,
real enâ†’tl): worker cmdlines are installed-only
(`py\python.exe â€¦ faster_whisper_worker.py --model <install>\models\faster-whisper-small --compute
int8 --threads 4 --beam-size 5`; Argos server on the same bundled python), first caption â‰ˆ4.1â€“4.7 s
warm, live partials + committed translated Tagalog (`EN || TL` badge), settings persist, clean
Start/Stop/Exit with 0 orphans, clean uninstall (exit 0) leaving only the app's own `settings.json`
(`PYTHONDONTWRITEBYTECODE=1` prevents `.pyc` leftovers). **384/384 tests**, Release 0 warnings/0
errors, `dotnet format` clean. Evidence: `docs/reports/INSTALLER_DISCOVERY.md` (Â§8 decisions, Â§9
build + acceptance), CHANGELOG v0.5.26, `packaging/` (`.iss`, `launcher.cmd`, `build-package.ps1`,
`output/UniversalCaptions-Setup-0.5.25.exe`), `installer_acceptance*.{ps1,log,csv,txt}` (untracked).
**Caveat (recorded, non-blocking):** installer acceptance passed using the final staged package; the
reproducible `build-package.ps1` path remains an optional follow-up validation because the final
installer was built successfully through the underlying Inno Setup process. Next meaningful test
before distribution is a **truly clean Windows machine**. No further installer changes.

**Final real-world acceptance â€” PASS (2026-08-06), project core-done.** Per user direction ("stop
optimizing CPU; run the final real-world acceptance session"), the production default
(`fasterwhisper-native` + live partials, `UC_NATIVE_THREADS=4`) was validated in continuous normal use
through `acceptance.ps1` (untracked): Release App + VLC + real WASAPI loopback, per-poll CIM CPU, UIA
overlay snapshots, 300 s legs. **Leg 1 Tagalog, translation OFF** (`uc_video_full.m4a`): STT worker
31.8% of machine (max 37.6%), App 0.9%, first caption 3.27 s, 95 snapshots, max 33 lines, clean exit,
0 orphans. **Leg 2 English + enâ†’tl** (`english_sustained_90s.wav` looped): STT 33.5% (max 37.1%) +
Argos 4.2% (max 21.6%), App 1.3%, first caption 3.23 s, 129 snapshots, max 54 lines, clean exit,
0 orphans. Overlay evidence: live partials grow in place, FINALs freeze into bounded history with the
`EN || TL` badge, committed lines real Tagalog, Stop retains history with no stale partial. Clean exit
measured ~5 s (`WM_CLOSE`); harness close budget 25 s. **382/382 tests**, Release 0 warnings/0 errors,
`dotnet format` clean. Evidence: TEST_REPORT (final real-world acceptance), CHANGELOG v0.5.25,
`acceptance_summary.csv`/`acceptance_*.csv`/`acceptance_*_captions.txt` (untracked).

**Entry 16 â€” CPU optimization: decode-thread cap (COMPLETE 2026-08-06).** The promoted path
(`fasterwhisper-native` + live partials) sustained **77.4% of the machine** in the STT worker: every
partial and FINAL decode used all 12 cores (`FasterWhisperEngineOptions.Threads` defaulted to
`Environment.ProcessorCount`; the App passed all 12 to `--threads`). Fix (code-behind only, no engine/
protocol/segmentation/partial/overlay/translation change): **`UC_NATIVE_THREADS` env knob, production
default `Threads = 4`** (clamped [1, ProcessorCount]) in `SpeechEngineFactory.CreateNative`; worker
args extracted to `LineProtocolFasterWhisperProcess.BuildWorkerArguments` (unchanged behavior);
`sttnative` gains `--threads`. **382/382 tests** (8 new: factory default 4 / override / invalid
fallback + worker-arg propagation), Release 0 warnings/0 errors, `dotnet format` clean. **Formal
`sttnative` gate (12t vs 4t, real video audio, small int8 tl, partials 1/4 s, max segment 8 s):**
WER **33.2% both**, realtime **1.18Ã— both**, first FINAL 17.98 vs 18.12 s, emit-lag comparable,
**FINAL stream text 100% identical (0 diffs)**. **Real-App CPU probe (default, speech + partials):**
STT worker system mean **77.4% â†’ 31.6%** (max 88.2% â†’ 37.6%), App ~1%, first caption 3.72 s, overlay
producing (max 16 lines); speech + translation run STT 26.6% + Argos 3.4%. **Decision: PASS â€” cap
production default at 4.** Evidence: Entry 16, TEST_REPORT (Entry 16 close-out),
BENCHMARK_REPORT (Entry 16 gate), CHANGELOG v0.5.24.

**Entry 15 â€” overlay live-line integration (COMPLETE 2026-08-06).** The WPF overlay previously painted
**committed FINALs only** â€” commit `7d1c057` ("temporary diagnostic tracer", 2026-08-03) had replaced
Slice 7's active-line painting and `_activeBlock` was never assigned. Now `CaptionOverlayWindow`
`UpdateCaptionItems` creates one mutable `_activeBlock`, rewrites it in place on later partials, and
removes it when `model.ActiveLine` is null (committed/stopped/hidden-while-translating); `ReconcileHistory`
reuse-by-sequence and the `shouldUpdate` gate (no source flash during translation-pending) unchanged.
`CaptionRenderIdentityTests` rewritten 4â†’6. **374/374 tests** (App 89), Release 0 warnings/0 errors,
`dotnet format` clean. **Real-App smoke PASS** (Entry 14 checklist + overlay AC-1..AC-8): first visible
partial â‰ˆ5.6 s after capture start; one growing active line (`meeting sum` â†’ `Meeting someone.`); FINAL
freezes into history with no churn; Stop/Dispose leaves no stale partial (POSTSTOP_1..3 stable); App CPU
~0â€œ66% variable / worker ~0%; **enâ†’tl Argos verified** â€” live-translated Tagalog active line painted before
commit, no raw-English flash; tlâ†’en confirmed as the documented-unsupported direction (stanza SBD) with
graceful degradation. Evidence: Entry 15, TEST_REPORT.md, CHANGELOG v0.5.23.

**Entry 14 â€” production default promotion (COMPLETE 2026-08-05, ADR-0008).** Product decision
(user-approved): the production STT default is now **`fasterwhisper-native` + live partials**; ggml-base
is preserved as the explicit fallback (`UC_STT_ENGINE=ggml-base`). Engine selection extracted into the
testable `SpeechEngineFactory` (default/native â†’ native + partials with interval 1 s / window 4 s / 8 s
segment cap frozen; `ggml-base` â†’ original local Whisper; `fasterwhisper` â†’ windowed engine). No
automatic runtime fallback (deliberate, ADR-0003 no-silent-switch). Worker protocol, windowed engine,
ADR-0007, TD-002, TD-005 untouched. **372/372 tests** (5 new factory selection tests), Release
0 warnings/0 errors. Decision records: ADR-0008, Entry 14, CHANGELOG v0.5.22.

**Slice 12 â€” faster-whisper native-streaming live partials (COMPLETE 2026-08-05: benchmark PASS).** Chrome-Live-Caption-style live partials on the opt-in `fasterwhisper-native` engine: incremental partial text while the speaker is still talking, one FINAL per completed segment (unchanged), no wire-protocol change, translation OFF, ggml-base untouched. **Implementation (additive, knobs default off):** `SpeechSegmentDetector.TryGetPartial` (bounded trailing-window snapshot, refused while idle/hangover/after close), `FasterWhisperEngineOptions.PartialDecodeInterval` (default 0 = Slice 10/11 FINAL-only preserved) + `PartialDecodeWindow` (4 s), `FasterWhisperNativeStreamingEngine` cadence dispatch with at most one partial decode in flight/queued (no backlog), `PartialTranscriptAvailable` event; App knobs `UC_NATIVE_PARTIAL_INTERVAL` (1 s) / `UC_NATIVE_PARTIAL_WINDOW` (4 s); `sttnative` benchmark `--partial-interval`/`--partial-window` + partial metrics + CSV partial table. **367/367 tests** (10 new), Release 0 warnings/0 errors, format clean. **Controlled real-audio benchmark PASS (2026-08-05):** small int8, tl, hangover 0.7 s, max 8 s, realtime feed, translation OFF, `--partial-interval 1 --partial-window 4` on `uc_video_full_16k.wav` (288.79 s) vs the `fil-orig` reference â€” first visible partial **5.59 s after speech onset** (vs first FINAL 15.0 s), **19.5 partials/120 s** (~3 s updates during speech), active line increments ("Magandang" â†’ â€¦ â†’ full sentence), FINAL stream **text-identical to Slice 11** (no accuracy regression, WER 33.19% in-harness), FINAL ~6 s after segment close, backlog **bounded** (plateau ~50 s vs 43 s FINAL-only; one 17.5 s machine-contention spike), realtime-safe 1.18Ã—, nothing dropped/reordered. **Decision: PASS â€” ggml-base stays the production default; partials default off, so production behavior is unchanged unless opted in.** Documented tradeoffs: ~5 % wall + ~8 s tail-latency cost, expected rolling-4 s-window behavior (FINAL reveals earlier words). Scoped in `docs/CHANGE_IMPACT_ANALYSIS.md` Entry 13; evidence in `BENCHMARK_REPORT.md`/`TEST_REPORT.md` (Slice 12), CHANGELOG v0.5.21.

**Slice 11 â€” native-streaming segment-boundary tuning (COMPLETE 2026-08-05: decision recorded â€” keep `MaxSegmentDuration = 8 s`).** Per-user follow-up to the Slice 10 PASS: tune the opt-in `fasterwhisper-native` segment boundaries (test 8/10/12 s, measure mid-sentence splits, confirm bounded latency/backlog, keep `SilenceHangover = 0.7 s`, no worker-protocol / ggml-base / windowed-engine changes). **Additive benchmark improvements:** `timeBeginPeriod(1)`/`timeEndPeriod(1)` around the `sttnative` realtime feed (fixes the ~1.57Ã— `Thread.Sleep` pacing artifact â†’ valid ~1.1Ã— controlled pacing) and a mid-sentence-split metric (unterminated FINALs + short fragments, in gate table + CSV). **Controlled sweep (2026-08-05) â€” max-segment 8/10/12 s** on the actual video audio vs the `fil-orig` reference (small int8, tl, hangover 0.7 s fixed): WER 32.6%/33.2%/30.0%, cadence 13.3/10.8/9.1 FINALs/120 s, mid-sentence splits 31%/42%/45%, 0 partials, latency/backlog bounded at all three caps (~5 s behind segment end). **Longer segments do NOT reduce mid-sentence splits, cost ~46% responsiveness at 12 s, and add end-of-audio cap hallucinations (10 s `Pag-pag-pagâ€¦` stutter, 12 s truncated `tunog`); the 12 s WER gain is a boundary artifact.** **Decision: keep 8 s â€” no production or knob-default change** (real-App 8 s latency/backlog evidence already exists from Slice 10). **357/357 tests**, Release build 0 warnings/0 errors, format clean. Scoped in `docs/CHANGE_IMPACT_ANALYSIS.md` Entry 12; evidence in `BENCHMARK_REPORT.md`/`TEST_REPORT.md` (Slice 11), CHANGELOG v0.5.20.

**Slice 10 â€” faster-whisper native streaming (COMPLETE 2026-08-05: deterministic phase + benchmark/real-App validation PASSED).** New `FasterWhisperNativeStreamingEngine` behind `UC_STT_ENGINE=fasterwhisper-native` (additive; `fasterwhisper` keeps the windowed engine; ggml-base stays the frozen production default). C# owns VAD/segment detection + buffering + when-to-decode; the existing faster-whisper worker wire protocol is unchanged. One FINAL per completed speech segment (no live partials). **Deterministic phase DONE:** engine + internal `SpeechSegmentDetector` implemented, App selector branch added, **357/357 tests** (21 new, no Python), Release build 0 warnings/0 errors, format clean, no vulnerable packages; fresh-context review PASSED with fixes. **Validation PASSED (2026-08-05):** additive `sttnative` benchmark mode + real-App run with `fasterwhisper-native` (small int8, tl) on the actual video audio vs the `fil-orig` reference â€” committed WER **32.6%** (ggml-base 51.2%), **0 partials (FINAL-only)**, commit cadence **13.3 FINALs/120 s** (windowed faster-whisper 2/120 s), first real-App caption **15.2 s**, STT latency 11.6â€œ12.9 s from segment start â‰ˆ ~4 s behind segment end with no growing backlog, no recurring `(Song)`/`(Subscribe)` hallucinations, no dropped final at Stop. **Decision gate recorded: Slice 10's question is answered â€” segment-based native streaming preserves faster-whisper's accuracy while eliminating the stale 20â€œ40 s commit backlog.** faster-whisper stays opt-in; the ggml-base production default is unchanged (frozen). Documented tradeoff: the 8 s segment cap can split sentences mid-word (tunable via `UC_NATIVE_MAX_SEGMENT`). Scoped in `docs/CHANGE_IMPACT_ANALYSIS.md` Entry 11; evidence in `TEST_REPORT.md` Slice 10, CHANGELOG v0.5.19.

**Slices 1â€œ6 complete (close-out 2026-08-01).** Slice 5 (WPF overlay + control window) + Entry 7 (live active-line translation + Chrome-style overlay) closed out 2026-08-01; Slice 6 (E2E latency + OFAT baseline) closed out 2026-08-01. **Argos pre-warm closed out 2026-08-02** (v0.5.9): first-caption latency ~23â€œ30 s â†’ ~3.8â€œ6.85 s. **Slice 7 â€” caption overlay layout & stable incremental rendering (in progress, 2026-08-02)**: full-viewport width verified via a layout probe; the render path now mutates only the live block on a Partial and reuses history blocks by identity, with bottom scroll/re-anchor limited to when a new block is inserted and content overflows. **All MVP slices (0â€œ6) complete; Phase 2 real-app validation deferred per user.**

**Post-close-out refinement (2026-08-01):** live **active-line translation** + **Chrome-style overlay redesign** landed on top of Slice 5 (change-impact Entry 7): the in-progress caption line is now translated in the target language while the speaker is still talking (single in-flight slot, instance-identity stale-guard, disabled-mid-flight results discarded); the overlay is an auto-sized translucent pill with white text, a target-language badge, expand/collapse chevron, and a hide button; the control window adds "Show Captions". Implementation + unit tests **complete (224/224)**; **manual verification with real audio + real Argos completed 2026-08-01** â€” Tagalog appears on the in-progress overlay line before commit, `TL` badge, chevron expand/collapse, close-hide, "Show Captions" re-show, and pipeline-continues-while-hidden all verified (evidence in `TEST_REPORT.md`). **Entry 7 closed out 2026-08-01.**

## Current Progress

Slice 1 (Audio Capture Spike), Slice 2 (STT Spike), Slice 3 (Translation Spike), and **Slice 4 (Caption Service) are complete and verified.** Slice 4 close-out approved 2026-08-01: `ICaptionService`, `CaptionLine`, `CaptionState`, and `CaptionServiceOptions` in `UniversalCaptions.Core.Captions` (contracts in Core so `UniversalCaptions.Captions` depends only on Core, per the ADR-0003/0006 precedent); `src/UniversalCaptions.Captions` implements `CaptionService`: partials replace the active line, finals commit to a bounded sequence-ordered history, optional background translation (failure preserves the source caption; stale results matched by line identity are dropped), cancellation of in-flight translations on stop/reset/dispose, and events raised outside the serialization gate with snapshot `History`. Verified with deterministic `StubTranslationEngine`/`GatedTranslationEngine` fakes â€” 40 tests, build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, no vulnerable packages. Fresh-context review completed; findings fixed (snapshot history reads, deferred CTS disposal, atomic translation-start token, stale-translation identity guard, event-raising moved out of the translation catch, target normalization).

**Slice 5 (WPF overlay + control window) is complete (close-out 2026-08-01).** `src/UniversalCaptions.App` (new WPF project, `net8.0-windows`, `UseWPF`, PerMonitorV2 manifest) is the DI composition root: `IOverlayService` (visibility/position/opacity/font size/click-through), `CaptionOverlayWindow` (borderless/transparent/always-on-top, history + active line, drag/resize, click-through via `WS_EX_TRANSPARENT`), `CaptionPipeline` (wiring capture â†’ processor â†’ STT â†’ `CaptionService` via `Func` factories, idempotent Start/Stop/Dispose, `StatusChanged`/`LatencyUpdated` events, error handling, teardown ordering), `ControlWindow` (audio source/language, translation on/off + target, status/latency, overlay sliders, Start/Stop), `AudioSourceLoader` (device enumeration with preferred default), `TranslationGuard` (source-equals-target rejection), and `App.xaml.cs` (DI registration + `ShutdownMode.OnMainWindowClose`). The deferred Q1 display policy is resolved: the active caption renders verbatim from the latest partial (`CaptionState.ActiveLine`); committed finals render as bounded history; translated text replaces the source on a committed line only when translation completes (PRD FR-5/FR-14). Verified with `UniversalCaptions.App.Tests` â€” `CaptionDisplayPolicyTests` (8) + `CaptionPipelineTests` (20) + `AudioSourceLoaderTests` (4) + `TranslationGuardTests` (4). **Manual overlay/device verification completed 2026-08-01** on this Windows 10 machine: real system audio â†’ Whisper `ggml-base` â†’ live overlay captions; always-on-top/transparency; drag/resize/click-through; stop/restart; rapid Stopâ†’close (clean ~2 s exit); model-not-found and source-equals-target error paths (evidence in `TEST_REPORT.md` Slice 5). **Real-Argos wiring verified end-to-end through the App 2026-08-01**: with the dev Argos venv recreated (`argostranslate==1.11.0` + enâ†’tl/tlâ†’en/jaâ†’en/enâ†’ja under a short 8.3 temp path per TD-011), the App spawned the Argos child process and committed overlay lines displayed real translated Tagalog (`tamad aso.`, `Ang mabilis na kayumangging sorra ay tumatalon sa ibabaw ng eruplano`) with `IsTranslated = True`; this also exercised the `ControlWindow.ApplyTranslationSettings` guard fix (toggle stays ON + target combo enabled on a guard error so a valid target can be selected). Total test count: **209/209 passing** (66 Audio + 45 Captions + 41 Speech + 21 Translation + 36 App). All Slice 5 Definition-of-Done items are satisfied.

The `ITranslationEngine` contract (with `TranslationResult`, `TranslationErrorKind`, `TranslationException`) lives in `UniversalCaptions.Core.Translation`; it is verified with a deterministic `FakeTranslationEngine` (8 tests); `ArgosTranslationEngine` (child Python process over a newline-delimited JSON line protocol, bundled `argos_translate_server.py`) is verified with a fake process seam (13 tests, incl. restart-after-fatal-error) and against real Argos 1.11.0 end-to-end (direct pairs `enâ†’tl`, `jaâ†’en`, `enâ†’ja`; pivoting `jaâ†’tl` via `en`). The translation benchmark is recorded (load/first latency, steady-state distinct-text latency, identical-input cache, throughput, Argos working set, finals-stream ordering, char-similarity quality). Fresh-context review completed; findings fixed (stale-process recovery, unwrapped exceptions, Python crash path, options validation, `ArgumentList`, UTF-8 pinning) with remaining items in TD-013â€œTD-015.

## Slice 6 Baseline Defaults (validated 2026-08-01)

> **Superseded 2026-08-05 (Entry 14 / ADR-0008):** the production STT default is now
> `fasterwhisper-native` + live partials (see Current Sprint). This Slice 6 block remains as the
> historical record of the former ggml-base baseline, now the explicit fallback
> (`UC_STT_ENGINE=ggml-base`) with its frozen settings:

```text
Model:            ggml-base (unchanged, ADR-0003)
WindowDuration:   8 s (unchanged)
DecodeInterval:   1 s (unchanged)
StabilityWindow:  3 â†’ 2 (promoted)

Evidence:         OFAT sweep (Phase 1b) + App-level SAPI E2E validation (Phase 1c:
                  real WASAPI loopback â†’ Whisper â†’ Argos enâ†’tl â†’ WPF overlay,
                  baseline + shortlist Ã— 3 runs each, every run publishing real
                  translated Tagalog) â€” docs/reports/BENCHMARK_REPORT.md +
                  docs/reports/TEST_REPORT.md

Status:           Validated baseline for the current release (one authoritative
                  configuration shared by App + benchmark). Real-application
                  validation (YouTube/Chrome, VLC, Zoom) is deferred per user;
                  defaults may be revisited after Phase 2.
```

## Current Focus

**v0.5.46 â€” auto-reconnect overlay refresh (540k hide-on-reconnect regression, CLOSED 2026-08-22).** The pipeline's auto-reconnect cycle worked (goAway â†’ setupComplete) but the overlay kept the previous session's last words stuck until the user clicked "Show Captions" or stopped/started the app. Two coordinated fixes:
- `CaptionPipeline.SessionResumed` event raised from `RestartLiveTranslationAsync` after the new engine is attached and `SyncLiveTranslationSession` has aligned the caption service; fires once per recoverable `SessionEnded`.
- `IOverlayService.Refresh()` (synchronous `Dispatcher.Invoke(Render)`) plus `ControlWindow.OnPipelineSessionResumed` calling `_captions.ClearCaptionContent()` then `_overlay.Refresh()`, so the overlay clears the stale active line + history immediately and the new session's first partial renders without any manual click.
**Validation:** full suite **532/532** (106 Audio + 69 Captions + 174 Speech.Gemini + 183 App), Release build 0 warnings / 0 errors. Two real-app reconnect cycles at 00:50:10 and 00:59:10 each show `SessionResumed â†’ ClearCaptionContent + Refresh â†’ Render(historyBlocks=0)` followed by the new session's first partial rendering correctly (trace at `%TEMP%\uc_540k_trace.log`; instrumentation removed post-verification). Operator note: the fix is currently delivered via the **Debug build**; Release packaging deferred to the next release gate. CHANGELOG v0.5.46.

**Slice 11 â€” native-streaming segment-boundary tuning (COMPLETE 2026-08-05: decision recorded â€” keep 8 s).** Additive `sttnative` benchmark improvements (`timeBeginPeriod(1)` realtime-feed pacing fix â†’ valid ~1.1Ã— controlled pacing; mid-sentence-split metric in gate table + CSV) + controlled sweep at max-segment 8/10/12 s (small int8, tl, hangover 0.7 s fixed) on the actual video audio vs the `fil-orig` reference. Results: WER 32.6%/33.2%/30.0%, cadence 13.3/10.8/9.1 FINALs/120 s, splits 31%/42%/45%, 0 partials; latency/backlog bounded at all three caps (~5 s behind segment end). **Longer segments do NOT reduce mid-sentence splits; they cost responsiveness and add end-of-audio cap hallucinations (10 s `Pag-pag-pagâ€¦`, 12 s truncated `tunog`); the 12 s WER gain is a boundary artifact.** **Decision: keep `MaxSegmentDuration = 8 s` â€” no production or knob-default change** (real-App 8 s evidence from Slice 10 stands). Worker protocol / ggml-base / windowed engine untouched. 357/357 tests, Release build 0 warnings/0 errors, format clean. Scoped in `docs/CHANGE_IMPACT_ANALYSIS.md` Entry 12; evidence in `BENCHMARK_REPORT.md`/`TEST_REPORT.md` (Slice 11), CHANGELOG v0.5.20.

**All MVP slices (0â€œ6) remain complete.** **Argos pre-warm landed 2026-08-02** (v0.5.9): background pre-warm warms one shared Argos process/model off the real-caption path, so the first caption drops from ~23â€œ30 s cold start to ~3.8â€œ6.85 s (warm translation ~0.46 s), verified live through the real App (Cases A + B: single process spawn + single model init, no duplicate init, no lost first caption; 260/260 tests). **Slice 7 â€” caption overlay layout & stable incremental rendering (2026-08-02)**: a layout probe confirmed the caption `TextBlock` already uses the full ~522 px viewport width correctly (short lines stay one line; long text wraps only on width exhaustion â€” the reported "whole text re-flows" is not a width bug), and the render path now does scope-stable incremental rendering (a Partial only rewrites the live block's text in place; history blocks reused by identity, never rebuilt) with bottom scroll re-anchoring limited to when a new block is inserted and content overflows.

**ADR-0007 Option B â€” boundary-preserving fallback (2026-08-04, in progress toward acceptance):** the streaming commit path was the last quality gap (premature `At gusto ko` / `Kaya` / `country can do for` fragments). Implemented + unit-tested (**284/284**) and validated live against **JFK (controlled English verification, PASS)** â€” single + continuous runs through the real App no longer emit the pre-fix `country can do for` interior fragment and Stop drain preserves finals. **The original Tagalog recording scenario (`At gusto ko` / `Kaya` / `artipisyal na katalinuhan`) is the remaining acceptance evidence and is Pending** â€” the original operator recording is not available in the workspace; per user, no substitute Tagalog sample may be used to claim acceptance. Implementation frozen; ADR-0007 stays `Proposed` until that live evidence exists. **Phase 2 real-app validation (YouTube/Chrome, VLC, Zoom) stays deferred per user.**

**Slice 8 â€” Tagalog STT-vs-Committer isolation + model selection (2026-08-04, recorded):** the reported Tagalog defects were isolated to the STT layer, not the committer. RAW Whisper segments already contain the misrecognitions (`Kung usta?`, `Ikao.`, `Salaman.`), hallucinated `1.` segments, and short fragment boundaries; the committer aggregates them faithfully. Real-App comparison across all three local models on the same Tagalog slice (STT `tl`, frozen config): **base ~3.1 s** STT latency but weak accuracy; **tiny ~1.75 s** fastest but no accuracy gain and worst fragmentation; **small** best Tagalog accuracy (`Kumusta`/`Ikaw`/`Salamat`/`Juan` all correct, no `1.` hallucination) but **16.9â€œ21.9 s** latency â€” cannot keep real-time pace. **No available whisper.cpp local model gives both Tagalog quality and responsiveness.** ADR-0007 is NOT implicated (this is model-selection, not commit/boundary behavior). Evidence: `artifacts/samples/raw_vs_committed_tagalog.log` + `realapp_{tiny,base,small}_tagalog.log`; findings in `BENCHMARK_REPORT.md` (Slice 8) + `TEST_REPORT.md`.

**Faster-whisper selectable STT engine (2026-08-04, recorded):** the Slice 8 gap was closed by adding a **parallel faster-whisper STT path** (`UC_STT_ENGINE=fasterwhisper`) behind the same streaming engine boundary â€” the whisper.cpp decode portion was extracted to the `ISTTDecoder` seam with **zero behavior change** to the default `ggml-base` path (293/293 tests). A persistent binary-framed Python worker (`Server/faster_whisper_worker.py`, model loaded once, `small` int8, 8 threads, beam 5, `condition_on_previous_text=False`) drives `FasterWhisperDecoder`. **Real-App validation (same 90 s Tagalog slice, STT `tl`, frozen config st2/8 s/0.5 s/0.5 s) confirmed the target: whisper-small-level Tagalog accuracy with no `1.`/`one` hallucination at lower latency than whisper.cpp small** â€” STT latency 10.7â€œ11.7 s (vs small 16.9â€œ21.9 s), first final 16.5â€œ29.9 s, 3â€œ4 clean bilingual finals. A 1.5 s-interval variant gave the cleanest complete sentences (first final 16.5 s â‰ˆ base 17.5 s). Two wire-protocol bugs (`0x46574355` magic endianness; 16â†’20-byte segment header) were found and fixed during the real-App run (unit-test fakes did not exercise the wire format). Evidence: `artifacts/samples/realapp_fasterwhisper_small_tagalog.log` (+ `_int1_5_` variant); findings in `BENCHMARK_REPORT.md` + `TEST_REPORT.md`.

**Faster-whisper default-selection decision-gate â€” CLOSED: NOT promoted (2026-08-04).** The promotion candidate was measured against the frozen default on startup + steady-state latency. Worker cold start: spawn 0.006 s + Python import/model load **2.6 s** + first 8 s-window decode 2.5 s. Real-App (same 90 s Tagalog slice, STT `tl`): faster-whisper `small` first caption **16.5â€œ17.4 s** (better than ggml-base's measured 25.0 s) but steady-state STT latency **13.7â€œ15.8 s** vs ggml-base **2.4â€œ3.7 s**. Window/interval tuning does not close the steady-state gap (1.0 s interval â‰ˆ no change; 1.5 s worse at 24.2 s; 4 s window produced no captions) â€” the frozen 8 s/0.5 s config is already near-optimal for the faster-whisper path. Pre-warm would save only ~2.6 s. **Decision per user: `ggml-base` stays the production default; faster-whisper `small` int8 remains opt-in (`UC_STT_ENGINE=fasterwhisper`) until its steady-state latency can be materially reduced.** Accuracy winner: faster-whisper; responsiveness winner: ggml-base. Clean close: no production change, no forced promotion; the Tagalog accuracy gap on the ggml-base default remains acknowledged as open. Evidence: `artifacts/samples/firstcaption_{fw_small,i1_fw_small,base,w4_fw_small}.log`; findings in `BENCHMARK_REPORT.md` (Slice 9 decision-gate) + `TEST_REPORT.md`.

**TD-005 â€” settings persistence CLOSED (2026-08-05).** The user-facing preferences now survive restart:
per-user JSON at `%LocalAppData%\UniversalCaptions\settings.json` (in-box `System.Text.Json`, atomic
`.tmp` â†’ `File.Move(overwrite)`, unknown fields ignored, missing/malformed â†’ safe defaults). The six
persisted categories: audio source device, speech language, translation on/off + target, overlay
appearance (opacity/font/click-through), overlay placement, overlay view state. Engine/env knobs
(`UC_STT_*`, Argos/Python, model) stay env-driven â€” never persisted. `App.xaml.cs` loads before window
construction; `ControlWindow` applies + coalesced-dispatcher-saves (close flush); `CaptionOverlayWindow`
seeds and saves placement/view state. **335/335 tests passing** (6 new `SettingsStoreTests`), Release
build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean. **TD-002 stays frozen/Open** until
the real hotplug acceptance test can be run; no change to ADR-0007 / model selection / the resampler.

## Architecture Status

Approved: .NET 8 + WPF + NAudio + **Gemini Live as the single STT + translation engine behind `ILiveAudioTranslationEngine` (ADR-0011, 2026-08-21)**. Local Whisper (ADRs 0003/0005) and Argos Translate (ADR-0006) are removed from the solution; those ADRs remain as historical records superseded by ADR-0011 for the shipped architecture. Pipeline layers per `ARCHITECTURE.md`.

## Platform Status

Windows 10 target (build 17763+). Development environment: Windows with .NET SDK 8/10. NAudio 2.2.1 restored. No VB-CABLE. No local model binaries or Python runtimes anywhere in the stack (ADR-0011); the only runtime dependency beyond .NET is the Gemini Live API + a user-supplied free API key stored in Windows Credential Manager.

## Current Blockers

**Original Tagalog recording for ADR-0007 acceptance** â€” the live evidence for the `"At gusto ko"` / `"Kaya"` / `"artipisyal na katalinuhan"` regression requires the original operator recording, which is unavailable in the workspace; per user, no substitute sample qualifies. ADR-0007 remains `Proposed` until it is supplied and validated through the real App (fragmentation, duplicates, missing words, Stop drain).

## Next Milestone

**v0.5.46 release gate:** the Debug build is verified PASS for the 540k auto-reconnect overlay refresh (trace in `%TEMP%\uc_540k_trace.log`, instrumentation removed post-verification). Remaining: a real-app smoke on the Release artifact (loopback → captions appear → translation toggle ON/OFF mid-session → target-language switch recycles the session → goAway recovery with overlay refresh), then `packaging/build-package.ps1 -Version 0.5.46` + `inspect-package.ps1` verification and the GitHub release. v0.5.44's `inputTranscription` release gate remains CLOSED (PASS, 2026-08-21) — see Current Sprint and TEST_REPORT.

Prior milestone for the record â€” **corpus-driven phrase-guard validation â€” CLOSED 2026-08-14
(decision: INSUFFICIENT EVIDENCE â€” do not ship).** See Current Sprint for the measured
reductionâˆ’over-join table and the established conclusions
(bare-word allowlist = reject; English equivalents = no net benefit; phrase guard = technically reduces
observed Cat 2 failures; same-surface ambiguity = irreducible with lexical info alone; frequency-weighted
real-world cost = unknown). **Production gate unchanged; no v0.5.41; the 49 matrix tests unchanged.**
The only thing that would justify Ship is a **frequency-weighted natural-corpus validation**: a
naturally occurring annotated corpus (NOT more hand-constructed examples) measuring per candidate
`false-splits-prevented / applicable continuation boundaries` and `false-joins / applicable sentence
boundaries` â€” with the frequency-weighted cost reported, not just example counts (e.g. if
`At pagkatapos` occurs 100 times naturally as 70 continuations + 30 sentence starts, appending all 100
creates 30 over-joins regardless of the apparent continuation win). Do NOT keep expanding the lexical
phrase list before that frequency question is answered. Full results:
`docs/implementation/investigations/phrase-guard-validation.md`.

Prior milestone for the record â€” **v0.5.40 â€” Gemini streaming-caption segmentation investigation +
matrix (COMPLETE 2026-08-14; decision: production gate unchanged).**
Separately tracked from the resolved v0.5.39 `goAway`/session-lifecycle fix (closed + released; NOT a
v0.5.39 defect). **Issue:** Gemini streaming segmentation can emit a mid-sentence fragment right after a
`FINAL`, e.g. `FINAL "Nabasa mo na ang job description."` â†’ `FRAG init " at halos tugma"` â†’ `FINAL
"at halos tugma ito."`. **Diagnosis (evidence-based, traced against `GeminiLiveTranslateEngine`):**
**(1) Gemini (primary, non-deterministic):** run 1 delivered `" description."` as a fragment carrying a
mid-sentence period, then streamed the true continuation `" at halos tugma"` (leading whitespace +
lowercase) as a new `ServerContent` fragment; the **same audio in run 2** produced one clean FINAL,
confirming Gemini's segmentation is not stable. **(2) Our engine (secondary):** the flush gate
(`GeminiLiveTranslateEngine.cs:434â€“440`) commits a FINAL whenever a new fragment arrives while the
accumulator ends in punctuation â€” no continuation heuristic (only cumulative restatements are rejected);
the premature flush happens before the fragment reaches `Accumulate`/`IsCumulativeRestatement`, so
classification is not the cause. **(3) Idle timer: not responsible** (1.5 s ARM-IDLE armed at 2.448 s
would fire ~3.95 s; the fragment arrived at 3.701 s and the flush was `reason=sentence-boundary`; run 1
has 0 idle-timeout FINALs without terminal punctuation). **(4) Repro variance:** `gemini_seg_trace.log`
lines 112â€“131 (cut) vs `gemini_seg_app_stderr.log` lines 1844â€“1862 (clean same-audio run). **Candidate
fixes (not implemented):** Option A (recommended) continuation guard â€” accumulator ends in punctuation
but incoming fragment begins with whitespace+lowercase â†’ append, not flush (implemented as the v0.5.40
fix, which closed the lowercase case); Option B stronger linguistic continuation heuristic (more
coverage, more wrongly-join-two-sentences risk); Option C remove punctuation-based immediate flush
(not preferred first â€” punctuation gives useful responsiveness). **Matrix executed 2026-08-14**
(see Current Sprint): 48 runs, **41 PASS / 7 FAIL**; Cat 2 capitalized continuation idioms are the
7 RED (measured gap), Cat 3 bare starters are provably ambiguous from the fragment alone, Cat 1/Cat 4
stay green. **Decision: production gate unchanged** â€” a simple `At|Kaya|Sige|Hindi â†’ APPEND`
allowlist is unsafe (Cat 3 over-join); phrase-level idiom guard remains a candidate pending a
corpus-driven validation establishing **false-split reduction âˆ’ over-join cost**.
**Explicitly out of scope:** Whisper, the translation engine, partial-rendering UX (v0.5.38 two-tone),
the goAway lifecycle. Evidence preserved: `gemini_seg_trace.log` / `gemini_seg_trace_run2.log` /
`gemini_seg_app_stderr.log` / `acceptance-gemini-seg-trace.ps1` (untracked). See ROADMAP.md (matrix CLOSED).

**v0.5.40 investigation + matrix COMPLETE 2026-08-14 (20-run real-Gemini study + 48-run
decision-gate matrix; tracer removed after; no production code change).** Result: Gemini streams ~1
fragment/s (median 1000 ms, p90 1244 ms); the app pipeline adds zero latency (FINALâ†’COMMITâ†’RENDER all
0 ms median / 1 ms p90); first visible caption median 8.72 s (primary) / 9.71 s (secondary) â€”
dominated by STT first-FINAL + Gemini first-token; **no app-side latency to optimize.** The v0.5.40
lowercase guard only catches lowercase continuations â€” capitalized mid-sentence continuations
(`Hindi Lunes.`, `At pagkatapos`, `Sige.`) still split: same-audio "â€¦Friday, not Monday" split in
**6/10 runs**, "â€¦plan. At pagkataposâ€¦" in **5/10**; fragmentary captions (len<15) rise to **9.8 %** on
the boundary-stress clip (vs 2.2 % primary); **under-segmentation (two real sentences joined) also
occurs â€” so a more aggressive guard is NOT an automatic win.** The decision-gate matrix (48 runs)
confirmed Cat 1/Cat 4 stay green, Cat 2 = 7 known capitalized-continuation false-splits (RED), Cat 3
bare starters provably ambiguous. **Decision: production gate unchanged; phrase-level guard stays a
candidate pending a broader corpus validation.** Evidence: BENCHMARK_REPORT.md (Gemini Streaming-Caption
Segmentation Study), investigations/gemini-segmentation.md, `gemini_seg_study\` (untracked). Gate:
651/651 tests, Release 0 warnings/0 errors, `dotnet format` clean.

**Core is done (per user criterion, 2026-08-06):** the final real-world acceptance run passed at the
production default â€” stable ~32â€œ33% STT + ~1% App CPU over 300 s continuous media, first caption ~3.2 s,
live partials on the overlay, live enâ†’tl translation, bounded history, clean exit, 0 orphans. No further
CPU optimization. Remaining work is feature-level / product-level, not core architecture: **Phase 2
real-app validation (YouTube/Chrome, VLC, Zoom) stays deferred per user**; ADR-0007 stays `Proposed`
until the original operator Tagalog recording is supplied; TD-002 stays **frozen/Open** until the real
hotplug acceptance test can be run.

**Slice 11 â€” native-streaming segment-boundary tuning (COMPLETE 2026-08-05: decision recorded â€” keep 8 s).** Additive `sttnative` benchmark improvements (`timeBeginPeriod(1)` realtime-feed pacing fix â†’ valid ~1.1Ã— controlled pacing; mid-sentence-split metric in gate table + CSV) + controlled sweep at max-segment 8/10/12 s (small int8, tl, hangover 0.7 s fixed) on the actual video audio vs the `fil-orig` reference: WER 32.6%/33.2%/30.0%, cadence 13.3/10.8/9.1 FINALs/120 s, splits 31%/42%/45%, 0 partials; latency/backlog bounded at all three caps (~5 s behind segment end). **Longer segments do NOT reduce mid-sentence splits, cost ~46% responsiveness at 12 s, and add end-of-audio cap hallucinations (10 s `Pag-pag-pagâ€¦` stutter, 12 s truncated `tunog`); the 12 s WER gain is a boundary artifact. Decision: keep 8 s â€” no production or knob-default change** (real-App 8 s latency/backlog evidence from Slice 10 stands). 357/357 tests, Release build 0 warnings/0 errors, format clean. Scoped in Entry 12; evidence in BENCHMARK_REPORT/TEST_REPORT (Slice 11), CHANGELOG v0.5.20. **Slice 10 is complete (close-out 2026-08-05)** â€” `FasterWhisperNativeStreamingEngine` + `SpeechSegmentDetector` behind `UC_STT_ENGINE=fasterwhisper-native`, benchmark + real-App validation PASSED (WER 32.6%, 13.3 FINALs/120 s, first caption 15.2 s, ~4 s behind segment end); faster-whisper stays opt-in, ggml-base default unchanged. **Slice 6 is complete (close-out 2026-08-01)** (E2E metric, OFAT sweep + shortlist in `BENCHMARK_REPORT.md`, App-level SAPI E2E validation; baseline `base/8/1/st2` promoted to the App default â€” `StabilityWindow` 3â†’2, model `ggml-base` unchanged). **All MVP slices (0â€œ6) are complete.** **Argos pre-warm closed out 2026-08-02** (v0.5.9) â€” first-caption latency ~23â€œ30 s â†’ ~3.8â€œ6.85 s, verified live. **Slice 7 (caption overlay layout & stable incremental rendering) closed out 2026-08-02** â€” tests 267/267 (see CHANGELOG v0.5.10). **ADR-0007 Option B implemented + unit-tested (284/284) + live JFK verification passed (2026-08-04); final acceptance gated on the original Tagalog recording (Pending).** Next work after acceptance is from the roadmap Future list and the deferred Phase 2 real-app validation (YouTube/VLC/Zoom) reassessment per user. See `docs/implementation/BUILD_PLAN.md` and `docs/implementation/ROADMAP.md`.

## Last Build

2026-08-21 — `dotnet build UniversalCaptions.slnx` succeeded (Debug + Release), 0 warnings, 0 errors. `dotnet test UniversalCaptions.slnx` passed **528/528** (106 Audio + 69 Captions + 174 Speech.Gemini + 179 App), `dotnet format --verify-no-changes` clean. **Gemini-only pipeline (ADR-0011) implemented:** local Whisper + Argos + Benchmarks projects removed; `ILiveAudioTranslationEngine` single-session STT+translation; packaging stripped to a ~145 MB flat publish; docs updated. See CHANGELOG v0.5.44.

Prior (2026-08-14): 651/651 on the pre-ADR-0011 suite (incl. since-removed Speech/Translation projects); v0.5.40 segmentation investigation COMPLETE — root cause identified, no production change. Prior (2026-08-13): v0.5.39 goAway fix 645/645 + real-app regression PASS; v0.5.38 two-tone partial rendering smoke PASS; v0.5.37 mixed-language history scrub smoke PASS. Prior (2026-08-12): runtime Gemini-toggle latency verification (measurement only); v0.5.33 translation parity 22/22. Details: CHANGELOG.md entries v0.5.33–v0.5.40.
