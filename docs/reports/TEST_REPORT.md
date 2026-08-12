# Universal Live Captions Test Report

Last updated: 2026-08-12

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Record test execution evidence for the current slice |
| Scope | Slice 1 — Audio Capture Spike, Slice 2 — STT Spike, Slice 3 — Translation Spike, Slice 4 — Caption Service (complete), and Slice 5 — Overlay + Control Window (complete) (automated unit tests + manual real-model verification) |
| Audience | Engineering |
| Owner | Engineering |
| Status | Active |
| Related Documents | [BUILD_PLAN.md](../implementation/BUILD_PLAN.md), [QUALITY_ASSURANCE.md](../QUALITY_ASSURANCE.md), [RELEASE_PLAN.md](../implementation/RELEASE_PLAN.md), [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md) |

---

## Summary

**Final real-world acceptance — v0.5.33 translation parity PASS (2026-08-12): 22/22 checks (Argos
11/11 + Gemini 11/11)** on the Release app over real WASAPI loopback (looped
`english_sustained_90s.wav`), driven by `acceptance-v0.5.33-translation-parity.ps1` (untracked).
Proven while captions are RUNNING, identically for both providers: Translate OFF → a **new** English
source caption appears (control toggle reads off, Whisper keeps capturing); Translate ON → target
language returns; target `tl → ja → tl` updates immediately with no Stop/Start; STT worker PIDs
stable across every toggle/target change. Gemini session spawned a **fresh worker set** after the
Argos set fully exited (`9776,22308` → `8288,22748`). Real CJK verified in the evidence file
(ノートブック / ありがとうございます / 来週のご来店をお待ちしております /
ゆっくり話すことを忘れないでください); Argos first translation request→result **0.088 s**; no
orphaned workers; clean exit. Three harness honesty fixes before the result was trusted (toggle-OFF
waits for a new non-translated caption with a word-boundary Tagalog regex; overlay badge is not
UIA-exposed so badge behavior stays in `CaptionDisplayPolicyTests` + a control-toggle assertion; the
fresh-worker-set check waits out the up-to-10 s worker Stop budget). **No product code changed for
the close-out.** Evidence: `v0533_parity_acceptance.log`, `v0533_app_stderr.log` (both untracked);
CHANGELOG v0.5.33.

**Common translation state is provider-agnostic (2026-08-12): full suite now 610/610 passing** (106
Audio + 78 Captions + 111 Speech + 42 Translation + 161 App + 112 Speech.Gemini), Release build 0
warnings / 0 errors, `dotnet format --verify-no-changes` clean. Design correction to v0.5.32 —
**`CaptionService.TranslationEnabled` / `TargetLanguage` now always reflect the user's Translate
checkbox + target for BOTH providers (Argos and Gemini); the provider decides only the translation
mechanism.** A new `SetCaptionLineTranslation` flag gates only the Argos caption-line path inside
`CaptionService` (its `ITranslationEngine` calls), while a live audio engine (Gemini) relays
translation-origin lines; `TranslationProviderPolicy.UsesCaptionLineTranslation` dropped its `enabled`
parameter so the policy can no longer drive UI state. **Runtime reconfiguration:** new
`CaptionPipeline.SetLiveTranslation(provider, source, target)` starts/stops/swaps the live Gemini
engine while a session is live — toggle off stops it + clears the translation active line (badge
clears, captions return to source, Whisper keeps running); toggle on / target change creates a new
engine; a failed swap (no API key) raises an error status without stopping Whisper. `ControlWindow`
sets the common state with the raw toggle and reconfigures the pipeline in the same pass. **Display:**
`CaptionDisplayPolicy` keys the badge and source-vs-translation display off the common state only.
Acceptance: `SetLiveTranslation` toggle-on/off/swap/no-op/pre-start (5), Argos caption-line
suppression restored + translation-origin relay + toggle-off drops in-flight content (6); policy +
display tests rewritten/expanded.

**Argos cold-start first-caption latency fix + production-wiring measurement (2026-08-09): full suite now 557/557 passing** (106 Audio + 72 Captions + 42 Translation + 111 Speech + 124 App + 102 Speech.Gemini), Release build 0 warnings / 0 errors, `dotnet format --verify-no-changes` clean. New additive benchmark leg `captionwire` drives the exact production composition (FasterWhisper native → `CaptionService` en→tl → single-gate `ArgosTranslationEngine`) and proves: Argos per-caption caller-visible p50 **0.27–0.30 s** (max 0.48 s) with a ~0 ms commit→translate queue (no backlog) and **E2E dominated by STT cadence** (first FINAL ≈ 11 s, E2E p50 ≈ 13 s). The ~18–20 s first-caption symptom reproduces **only on a cold Argos process** (python import ≈ 5.75 s + first-translate lazy stanza load ≈ 2.8 s inline); with pre-warm the first visible translated caption is **3.83 s**. Fix (code-behind): `ArgosTranslationEngine.TranslateAsync` awaits an in-flight same-target warm-up before issuing its own request (`AwaitInFlightWarmupAsync`), and `ControlWindow.OnLoaded` now pre-warms Argos at startup when translation is persisted (previously the `_initializing` toggle assignment skipped the pre-warm). Re-measured via the exact App startup path (`--prewarm-race`): first visible **12.0–12.2 s** (was 18.94 s), Argos max **0.48 s** (was 7.23 s inline-cold), 0 errors. 3 new `ArgosTranslationEngineTests` pin the await-in-flight behavior. CPU split measured per python worker: **STT ≈ 30–31 %, Argos ≈ 3.4–4.1 %** of the machine — the "Argos ≈ 51 % CPU" report is not supported. Evidence CSV: `artifacts/reports/captionwire/argos_wire_2026-08-09.csv`, `argos_wire_noprewarm_2026-08-09.csv`, `argos_wire_prewarmrace_fixed_2026-08-09.csv`.

**Gemini provenance spike diagnostics (2026-08-09): full suite now 557/557 passing** (106 Audio + 72
Captions + 111 Speech + 42 Translation + 124 App + 102 Speech.Gemini), Release build 0 warnings / 0
errors, `dotnet format --verify-no-changes` clean. **Spike/diagnostics layer only — the frozen
production Gemini channel/protocol/engine (A1–A6) is untouched.** The direct-wire spike now wraps the
real channel in a `ProvenanceObservingChannel` decorator that proves the caption text comes from
Gemini's own generated-audio `outputAudioTranscription` side-channel, not from a Whisper→Argos path:
`serverContent`/`modelTurn`/`parts`/`inlineData`/`outputTranscription`/`turnComplete` frame counts +
`GeminiAudioFrames`/`GeminiAudioBytes`/MIME metadata (never payload bytes) with `ArgosCalls = 0` /
`ArgosOutput = NONE` pinned per utterance; spike exits 71 when provenance is not verified. 11
deterministic `ProvenanceObservingChannelTests` + 11 `AbRegressionHarnessTests` (hand-crafted frames &
fictional transcripts, no API key/network). Evidence + next gate in
`docs/spikes/GEMINI_MODEL_DISCOVERY.md` and CHANGELOG v0.5.30.

**Installer & distribution + `UC_FW_MODEL` (2026-08-06): full suite now 384/384 passing** (77 Audio + 72 Captions + 111 Speech + 27 Translation + 97 App), Release build 0 warnings / 0 errors, `dotnet format --verify-no-changes` clean. Two new `SpeechEngineFactoryTests` cover the approved additive seam: `NativeModel_Unset_DefaultsToSmall` and `NativeModel_Override_IsRespected` (worker receives `--model <path>`). The frozen v0.5.25 core is otherwise untouched — the rest of this entry is packaging/launcher-only. **Installed-bundle acceptance PASS** (see the Installer section below): clean install exit 0 to `%LocalAppData%\UniversalCaptions` (1,634.5 MB, Setup.exe 795.5 MB); worker cmdlines verified installed-only (`py\python.exe … faster_whisper_worker.py --model <install>\models\faster-whisper-small --compute int8 --threads 4 --beam-size 5`; Argos server on the same bundled python — no `%TEMP%\fwv`/`argosv`, no `huggingface`, no repo refs); real audio via WASAPI loopback produced live partials + committed translated Tagalog (`EN || TL` badge) with first caption ≈4.1–4.7 s warm; settings persist; clean Start/Stop/Exit 0 orphans; **clean uninstall exit 0 leaving only the app's own `settings.json`** (`PYTHONDONTWRITEBYTECODE=1` prevents stdlib `.pyc` leftovers).

**Entry 15 — overlay live-line integration (2026-08-06, ADR-0008 follow-up): full suite now 374/374 passing** (77 Audio + 72 Captions + 109 Speech + 27 Translation + 89 App), Release build 0 warnings / 0 errors, `dotnet format --verify-no-changes` clean. The WPF overlay previously painted **committed FINALs only** — commit `7d1c057` ("temporary diagnostic tracer", 2026-08-03) had replaced Slice 7's active-line painting (`_activeBlock` was never assigned; `CaptionRenderIdentityTests` asserted `ActiveBlock() == null`). This slice restores the live active line and feeds it the `fasterwhisper-native` partial stream so the promoted default is actually visible as it happens: `CaptionOverlayWindow.UpdateCaptionItems` creates one mutable `_activeBlock`, rewrites its text in place on later partials, removes it when `model.ActiveLine` is null (committed/stopped/hidden-while-translating); `ReconcileHistory` reuse-by-sequence and the `shouldUpdate` gate (holds during translation-pending so no source-language flash) are unchanged. `CaptionRenderIdentityTests` rewritten 4→6 (partial rewrites same block identity; growing stream paints one block with no history churn; no partial ever enters committed history; FINAL freezes active into history and removes the active block; cleared active line removes block and keeps history; finalized blocks keep text instances and order). **Real-App smoke verification PASS (Entry 14 nine-point checklist + overlay AC-1..AC-8)**: promoted default spawns the faster-whisper worker and paints live partials in the active line (`meeting sum` → `Meeting someone.` → FINAL freeze, single block `n1` throughout); first visible partial ≈5.6 s after capture start; FINALs freeze into history with no churn; POSTSTOP_1..3 probes (1.2 s apart) prove Stop/Dispose clears the active line and leaves no stale partial; tl→en Argos verified as the documented-unsupported direction (stanza SBD, no `tl` processor) with correct graceful degradation to source text, and **en→tl verified as the supported direction end-to-end** — English audio → native STT → Argos en→tl → live-translated Tagalog active line, translation visible on the overlay before commit (T5 first request 3.610 s, T6 first result 6.847 s, first translated caption painted ~11.5 s). App CPU variable (~0–66%, decode-bound spikes), faster-whisper worker CPU ~0% (worker idles between requests). **Both entries now honest in this report — no earlier smoke claim of a translated/captioned overlay without verification.** Decision records: Entry 15, CHANGELOG v0.5.23.
**Entry 14 — production default promotion (2026-08-05, ADR-0008): full suite now 372/372 passing** (77 Audio + 72 Captions + 109 Speech + 27 Translation + 87 App), Release build 0 warnings / 0 errors. The App's STT engine selection moved into a testable `SpeechEngineFactory`: default / `UC_STT_ENGINE=fasterwhisper-native` → `FasterWhisperNativeStreamingEngine` + live partials (interval 1 s, window 4 s, 8 s segment cap frozen); `UC_STT_ENGINE=ggml-base` → the original local-Whisper engine (explicit fallback); `UC_STT_ENGINE=fasterwhisper` → the unchanged windowed engine. **No automatic runtime fallback** (ADR-0003 no-silent-switch). New `SpeechEngineFactoryTests` (5) verify the selection table deterministically (side-effect-free constructors, no Python/model). Faster-whisper worker protocol, the windowed engine, ADR-0007, TD-002, and TD-005 are untouched. Decision records: ADR-0008, Entry 14, CHANGELOG v0.5.22.
**Slice 12 — faster-whisper native-streaming live partials (2026-08-05): full suite 367/367 passing** (77 Audio + 72 Captions + 109 Speech + 27 Translation + 82 App), Release build 0 warnings / 0 errors, `dotnet format --verify-no-changes` clean. New live-partial layer on the `fasterwhisper-native` engine (bounded trailing-window re-decodes at a configurable cadence, at most one partial decode in flight/queued) — `SpeechSegmentDetector.TryGetPartial` + `FasterWhisperEngineOptions.PartialDecodeInterval` (default 0 = FINAL-only preserved) / `PartialDecodeWindow` (4 s) + `PartialTranscriptAvailable`. 10 new deterministic tests (6 detector + 4 engine, incl. a `BlockingFasterWhisperProcess` fake proving the single-in-flight bound). **Controlled real-audio benchmark PASS:** small int8, tl, hangover 0.7 s, max 8 s, realtime feed, translation OFF, `--partial-interval 1 --partial-window 4` on `uc_video_full_16k.wav` (288.79 s) vs `fil-orig` — first visible partial **5.59 s after speech onset** (vs first FINAL 15.0 s), **19.5 partials/120 s**, active line increments while speaking, FINAL stream **text-identical to Slice 11** (no accuracy regression, WER 33.19% in-harness), FINAL ~6 s after segment close, backlog bounded (plateau ~50 s vs 43 s FINAL-only), realtime-safe 1.18×, nothing dropped/reordered. **Decision: PASS — ggml-base stays the production default; partials default off** (superseded by Entry 14 promotion). See the Slice 12 section below.
**Slice 11 — native-streaming segment-boundary tuning (2026-08-05): decision recorded — keep `MaxSegmentDuration = 8 s`.** Additive benchmark improvements (`timeBeginPeriod(1)` realtime-feed fix → valid controlled pacing ~1.1×; mid-sentence-split metric). Controlled `sttnative` sweep at max-segment 8/10/12 s (small int8, tl, hangover 0.7 s fixed): WER 32.6%/33.2%/30.0%, cadence 13.3/10.8/9.1 FINALs/120 s, splits 31%/42%/45%, 0 partials. **Longer segments do NOT reduce mid-sentence splits, cost responsiveness, and add end-of-audio cap hallucinations (10 s `Pag-pag-pag…`, 12 s truncated `tunog`); latency/backlog bounded at all three caps. Decision: keep 8 s — no production or knob-default change; worker protocol / ggml-base / windowed engine untouched.** Evidence: `BENCHMARK_REPORT.md` (Slice 11), Entry 12, CHANGELOG v0.5.20. Full suite **357/357 passing** (77 Audio + 72 Captions + 99 Speech + 27 Translation + 82 App), Release build 0 warnings / 0 errors, `dotnet format --verify-no-changes` clean.
**Slice 10 — faster-whisper native streaming (deterministic phase + benchmark/real-App validation, 2026-08-05): full suite now 357/357 passing** (77 Audio + 72 Captions + 99 Speech + 27 Translation + 82 App), Release build 0 warnings / 0 errors, `dotnet format --verify-no-changes` clean, no vulnerable packages. New `FasterWhisperNativeStreamingEngine` (one FINAL per completed speech segment, C#-side VAD, no live partials) + internal `SpeechSegmentDetector` state machine behind `UC_STT_ENGINE=fasterwhisper-native`; the ggml-base default, the windowed `fasterwhisper` engine, the worker wire protocol, and ADR-0007 are all untouched. New `SpeechSegmentDetectorTests` (11) + `FasterWhisperNativeStreamingEngineTests` (11) are fully deterministic (synthetic PCM + scripted VAD/worker process, no Python). Fresh-context review PASSED with fixes (segment-duration accounting across hangover resumes; per-session epoch guard so a decode that outlives Stop cannot bleed a stale FINAL into a restarted session; broadened Start error mapping; option validation; engine-level max-segment cap test). **Benchmark + real-App validation PASSED:** committed WER **32.6%** vs `fil-orig` (ggml-base 51.2%), **0 partials (FINAL-only)**, commit cadence **13.3 FINALs/120 s** (windowed faster-whisper: 2/120 s), first real-App caption **15.2 s**, STT latency ~4 s behind segment end with no growing backlog; accepted with the documented tradeoff that the 8 s segment cap can split sentences mid-word; faster-whisper stays opt-in and the ggml-base default is unchanged. See the Slice 10 section below.

**TD-005 closed - settings persistence (2026-08-05): full suite now 335/335 passing** (77 Audio + 72 Captions + 77 Speech + 27 Translation + 82 App), Release build 0 warnings / 0 errors. New `SettingsStoreTests` (6) deterministically cover save/load round-trip, missing file → defaults, malformed/wrong-type → safe defaults, unknown/new fields ignored (forward compatibility), atomic `.tmp` → `File.Move(overwrite)` with failed-write preserving the last good file, and concurrent/rapid saves settling without torn state. See the TD-005 section below.

**TD-016 closed - `LineProtocolFasterWhisperProcess` protocol-contract suite (2026-08-04): full suite now 302/302 passing** (66 Audio + 72 Captions + 77 Speech + 27 Translation + 60 App), Release build 0 warnings / 0 errors. A fake-worker fixture emits exactly the production wire format over an in-memory stdout stream (no Python/venv/model); the real production reader is exercised unchanged through a new internal injectable-stream constructor seam (`StartAsync` skips the real spawn; `WriteRequestAsync` no longer requires a live `_process`). New `LineProtocolFasterWhisperProcessProtocolTests` (9) deterministically guard the two Slice 9 wire bugs (magic `0x46574355`; 20-byte segment header) plus: request-header layout/int16 PCM, wrong-magic rejection, 20-byte-header-does-not-consume-payload, two-segment ordering, fragmented pipe reads, truncated segment/response header -> deterministic protocol error, multi-byte UTF-8 payload boundary. Isolated to the opt-in faster-whisper path; `ggml-base` default untouched (see TD-016 section).

**Faster-whisper selectable STT engine (Slice 9, 2026-08-04): full suite now 293/293 passing** (66 Audio + 72 Captions + 68 Speech + 27 Translation + 60 App), Release build 0 warnings / 0 errors; the whisper.cpp decode was extracted to the `ISTTDecoder` seam with zero behavior change to the frozen `ggml-base` default, and a persistent binary-framed Python worker drives `FasterWhisperDecoder` (`UC_STT_ENGINE=fasterwhisper`). Real-App validation PASS on the 90 s Tagalog slice: faster-whisper `small` int8 gave whisper-small-level accuracy with **no `1.`/`one` hallucination** at STT latency **10.7–11.7 s** (whisper.cpp small was 16.9–21.9 s); two wire-protocol bugs (magic endianness; 16→20-byte segment header) were surfaced only by the real-App run and fixed. **`ggml-base` remains the frozen default; faster-whisper is opt-in** (see Slice 9 section). Slices 1–5 automated tests pass: **253/253 passed, 0 failed, 0 skipped** (66 Audio + 71 Captions + 45 Speech + 21 Translation + 50 App). Solution builds with **0 warnings, 0 errors** (warnings-as-errors). A post-close-out refinement (change-impact Entry 7) adds **live active-line translation** (single in-flight slot, instance-identity stale-guard, disabled-mid-flight results discarded) and a **Chrome-style overlay redesign** (auto-sized translucent pill, white text, target-language badge, expand/collapse chevron, hide button) with a ControlWindow "Show Captions" button; its automated tests are complete (**238/238** for Slice 6 Phase 1a; Entry 7 itself was 224/224) and its **manual verification with real audio + real Argos is complete (2026-08-01)** — the in-progress overlay line reads Tagalog before it commits (see Slice 5 refinement note). **Overlay caption display fixed (2026-08-01):** `CaptionDisplayPolicy` renders the committed history chronologically (oldest at top, newest at the bottom), and the overlay's hard height caps (`HistoryList MaxHeight` + window `MaxHeight`) were removed so the auto-sized pill grows to fit every rendered line — the newest committed caption and the highlighted/current caption are never clipped or covered (the active line occupies its own layout row, separate from the history). Deterministic display-policy tests cover first-caption, chronological ordering, newest-at-bottom, capacity eviction (oldest removed from the top), and partial→final append with no duplication; build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean. **Slice 6 (Phases 1a–1c) is complete (close-out 2026-08-01)** — E2E latency metric + tests (238/238), OFAT sweep + shortlist in `BENCHMARK_REPORT.md`, and the App-level SAPI E2E validation recorded below (baseline + shortlist configs × 3 runs each through the real App: WASAPI loopback → Whisper → Argos en→tl → overlay, E2E latency row polled via UIA, every run publishing real translated Tagalog). The validated baseline `base/8/1/st2` is the App default (`StabilityWindow` 3→2, model `ggml-base` unchanged). Phase 2 real-app validation remains deferred per user. Slice 1 manual verification against real system audio succeeded. Slice 2 real-model verification succeeded: `WhisperSpeechToTextEngine` streamed **partial and final transcripts** from four samples through the real ggml-tiny/base models at realtime pacing with a clean stop/dispose (see [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md)). Slice 3 real-Argos verification succeeded: `ArgosTranslationEngine` translated **offline/local** through a real Argos 1.11.0 child process for direct pairs (`en→tl`, `ja→en`, `en→ja`) and a pivot pair (`ja→tl` via `en`), with correct error mapping (see below and [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md)). Slice 4 (complete): `CaptionService`/`CaptionState` verified with deterministic fake translation engines — partial→active→final→committed transitions, translation on/off, translation failure preserving the source caption, ordering, bounded history, and cancellation. Slice 5 (complete): `UniversalCaptions.App` overlay display policy + pipeline wiring verified with deterministic fakes (`CaptionDisplayPolicyTests` 8 + `CaptionPipelineTests` 20 + `AudioSourceLoaderTests` 4 + `TranslationGuardTests` 4) — Q1 display policy resolution (active line = verbatim latest partial; finals = bounded history newest-first; translated text replaces source only when `Completed`), capture→processor→STT→caption-service wiring, error handling, lifecycle, audio-source enumeration (preferred default, failure-surfacing), and translation guard (source-equals-target rejection). **Manual overlay/device verification completed 2026-08-01** (all items Passed — see Slice 5 section below), including the **real-Argos wiring end-to-end through the App** (committed overlay lines translated to Tagalog by a real local Argos child process).

## Installer & distribution (2026-08-06)

### Purpose

Prove the frozen v0.5.25 core deploys to a clean Windows 10 machine with **no repository and no
network** — Inno Setup (per-user), self-contained .NET 8 win-x64, bundled pruned Python runtime,
bundled faster-whisper small model, bundled pruned Argos `en→tl` packages, wired by a launcher that
sets process-scoped env knobs. One approved additive production seam: `UC_FW_MODEL`.

### Automated tests — PASS

Full suite **384/384 passing, 0 failed, 0 skipped**, Release build 0 warnings / 0 errors.

```
Passed!  - Failed: 0, Passed: 77, Total: 77   - UniversalCaptions.Audio.Tests.dll
Passed!  - Failed: 0, Passed: 72, Total: 72   - UniversalCaptions.Captions.Tests.dll
Passed!  - Failed: 0, Passed: 111, Total: 111 - UniversalCaptions.Speech.Tests.dll
Passed!  - Failed: 0, Passed: 27, Total: 27   - UniversalCaptions.Translation.Tests.dll
Passed!  - Failed: 0, Passed: 97, Total: 97   - UniversalCaptions.App.Tests.dll
```

New `SpeechEngineFactoryTests` (2), no Python/model:

1. **`NativeModel_Unset_DefaultsToSmall`** — with `UC_FW_MODEL` unset, the native engine options model
   is `"small"` (identical to v0.5.25 behavior).
2. **`NativeModel_Override_IsRespected`** — with `UC_FW_MODEL=<dir>`, the native engine options model is
   `<dir>`, forwarded verbatim as worker `--model` (faster-whisper accepts a local directory → offline).

### Installed-bundle acceptance — PASS (real audio, real en→tl)

Run via `installer_acceptance.ps1` (untracked) against the app installed at
`%LocalAppData%\UniversalCaptions`, launched through `launcher.cmd`, English audio looped through VLC
over WASAPI loopback, overlay sampled by UIAutomation, worker cmdlines captured by CIM:

- **Clean install**: `UniversalCaptions-Setup-0.5.25.exe` (795.5 MB, lzma2/ultra) `/VERYSILENT` exit 0;
  installed size 1,634.5 MB. No UAC (`asInvoker`, `app.manifest` has no execution level).
- **Worker paths are installed-only**: STT `py\python.exe -u <install>\Server\faster_whisper_worker.py
  --model <install>\models\faster-whisper-small --compute int8 --threads 4 --beam-size 5`; Argos
  `py\python.exe -u <install>\Server\argos_translate_server.py`. Asserted: no `%TEMP%\fwv`,
  `%TEMP%\argosv`, `huggingface`, `artifacts\`, or repo references in any cmdline.
- **Captions**: first real caption ≈4.1–4.7 s (warm); live partials grow in place
  (`Maraming salamat.` → `TAGALOG na grupong nagsasanay.` → committed history); committed translated
  Tagalog lines with the `EN || TL` badge (`Ang pangalan ko ay Maria.`, `Ano ang pangalan mo?`,
  `Magandang umaga lahat.`); looped corpus repeats; POSTSTOP keeps history with no stale partial.
- **Settings persist**: `settings.json` (`language: en`, `translationEnabled: true`, `targetLanguage:
  tl`) honored across install/uninstall/reinstall cycles.
- **Lifecycle**: clean Start/Stop/Exit (25 s close budget), **0 orphaned app/worker processes**.

### Clean uninstall — PASS

`unins000.exe /VERYSILENT` exit 0; **only the app's own `settings.json` remains** at
`%LocalAppData%\UniversalCaptions` (user data preserved), no leftover `py` tree (the launcher sets
`PYTHONDONTWRITEBYTECODE=1`, so Python never writes stdlib `__pycache__` that Inno cannot track), no
leftover processes, no registry uninstall entry.

### Evidence

`docs/reports/INSTALLER_DISCOVERY.md` (§8 decisions, §9 build + acceptance), CHANGELOG v0.5.26,
`packaging/` (`.iss`, `launcher.cmd`, `build-package.ps1`, `output/`), `installer_acceptance*.{ps1,log,
csv,txt}` (untracked).

## App-by-app validation — Phase 2 (2026-08-06)

### Validation target

- **Build:** UniversalCaptions v0.5.26.
- **Source:** final `UniversalCaptions-Setup-0.5.25.exe` produced by the completed installer slice
  (Entry 17).
- **Location:** installed at `%LocalAppData%\UniversalCaptions`, launched via `launcher.cmd`.
- **Configuration (fixed):** `fasterwhisper-native` + live partials; small/int8 model; 4 STT threads;
  normal persisted settings (`language: en`, translation ON → `tl`); real WASAPI loopback; internet
  allowed **only** for the media sources; **no** repo/venv/model paths injected manually.
- **Not exercised against:** `dotnet run`, `bin\Release` directly, the development Python environment,
  the repo's model directory, or a manually configured `UC_FW_MODEL`.

### Network policy

> **Network policy:** Internet access permitted for external media sources (YouTube/Chrome and Zoom).
> UniversalCaptions itself remains offline/local-only; all speech recognition and translation are
> performed locally, with system audio captured through WASAPI loopback. Network activity from
> Chrome/YouTube/Zoom is **test-environment traffic**, not an application dependency.

### Matrix

| App | Status | Notes |
| --- | --- | --- |
| Chrome / YouTube | ✅ PASS | Real YouTube playback → live partials + en→tl committed lines, 0 orphans |
| VLC | ✅ PASS | Fresh + installer/final-acceptance evidence, live partials + en→tl, 0 orphans |
| Zoom | ⚠️ NOT VALIDATED | Environment-limited (Chromium UI, no automation surface, no meeting available) — see Results |
| Teams | **N/A** | Desktop Teams client unavailable |

> **Microsoft Teams — N/A:** The Teams desktop application is not installed on the validation machine.
> Only the Office add-in is present, which does not provide an equivalent Teams audio/capture
> environment. Therefore no Teams compatibility claim is made. If Teams compatibility becomes a
> requirement later, install the desktop client and add a separate validation run.

### Method

For each app, in order: **Audio capture → first partial → partial updates → FINAL → translation (if
enabled) → Stop/Exit → orphan processes.** No core changes during this phase; a failure is recorded
as a validation failure first and diagnosed separately. Worker command lines are verified to use the
installed bundle only (same assertions as the installer acceptance).

### Results

**VLC — PASS (2026-08-06).** Fresh run against the installed v0.5.26 bundle (`app_validation.ps1
-App vlc`, `english_sustained_90s.wav` looped 60 s): worker cmdlines installed-only (PASS), first
real caption ≈4.6 s, live partials grow in place (`Hello at tanggapin` → `Maligayang pagdating sa
unang pulong ng ating pag-uusap na Tagalog.` → re-decode to the full line), committed translated
Tagalog history with the `EN || TL` badge (`Ang pangalan ko ay Maria.`, `Ano ang pangalan mo?`,
`Magandang umaga lahat.`), loop repeats, POSTSTOP history retained, clean exit 0 orphans.
Corroborated by the installer acceptance (en→tl) and the final real-world acceptance (Tagalog +
en→tl legs). Evidence: `appval_vlc_*`, `installer_acceptance_*`.

**Chrome / YouTube — PASS (2026-08-06).** Two phases:
- **Chrome media playback** (local WAV via `file://`): first caption ≈2.5 s, live partials +
  committed translated Tagalog — proves Chrome's audio reaches loopback. (An earlier Chrome run
  passed `--mute-audio=0`, which Chrome parses as **muted** — a harness error, fixed; per-app
  Volume Mixer state can also mute Chrome, which is not an app issue.)
- **YouTube playback** (`youtube.com/watch?v=dQw4w9WgXcQ`, network allowed per policy): first real
  caption ≈14 s after Start (YouTube load/playback latency), live partials translate in place
  (`Ikaw` → `Bago kami` → full line), `EN || TL` badge, committed translated Tagalog history, clean
  exit 0 orphans. Worker cmdlines installed-only (PASS). Evidence: `appval_chrome_*`.

**Zoom — NOT VALIDATED (environment-limited, 2026-08-06).** No Zoom PASS/FAIL claim is made. Zoom
Workplace 7.0.6's client is Chromium-based and exposes **no UIAutomation surface** (no buttons,
edits, or web content in the accessibility tree), so the join flow cannot be automated:
`--url=https://zoom.us/test` opened only the embedded calendar webview; a `zoommtg://` join with the
documented test-meeting ID produced a `zJoinMeetingFailedDlgClass` (invalid/requires interaction);
no real meeting/account is available to emit speech. This is a **test-environment limitation**, not
an app defect — the WASAPI capture path under test is identical to the VLC/Chrome legs (both PASS).
**Optional follow-up:** a manual-assist run (a user joins a Zoom meeting while the caption app is
sampled and evidence recorded) or a machine with an automatable Zoom client.

**Teams — N/A** (desktop client not installed; recorded above).

| App | Status | Notes |
| --- | --- | --- |
| Chrome / YouTube | ✅ PASS | Real YouTube playback → live partials + en→tl committed lines, 0 orphans |
| VLC | ✅ PASS | Fresh + installer/final-acceptance evidence, live partials + en→tl, 0 orphans |
| Zoom | ⚠️ NOT VALIDATED | Environment-limited (Chromium UI, no automation surface, no meeting available) — see Results |
| Teams | **N/A** | Desktop Teams client unavailable |

## TD-005 — Settings persistence (2026-08-05)

### Automated tests — PASS

Full suite **335/335 passing, 0 failed, 0 skipped**, Release build 0 warnings / 0 errors.

```
Passed!  - Failed: 0, Passed: 77, Total: 77  - UniversalCaptions.Audio.Tests.dll
Passed!  - Failed: 0, Passed: 72, Total: 72  - UniversalCaptions.Captions.Tests.dll
Passed!  - Failed: 0, Passed: 77, Total: 77  - UniversalCaptions.Speech.Tests.dll
Passed!  - Failed: 0, Passed: 27, Total: 27  - UniversalCaptions.Translation.Tests.dll
Passed!  - Failed: 0, Passed: 82, Total: 82  - UniversalCaptions.App.Tests.dll
```

`SettingsStoreTests` (6), each run against a unique temp directory so the real `%LocalAppData%` file is
never touched:

1. **Save→load round-trip** — every persisted field (device id, language, translation on/off + target,
   opacity, font size, click-through, placement, expanded, version) equals across a save→load cycle.
2. **Missing file → defaults** — no file yields `new UserSettings()` with no throw.
3. **Malformed/wrong-type → safe defaults** — invalid JSON and wrong-typed values (`"Opacity": "not-a-number"`)
   both load as defaults without throwing.
4. **Unknown/new fields ignored** — a JSON file with `FutureField`/`NewNested` plus known fields loads
   the known fields (`DeviceId`, `Version`) and drops only the unknown ones (forward compatible).
5. **Atomic write + failed write preserves last good** — after a save there is no `.tmp` left; blocking
   the temp path with a directory makes the next save fail silently and the previously saved content
   survives.
6. **Concurrent/rapid saves settle** — 25 parallel `Save` calls produce a complete, parseable file whose
   content is one of the written values (never torn), and a final sequential save is last-write-wins.

### Manual/verification note

The persistence wiring (control-window categories + overlay placement/view state) is exercised through
normal real-App use — settings are written on change and on exit, and reapplied on the next launch via
the DI-registered store (`App.xaml.cs` loads before window construction). No device hotplug dependency.

## Entry 15 — Overlay live-line integration (2026-08-06)

### Purpose

ADR-0008 promoted `fasterwhisper-native` + live partials to the production default, but the WPF overlay
painted **committed FINALs only** — commit `7d1c057` ("temporary diagnostic tracer", 2026-08-03) had
replaced Slice 7's active-line painting and `_activeBlock` was never assigned (`CaptionRenderIdentityTests`
even asserted `ActiveBlock() == null`). Slice 12 proved the *engine* emits partials with Chrome-like timing;
it never proved the *overlay* displays them. This slice restores the live active line and feeds it the
native partial stream so the promoted default is actually visible as it happens.

### Acceptance criteria (user-approved 2026-08-06)

1. A partial becomes visible in the active line while the speaker is talking.
2. Later partials of the same segment replace it in place (same line).
3. The FINAL freezes that line into history (active line disappears).
4. Committed history never churns once a line is finalized.
5. Stop/Dispose leaves no stale partial on the overlay.
6. No partial ever enters committed history.
7. First-visible-partial latency and CPU impact are measured.
8. The `ggml-base` fallback stays functional (no regression).

### Implementation (code-behind only)

`CaptionOverlayWindow.UpdateCaptionItems` now creates **one** mutable `_activeBlock`, rewrites its text in
place on later partials, and removes it when `model.ActiveLine` is null (committed/stopped/hidden-while-
translating). `ReconcileHistory` reuse-by-sequence is unchanged; the `shouldUpdate` gate is unchanged
(holds during translation-pending so no source-language flash). Class + field + `ReconcileHistory` doc
comments updated; the XAML comment already described this restored design.

### Automated tests — PASS

Full suite **374/374 passing, 0 failed, 0 skipped** (77 Audio + 72 Captions + 109 Speech + 27 Translation +
89 App), Release build 0 warnings / 0 errors, `dotnet format --verify-no-changes` clean. Baseline was
372/372.

`CaptionRenderIdentityTests` rewritten 4→6, all deterministic (synthetic `CaptionState`, no overlay
paint/dispatcher dependency):

- partial rewrite keeps the same block identity (one `_activeBlock`, text replaced, no new history);
- growing partial stream paints a single active block and commits no history until the FINAL;
- no partial ever enters committed history;
- FINAL freezes the active line into history and removes the active block;
- cleared active line removes the block while history is preserved;
- finalized blocks keep their text instances and order.

### Real-App smoke verification — PASS (Entry 14 checklist + overlay AC-1..AC-8)

Driven by `smoke.ps1` (UIA) against the Release App with the promoted default. Per-sample rows carry
`app%|wkr%|n` (App Process CPU %, faster-whisper worker CPU %, overlay text-element count); POSTSTOP is
followed by `POSTSTOP_1..3` samples 1.2 s apart to prove STT actually stopped.

| Run | Verdict | Evidence |
|---|---|---|
| `promoted` (Tagalog, tl, translation off) | PASS | FW worker spawned; T3 first Partial 5.035 s / T4 first Final 7.573 s; 18 FINALs; `Salamat. Ikaw.` preserved PRESTOP→POSTSTOP; Tagalog materially better than ggml-base (17.5 s first caption, hallucination at 46.9 s); two `one`-for-`ako` quirks remain (`Ang pangalan ko ay one.`) |
| `liveoverlay` (promoted default, 250 ms sampling, 105 s) | PASS (AC-1, AC-2, AC-3, AC-4, AC-7) | Active line paints and evolves: `meeting sum` (9.8 s, n1) → `Meeting someone.` (12.2 s, same n1) → FINAL freeze at 15.1 s (n2); 27 lines POSTSTOP (vs 18 when FINAL-only); App CPU ~0–66% variable, worker CPU ~0%; first visible partial ≈5.6 s after capture start |
| `stopmid` (Stop mid-speech, 18 s) | PASS (AC-3, AC-4, AC-5, AC-6) | POSTSTOP committed 2 drained FINALs (`How are you?`, `Hello!`) then stable at 4 elements across 3 × 1.2 s within 4 s — no stale partial |
| `transen` (en→tl, translation on, Argos) | PASS (AC-1..AC-8; Entry 14 check 9) | English audio → native STT → Argos en→tl → **live-translated Tagalog active line** (`sa unang pulong ng aming pag - uusap.` at 11.5 s, rewritten in place to `...tungkol sa mga taglog.`, then `Ang pangalan ko ay Maria.`, `Ano ang pangalan mo?`, `Magandang umaga lahat.`); no raw-English flash; T5 first translation request 3.610 s, T6 first result 6.847 s, T4 first Final 9.455 s |
| `trans` (tl→en, Argos) | PASS (graceful degradation) | Argos tl→en is the **documented-unsupported direction** (stanza has no `tl` SBD processor → `ValueError: No processors to load for language tl`); App degraded correctly to source text (no crash, no fallback) |

Translation diagnostics (en→tl): first Partial 3.604 s, first translation request 3.610 s (6 ms after the
partial), first translation result 6.847 s (Argos cold-start), first Final 9.455 s, first translated caption
painted ≈11.5 s. Argos `ja→tl` pivot and `tl`-as-source remain governed by ADR-0006.

### Honesty note (closes the Entry 14 gap)

Entry 14 promoted the partials feature with **no real-App overlay smoke** — this report previously listed
"overlay live partials visible" as an unverified follow-up. The rows above are actual UIA-driven runs of
the Release App; no smoke claim here is made without execution evidence. `docs/CHANGE_IMPACT_ANALYSIS.md`
(Entry 15) records the root cause (7d1c057 tracer regression) and the pre/post implementation state.

### Close-out

Entry 15 complete (2026-08-06). No commit unless requested. Worker protocol, 8 s `MaxSegmentDuration`,
windowed engine, ADR-0007, TD-002, TD-005 untouched; `ggml-base` fallback preserved (Entry 14 check 8).

## Entry 16 — CPU/resource validation of the promoted production path (2026-08-06)

### Purpose

The promoted default (`fasterwhisper-native` + live partials) had an unresolved 80%+ CPU concern from
the earlier ggml-base era. This record measures the real CPU footprint of the promoted path across the
five requested scenarios to decide whether decode threading/scheduling needs optimization. Slice 10–15
work is **not reopened** — this is measurement-only plus an evidence-based optimization proposal.

### Method

`cpu_probe.ps1` (untracked harness) launches the Release App, clicks Start via UIA, plays the sustained
English corpus (`artifacts/samples/english_sustained_90s.wav`, ~88 s of SAPI speech) through WASAPI
loopback, and samples every ~200–500 ms. Per-process CPU% is computed from `TotalProcessorTime`/CIM
`Win32_Process` deltas (worker matched by command line, not path — the `fwv\Scripts\python.exe` is a venv
shim; the real inference runs in the sibling uv-python process). Warmup is excluded; single-core % is
shown alongside system % (÷ 12 logical cores).

### Results (12 logical cores; single-core %, warmup excluded)

| Scenario | App mean / p95 / max | STT worker mean / max (single-core) | STT worker (system) | Argos worker (system) |
|---|---|---|---|---|
| 1. App idle | 0.1 / 0 / 7.4 | 0 / 0 | 0% | 0% |
| 2. Captions, silence | 2.8 / 7.1 / 7.1 | 0 / 0 | 0% | 0% |
| 3. Speech + partials (interval 1 s) | 12.1 / 31.9 / 38.6 | **929.1 / 1059** | **77.4% mean / 88.2% max** | 0% |
| 4. Speech + translation (en→tl) | 12.0 / 28.4 / 46.3 | **895.3 / 1026.9** | **74.6% mean / 85.6% max** | 2.8% mean / 19.2% max |
| 5. Speech, partials disabled (`UC_NATIVE_PARTIAL_INTERVAL=0`) | 10.5 / 24.2 / 37.6 | 679.5 / 1043.3 | 56.6% mean / 86.9% max | 0% |

Series: `cpu_<scenario>.csv`, summary `cpu_summary.csv` (untracked).

### Findings

- **Idle is ~0%.** Silence is cheap (App ~3%, worker 0% — the worker burns CPU only during decodes).
- **The concern is confirmed and it lives in the STT worker, not the App.** With speech + partials the
  worker sustains ~9–10 cores = **77.4% of the whole machine** (≥80% single-core 100% of the time), the
  App itself only ~12% mean. Translation adds ~3% (Argos bursts ≤ ~2.3 cores).
- **Root cause: every decode uses all 12 cores.** `FasterWhisperEngineOptions.Threads` defaults to
  `Environment.ProcessorCount` and the App passes it straight to the worker (`--threads 12`). Both partial
  and FINAL decodes saturate the machine.
- **Partials cost ~20 points of machine CPU over FINAL-only (77% vs 57%).** Even FINAL-only stays heavy
  (57%) because 8 s segment FINALs decode back-to-back during continuous speech.
- **Decode wall is thread-count-invariant for real speech.** Worker round-trip sweep on real video audio
  (8 s slices, `small` int8): 12 threads 3.48 s (2.30×) vs 4 threads 3.21 s (2.50×) and 2 threads 3.97 s
  (2.01×) — short-input CTranslate2 decode is fixed-overhead-dominated. A pathological music/no-speech
  region (0 segments) is 0.42× realtime at both 12 and 4 threads (thread-invariant worst case).
- **CPU-seconds/audio-second scales ~linearly with threads** (12→4 threads: 5.22→1.60; 2 threads 0.99).

### Conclusion

The promoted path needs the threading optimization the checkpoint anticipated: cap the worker thread
count (e.g., to 4) so each decode uses ~4 cores instead of all 12, cutting the worker's machine share
~3× (≈77% → ≈26%) with **no decode-latency change** for real speech (realtime 2.3–2.8× either way).
Caption behavior (partial visibility, in-place rewrite, FINAL freeze, backlog) is unaffected by the thread
count; the worst-case music region is also thread-invariant.

### Optimization implemented and close-out (2026-08-06)

**Implementation (user-approved CPU-optimization slice; engine selection, worker wire protocol,
segmentation/8 s cap, partials, overlay, translation all untouched):** `UC_NATIVE_THREADS` env knob in
`SpeechEngineFactory.CreateNative` → `FasterWhisperEngineOptions.Threads` (default **4**, clamped to
[1, ProcessorCount] via `ResolveNativeThreads`); `--threads` remains the single worker decode-thread
control (`LineProtocolFasterWhisperProcess.BuildWorkerArguments`, extracted for tests); `sttnative`
benchmark gains `--threads`. `ggml-base`/windowed-engine paths unchanged (they use their own options).

**Tests (8 new → 382/382):** `SpeechEngineFactoryTests` — default `Threads == 4`, override 6 respected,
invalid/out-of-range (abc/0/-1/99) fall back to 4; `LineProtocolFasterWhisperProcessProtocolTests` —
`--threads <N>` appears in the worker argument list (4 and 12) with the rest of the contract unchanged.
Release 0 warnings/0 errors; `dotnet format` clean. Pre-existing flaky race hardened
(`CaptionPipelineTests` recovery test: `List<PipelineStatus>` → `ConcurrentQueue`).

**Formal `sttnative` gate (same Slice 12 config: real video audio 288.79 s, `small` int8, tl, hangover
0.7 s, max segment 8 s, partials interval 1 s / window 4 s, realtime feed):**

| Metric | threads=12 (baseline) | threads=4 (production default) |
|---|---|---|
| first FINAL | 17.98 s | 18.12 s |
| FINALs | 32 (32 feed / 0 flush) | 32 (31 feed / 1 flush) |
| WER (committed, vs `fil-orig`) | **33.2%** | **33.2%** |
| wall vs audio | 1.18× | 1.18× |
| first partial | 13.27 s | 13.30 s |
| emit-lag vs segStart (min/med/max) | 14.38 / 39.95 / 58.64 s | 14.51 / 41.27 / 59.67 s |
| mid-sentence splits | 10/32 | 10/32 (same split points) |
| short fragments | 0 | 0 |

**FINAL transcript text is 100% identical between 4t and 12t across all 32 FINALs (0 textual diffs).**
No caption regression; decode wall and realtime factor are unchanged — the thread-invariance the decode
sweep predicted, confirmed end-to-end through the real worker.

**Real-App CPU probe at the new default (speech + partials, translation OFF, `english_sustained_90s.wav`,
75 s, warmup 12 s excluded):**

| Metric | Pre-fix (Threads=12) | Post-fix (Threads=4) |
|---|---|---|
| STT worker single-core mean / max | 929.1 / 1059% | **379.6 / 451.7%** |
| STT worker (system) mean / max | 77.4 / 88.2% | **31.6 / 37.6%** |
| App (system) mean | 1.0% | 1.1% |
| first caption | 3.49 s | 3.72 s |
| overlay max lines | 15 | 16 |

A second post-fix run with translation ON (settings left by the prior probe) measured STT 26.6% + Argos
3.4% system mean ≈ 30% total vs 74.6% + 2.8% pre-fix. Captions still flow (first caption ~3.7 s, live
partials visible, overlay producing lines).

**Decision: PASS — production default `Threads` capped at 4.** Sustained STT CPU drops ~77% → ~32% of the
machine (2.4×; ~26% is the theoretical thread-proportional floor and is where the benchmark-predicted
~318% single-core would land under idle-machine conditions) with a text-identical caption stream, no
latency/backlog change, and the knob preserved for machines that want more cores. Series:
`cpu_speech.csv` (post-fix rows), `cpu_gate_t12/t4.log/.csv`, summary `cpu_summary.csv` (untracked).

## Final real-world acceptance — continuous media playback at production default (2026-08-06)

### Purpose

Per user direction ("stop optimizing CPU; run the final real-world acceptance session"), confirm the
production default (`fasterwhisper-native` + live partials, `UC_NATIVE_THREADS=4`) behaves acceptably in
**continuous normal daily use**: long-duration media playback through the default device, live partials
on the overlay, optional local translation, stable CPU across several minutes, clean Stop/Exit, no
orphaned workers.

### Method

`acceptance.ps1` (untracked harness): launches the Release App, clicks Start via UIA, plays media through
**VLC** (`C:\Program Files\VideoLAN\VLC\vlc.exe`, `--intf dummy --no-video --volume 256`, real WASAPI
loopback on the default device), samples per-poll CPU from CIM `Win32_Process` deltas (workers matched by
command line — the `fwv\Scripts\python.exe` is a venv shim; real inference is in the sibling uv-python
process), snapshots the overlay text via UIA, clicks Stop, then closes via `CloseMainWindow` and waits up
to 25 s for clean exit (post-final-flush), counting orphaned workers. Warmup 12 s excluded. Rows in
`acceptance_summary.csv` (untracked).

### Leg 1 — Tagalog YouTube-style content, translation OFF

Media: `uc_video_full.m4a` (real Tagalog video audio, 288.79 s, single pass), 300 s sampling, 374
samples.

| Metric | Result |
|---|---|
| STT worker (system) mean / max | **31.8% / 37.6%** |
| App (system) mean / max | 0.9% / 3.4% |
| Argos worker (system) | 0% (translation OFF) |
| first caption | 3.27 s |
| caption snapshots | 95 |
| overlay max lines | 33 |
| clean exit (within 25 s) | **True** |
| orphaned workers | 0 |

### Leg 2 — English content + en→tl translation ON

Media: `artifacts/samples/english_sustained_90s.wav` (SAPI English corpus, 90 s, **looped** for 300 s
continuous), 352 samples.

| Metric | Result |
|---|---|
| STT worker (system) mean / max | **33.5% / 37.1%** |
| Argos worker (system) mean / max | 4.2% / 21.6% (bursty — single in-flight slot) |
| App (system) mean / max | 1.3% / 3.2% |
| first caption | 3.23 s |
| caption snapshots | 129 |
| overlay max lines | 54 |
| clean exit (within 25 s) | **True** |
| orphaned workers | 0 |

### Findings

- **CPU stays flat across minutes of continuous captions.** Both legs sustained the Entry 16 numbers
  (STT ~32% of the machine, App ~1%, Argos bursts ≤ ~21%) with no drift, no growing backlog, no worker
  churn over 300 s of non-stop media.
- **Live partials + live translation verified end-to-end.** Overlay snapshots show a growing active
  partial that increments while the speaker talks (`Hello at malugod na tanggapin ang` → full line) and
  FINALs freeze into bounded history with the `EN || TL` badge; committed lines are real Tagalog
  (`Ano ang pangalan mo?`, `Magandang umaga lahat.`), and the looped corpus repeats correctly.
- **Clean Stop/Exit is real.** Both legs stopped on the Stop button (history retained, no stale partial)
  and the app exited on `WM_CLOSE` within the budget (0 orphans). The earlier 10 s-timeout flake was the
  harness close budget being too tight while the 289 s video's final flush was in flight — the app exits
  on its own in ~5 s (measured separately) and the harness budget was raised to 25 s.

### Decision — PASS

The production default behaves acceptably in continuous real-world use on this machine (Windows 10,
12 logical cores): stable ~32–33% STT + ~1% App CPU, first caption ~3.2 s, live partials on the overlay,
live en→tl translation, bounded history, clean exit, 0 orphaned workers. **The project is core-done per
the user's criterion** (no further CPU optimization; remaining product work is feature-level, not
core-architecture). Raw series: `acceptance_tl.csv`, `acceptance_en2tl.csv`, captions
`acceptance_tl_captions.txt`, `acceptance_en2tl_captions.txt` (untracked).

## Slice 10 — Faster-Whisper Native Streaming (deterministic phase, 2026-08-05)

### Purpose

Close the Slice 9 stale-caption gap (windowed faster-whisper committed only ~2 FINALs in 120 s, ~40 s
apart) by replacing the sliding-window re-decode with segment-based streaming: C# owns VAD speech-segment
detection, and each completed segment is decoded **once** through the existing faster-whisper worker wire
protocol, yielding one coherent FINAL per segment. Isolated experiment behind
`UC_STT_ENGINE=fasterwhisper-native`. This record covers only the **deterministic phase** (no Python,
no model); real-App/benchmark validation is the next, still-open gate.

### Automated tests — PASS (deterministic phase)

Full suite **357/357 passing, 0 failed, 0 skipped**, Release build 0 warnings / 0 errors.

New tests (21 total, all deterministic):

- `SpeechSegmentDetectorTests` (11) — the C# segment state machine driven directly by scripted
  voice-activity decisions on synthetic PCM: speech→silence emits one segment covering both; speech
  resumed within the hangover is one segment; two bursts produce two segments in order with correct
  `CapturedAtUtc`; continuous speech caps at `MaxSegmentDuration` then keeps buffering; a blip shorter
  than `MinSpeechDuration` is discarded; `Flush` emits the in-progress segment (and discards a too-short
  one / returns null when idle); `Reset` clears state; resumed speech counts toward `MinSpeechDuration`.
- `FasterWhisperNativeStreamingEngineTests` (11) — the engine against a scripted VAD + scripted
  `IFasterWhisperProcess`: one FINAL per completed segment with no live partials and the exact buffered
  PCM + language passed through; float→int16 clamping; Stop flushes the in-progress segment and drains
  already-queued segments (nothing dropped); decode failure raises `EngineFailed` and stops recognizing;
  start failure raises `ModelLoadFailed`; restart after Stop resets session state (two sessions, two
  FINALs, worker started once per session); invalid (non-mono) format raises `InvalidAudioFormat` once;
  empty decoded text emits no FINAL; Process before Start / after Stop is ignored; Start is idempotent;
  continuous speech emits FINALs at the max-segment cap (no stale monologue).

### Fresh-context review — PASS with fixes (2026-08-05)

Independent review of the new engine + detector found the concurrency core sound (no lost-segment or
post-Stop-write path; `_gate` serialization correct under Process-on-capture-thread vs Stop-on-teardown
thread). Fixes applied before this record: **M1** resumed speech now counts toward `_speechDuration`
(was undercounting `MaxSegmentDuration`/`MinSpeechDuration` across hangover resumes); **M2** per-session
epoch guard — a decode that outlives the Stop drain budget cannot raise a stale FINAL into a restarted
session (plus a Stop-timeout cancellation backstop); **M3** `Start` now maps any start exception to
`ModelLoadFailed` (was catching only `FasterWhisperProcessException`); **M5** detector options reject
negative/zero `MinSpeechDuration`/`SilenceHangover`/`MaxSegmentDuration`; **M4** removed the unreachable
multichannel downmix (the engine already gates mono). Accepted nits: `FloatToInt16` is duplicated from
`FasterWhisperDecoder` to keep the frozen windowed path byte-identical; `_cts` is not disposed (matches
baseline).

### Benchmark + real-App validation — PASS (2026-08-05)

Controlled benchmark + real-App run with `UC_STT_ENGINE=fasterwhisper-native` (model `small` int8,
language `tl`) on the actual video audio (`uc_video_full.m4a` / `uc_video_full_16k.wav`, 288.79 s) vs the
`fil-orig` reference. New additive benchmark mode `sttnative` in `UniversalCaptions.Benchmarks`
(`NativeStreamingBenchmark.cs`) drives the real engine exactly as the App composes it (same
`EnergyVad(0.008, 1, 2)`, same segment knobs 0.3 s / 0.7 s / 8 s). Raw logs: `artifacts/samples/realapp_native_streaming.log`,
`%TEMP%\opencode\sttnative_small_realtime.log` (+ `.csv`, `hyp_sttnative_small.txt`).

**Controlled benchmark (realtime feed, 10 ms chunks):** 0 partials (**FINAL-only**), 32 FINALs + 0 on
Stop flush (final sentence already committed via hangover; trailing audio is music → no in-progress
segment), commit cadence **13.3 FINALs/120 s** (vs windowed faster-whisper's 2 FINALs/120 s ~40 s
apart). **Committed WER 32.6%** vs `fil-orig` (same `stt_compare.py` normalization; ggml-base full-file
51.2%, faster-whisper full-file 31.1%) — the faster-whisper accuracy advantage is preserved on the
committed stream. Absolute latency numbers from the controlled run are **not** meaningful: the feed
loop's `Thread.Sleep(10)` ≈ 15.6 ms on Windows (timer granularity) paced audio at ~1.57× wall, so the
run's "backlog" was a feed artifact, not engine behavior. The App run (true realtime via WASAPI) is the
authoritative latency measurement.

**Real-App run (ffplay → WASAPI loopback → App, realtime):** first caption **15.2 s** after playback
start (vs ggml-base 14.8–21.0 s, windowed faster-whisper 27.1 s); **~30 committed captions / 289 s
(≈12.5 FINALs/120 s)**, one caption every ~8.2 s during continuous speech (segment-cap cadence); STT
latency rows **11.6–12.9 s** from segment start = **~4 s behind the segment's speech end** (decode-bound,
no growing backlog); 0 partial rows (FINAL-only, captions appear atomically). **Acceptance criteria
met:** accuracy ≫ 51.2% baseline (32.6%); responsive first caption, no 20–40 s stale backlog (fresh
commit every ~8.2 s, ~4 s behind segment end); one FINAL per segment, no duplicate/re-emitted segments;
no dropped final at Stop (last sentence "…maliit na unit ng tunog." committed at 285 s before the 289 s
audio end); no recurring `(Song)`/`(Subscribe)` hallucinations (music gaps produce no captions; the
"Paano kung…" repetition is in the source audio, not a hallucination).

**Known tradeoffs (documented, not blocking):** the 8 s `MaxSegmentDuration` cap can split a sentence
mid-word (e.g. "…instruction ng wea" / "Ang Filipino…") — inherent to segment-based streaming and
tunable via `UC_NATIVE_MAX_SEGMENT`; rare small fragments/hallucinations (`ba ba i?`, `Usa atin!`);
faster-whisper's ~12 s STT latency is governed by segment duration + decode (~4 s behind segment end),
on par with the windowed path's 13–16 s but with a ~6× better commit cadence. One first segment decoded
empty in the controlled run (dropped) but not in the App run.

**Decision (2026-08-05):** Slice 10 answered its research question — segment-based native streaming
**preserves faster-whisper small's accuracy advantage (32.6% vs 51.2%) while eliminating the stale
20–40 s commit backlog** (one fresh FINAL per ~8.2 s segment, ~4 s behind segment end, FINAL-only).
faster-whisper stays **opt-in** (`UC_STT_ENGINE=fasterwhisper-native`); the ggml-base production default
is unchanged (frozen). The Tagalog-accuracy gap on `ggml-base` now has a validated opt-in remedy with a
sentence-fragmentation tradeoff at the segment cap.

## Slice 11 — Native-Streaming Segment-Boundary Tuning (2026-08-05)

**Purpose:** tune the opt-in `fasterwhisper-native` segment boundaries (per user, after the Slice 10
PASS) — test `MaxSegmentDuration` 8/10/12 s, measure whether longer segments reduce mid-sentence
splits, confirm latency/backlog stays bounded, keep `SilenceHangover = 0.7 s` fixed, and change no
worker protocol / ggml-base / windowed-engine path. Goal: **accurate + natural sentence boundaries +
bounded live latency** as the basis for any future default-selection decision.

### Benchmark tooling improvements (additive, benchmark-only)

- **Timer-resolution fix:** `timeBeginPeriod(1)`/`timeEndPeriod(1)` around the `sttnative` realtime
  feed so `Thread.Sleep(10)` paces ~10 ms/chunk — controlled-run pacing is now valid (~1.1× realtime
  instead of the ~1.57× Slice 10 artifact).
- **Mid-sentence-split metric:** the gate table + CSV now count FINALs ending without terminal
  punctuation (`unterminated`, with split-point indices) and short fragments (≤2 words and
  unterminated).

### Controlled sweep — PASS

Hardware/real-path: Release `UniversalCaptions.Benchmarks.exe sttnative` driving the real
`FasterWhisperNativeStreamingEngine` (same composition as the App: `EnergyVad(0.008, 1, 2)`, min
speech 0.3 s, hangover 0.7 s, small int8, `tl`) on `uc_video_full_16k.wav` (288.79 s) vs the
`fil-orig` reference, realtime feed, three runs at max-segment 8/10/12 s. Logs:
`%TEMP%\opencode\sttnative_max{8,10,12}.log` + `hyp_sttnative_max{8,10,12}.txt` + `.csv`.

| MaxSegment | FINALs | Cadence | WER (norm) | Mid-sentence splits | Short fragments | Stop flush | End-of-audio cap behavior |
|---|---|---|---|---|---|---|---|
| 8 s | 32 | 13.3/120 s | 32.6% | 10/32 (31%) | 0 | none (last speech seg committed before music tail) | clean |
| 10 s | 26 | 10.8/120 s | 33.2% | 11/26 (42%) | 1 | 1 | capped segment spanning the music tail decoded as `Pag-pag-pag…` stutter |
| 12 s | 22 | 9.1/120 s | 30.0% | 10/22 (45%) | 1 | 1 | capped segment decoded as truncated `tunog` fragment |

- **FINAL-only in all three runs (0 partials).** 8 s reproduces the Slice 10 WER exactly (32.6%),
  confirming the timer fix did not alter accuracy.
- **Latency/backlog bounded at all three caps:** emit stays ~5 s behind each segment's speech end
  throughout (no growth); the worst decode was ~8 s for a capped 12 s segment — still < segment
  length, so the pipeline keeps up.

### Decision — keep 8 s (PASS, no change)

- **Longer segments do NOT reduce mid-sentence splits.** The split *fraction* worsens 31% → 42% →
  45%: the cap still force-closes mid-sentence; it just happens less often while each forced cut now
  discards more in-flight content (e.g. at 12 s: "…pagpapahapag-" mid-word, a sentence cut across
  FINAL 21/22 into a bare "tunog").
- **10 s/12 s add end-of-audio cap risk:** a segment force-closed at the cap that spans into the
  music tail produced a `Pag-pag-pag…` stutter (10 s) and a truncated `tunog` (12 s); at 8 s the last
  speech segment commits before the tail.
- **12 s costs ~46% responsiveness** (9.1 vs 13.3 FINALs/120 s — captions every ~13 s instead of
  ~9 s). Its small WER gain (30.0% vs 32.6%) is a boundary artifact (fewer force-close boundaries),
  not a decoding-quality gain.
- **Result: keep `MaxSegmentDuration = 8 s`** as the native engine's default — no production or
  knob-default change. The kept default's real-App latency/backlog evidence is the Slice 10 real-App
  run (`artifacts/samples/realapp_native_streaming.log`); a redundant re-run was not needed.
  Decision recorded in `docs/CHANGE_IMPACT_ANALYSIS.md` Entry 12, `BENCHMARK_REPORT.md` (Slice 11),
  CHANGELOG v0.5.20.

## Slice 12 — Faster-Whisper Native-Streaming Live Partials (Chrome-Live-Caption-style, 2026-08-05)

**Purpose:** deliver the Chrome-Live-Caption-style experience on the opt-in `fasterwhisper-native`
engine — incremental live partial text while the speaker is still talking, one FINAL per completed
segment (unchanged), no wire-protocol change, translation OFF, ggml-base untouched.

### Implementation (additive, opt-in)

- `SpeechSegmentDetector.TryGetPartial(maxSamples, out samples, out capturedAtUtc)` — bounded
  trailing-window snapshot of the in-progress segment (refused while idle / during hangover / after
  the segment completes).
- `FasterWhisperEngineOptions.PartialDecodeInterval` (default 0 = disabled → Slice 10/11 FINAL-only
  preserved) + `PartialDecodeWindow` (default 4 s, bounds each partial decode).
- `FasterWhisperNativeStreamingEngine` cadence dispatch with at most one partial decode in
  flight/queued (no backlog; ticks deferred, not queued), partials cleared on FINAL, shared session
  guard, `PartialTranscriptAvailable` event. Partials flow through the existing CaptionService
  (active-line replace) + overlay path (existing `CaptionDisplayPolicy` strips overlap).
- App knobs `UC_NATIVE_PARTIAL_INTERVAL` (default 1 s) / `UC_NATIVE_PARTIAL_WINDOW` (default 4 s);
  interval 0 restores FINAL-only. Benchmark `sttnative` gains `--partial-interval`/`--partial-window`
  + partial metrics.

### Automated tests — PASS

10 new deterministic tests (6 `SpeechSegmentDetector.TryGetPartial` + 4 engine partial tests incl. a
`BlockingFasterWhisperProcess` fake proving the single-in-flight bound). Full suite **367/367**
(109 Speech + 82 App + 72 Captions + 77 Audio + 27 Translation), Release build 0 warnings / 0 errors,
`dotnet format --verify-no-changes` clean.

### Controlled benchmark — PASS (2026-08-05)

One real-audio run, identical composition to Slice 10/11 (Release `sttnative`, real
`FasterWhisperNativeStreamingEngine`, `EnergyVad(0.008, 1, 2)`, min speech 0.3 s, hangover 0.7 s,
max segment 8 s, small int8, `tl`, realtime feed) on `uc_video_full_16k.wav` (288.79 s) vs the
`fil-orig` reference, with `--partial-interval 1 --partial-window 4`. Translation OFF.
Log: `%TEMP%\opencode\sttnative_partials_slice12.log` + `.csv`.

| Metric | Result |
|---|---|
| First visible partial (from audio feed start) | 9.19 s (speech onset 3.60 s) |
| **First caption lag T4 (onset → first partial)** | **5.59 s** (vs first FINAL 15.0 s) |
| Partial update cadence | 47 partials → 19.5/120 s (~3 s apart during speech) |
| Active-line changes while speaking | yes — text increments across partials ("Magandang" → "ang buhay, ako sigino…" → full sentence) |
| FINALs | 32 — **text-identical to Slice 11** (no accuracy regression) |
| WER (in-harness, vs fil-orig) | 33.2% (= Slice 11's 33.19%; the report's 32.6% uses `stt_compare.py` normalization) |
| FINAL latency after speech end | ~6 s after segment close (decode-bound, same as Slice 10/11) |
| Backlog | bounded: FINAL emit-lag plateau ~50 s (Slice 11 FINAL-only ~43 s); one 17.5 s decode spike (machine contention); flat, not growing |
| Realtime factor | 1.18× (Slice 11 1.13×; partial decodes add ~5 % wall) |
| Dropped/reordered captions | none (32/32 in order); stop flush none (clean end) |
| Hallucination/repetition | no new artifacts — identical FINAL stream (the "Paano kong?" repetition exists in the Slice 11 baseline; it is in the audio) |
| Process CPU | 1.2 s (worker Python does the heavy lifting) |

### Gate — PASS

speech → partial caption appears quickly (5.59 s) → incremental updates (19.5/120 s) → stable FINAL
at/near speech end, no accuracy regression (identical FINAL stream), no growing backlog (elevated
but bounded plateau ~50 s vs 43 s FINAL-only, realtime-safe 1.18×). Caveat recorded: partial decodes
add ~5 % wall and ~8 s to the tail emit-lag plateau; the 4 s window means the active line shows a
rolling 4 s of the segment and the FINAL reveals the sentence's earlier words not shown by the last
partial (expected Chrome-style rolling-window tradeoff). `ggml-base` remains the production default;
this benchmark does not constitute promotion. Recorded: Entry 13, CHANGELOG v0.5.21.

### Promotion (Entry 14, 2026-08-05 — ADR-0008)

The "production default" statement above was superseded by the user-approved promotion (Entry 14 /
ADR-0008): `fasterwhisper-native` + live partials (interval 1 s, window 4 s) is now the production
STT default via the new `SpeechEngineFactory`; `ggml-base` is the explicit fallback
(`UC_STT_ENGINE=ggml-base`). The deterministic factory-selection tests landed with it
(`SpeechEngineFactoryTests`, 5 new — App tests now 87, suite 372/372). The production path is exactly
the engine + knobs validated in this controlled benchmark and in the Slice 10 real-App run; no new
benchmark runs were required. Recorded: Entry 14, ADR-0008, CHANGELOG v0.5.22.

## ADR-0007 Option B — boundary-preserving fallback (2026-08-04)

### Automated tests — PASS

Full suite **284/284 passing, 0 failed, 0 skipped**, Release build 0 warnings / 0 errors.

```
Passed!  - Failed: 0, Passed: 66, Total: 66  - UniversalCaptions.Audio.Tests.dll
Passed!  - Failed: 0, Passed: 72, Total: 72  - UniversalCaptions.Captions.Tests.dll
Passed!  - Failed: 0, Passed: 59, Total: 59  - UniversalCaptions.Speech.Tests.dll
Passed!  - Failed: 0, Passed: 27, Total: 27  - UniversalCaptions.Translation.Tests.dll
Passed!  - Failed: 0, Passed: 60, Total: 60  - UniversalCaptions.App.Tests.dll
```

Changes under test: `StreamingTranscriptCommitter` Option B rules 1/3/4 (`LastCompletedBoundaryLength`, `PendingStable`, replacement-drop in `UpdatePendingStable`) — rewritten budget-fallback tests (rule 3 commits last completed boundary + keeps tail; rule 4 never manufactures a word-backed FINAL), `CommittedUntilUtc` snap-to-boundary tests, epoch-rollover timer survival, and `WhisperSpeechToTextEngine` multi-segment `ScriptedSegmentDecoder` migration.

### Live JFK verification — controlled English verification, PASS

**Hardware/real-path:** real App Release build, `ggml-base`, `StabilityWindow=2`, steady 8 s / 0.5 s config, real WASAPI loopback capture of the default render device, `artifacts/samples/jfk_long.wav` played through the loopback device, overlay committed-FINAL lines observed via UI Automation.

**Run A — single 22 s playback:** committed FINALs in order:
1. `Listening.` (pre-existing Whisper silence hallucination — also present pre-fix)
2. `Ask what you can do for your country.`
3. `And so my fellow Americans ask not what your country can do for you, ask what you can do for your country.`

**Run B — continuous ~2 min loop:** committed FINALs in order:
1. `Listening.`
2. `you ask what you can do for your country.`
3. `And so my fellow Americans ask not what your country can do for you, ask what you can do for your country.`
4. `And so my fellow Americans ask not what your country can do for you,`
5. `ask what you can do for your country. And so my fellow Americans ... ask what you can do for your country.` (cross-loop sliding-window re-emission — TD-006/007, pre-existing, isolated; not the Option B fallback defect)

**Pass criteria — all met:**
- ❌ Pre-fix interior-fragment FINAL `country can do for` → **ABSENT in both runs** (pre-fix run committed it with `boundary_found: false, fallback_used: true`; evidence `artifacts/samples/adv7_trace_evidence.log`, gitignored).
- ✅ Complete boundary-backed JFK sentences present.
- ✅ Stop drain preserves the final committed captions (POST-STOP == committed set).
- ✅ No app crash/hang.
- Notes: `Listening.` is a pre-existing Whisper artifact (present in the pre-fix baseline too); Run B line 5 is the known TD-006/007 overlap re-emission, out of scope for this step (duplicate handling is a separate follow-up per ADR-0007).

**Evidence:** `artifacts/samples/adv7_optionB_jfk.log`; driver script (UI Automation) preserved at the temp harness used for both runs.

**Acceptance gate — PENDING:** the original Tagalog recording scenario (`"At gusto ko"` / `"Kaya"` / `"artipisyal na katalinuhan"`) is the remaining acceptance evidence. The original operator recording is **not available** in the workspace; per user, no substitute Tagalog sample may be used to claim acceptance. ADR-0007 therefore remains **Proposed** until that live evidence exists (fragmentation, duplicates, missing words, Stop drain judged end-to-end through the real App).

## Slice 8 — Tagalog STT-vs-Committer Isolation & Model-Selection (2026-08-04)

**Purpose:** classify the reported Tagalog live-caption defects as STT (Whisper) vs committer, and
measure the accuracy-vs-latency tradeoff across the three locally available models — **without
changing the frozen production default or the boundary algorithm.** This addresses the earlier
open question independently of ADR-0007.

**Diagnostic evidence (`artifacts/samples/raw_vs_committed_tagalog.log`):**
- RAW Whisper full-file segments already contain the reported symptom classes verbatim:
  recognition errors (`Kung usta?`, `Ikao.`, `Salaman.`, `Syangapala.`, `Tagaman nila ako.`),
  hallucinated `1.` segments, and short 0.5–1.6 s fragment boundaries.
- The committer **aggregates** these RAW segments into larger FINALs (90 s streamed-harness run:
  10 RAW groups → 3–4 cleaner FINALs); it does not manufacture the cuts.
- **Conclusion: the reported issues are STT model quality on Tagalog, not committer logic.
  ADR-0007 is not implicated** and stays Proposed/frozen.

**Real-App model comparison** — UIA-driven Release App, same 90 s Tagalog slice (STT `tl`), frozen
config (st2 / window 8 s / interval 0.5 s / min-audio 0.5 s), full `ProcessorCount` threads:

| Model | STT latency | First final | Finals | Tagalog accuracy | Halluc. `1.` | Stop drain |
|---|---|---|---|---|---|---|
| tiny | ~1.75 s | ~2.7 s | ~23 | ❌ `Komosita!`, `guan`, `Salaman` | ❌ `My name is One` | ✅ |
| base | ~3.1 s | ~17.5 s | 10 | ❌ `Kung usta`, `Ikaw`, `Mabutirin` | ❌ `ay 1.` | ✅ |
| small | 16.9–21.9 s | ~35.3 s | 4 | ✅ all target words correct | ✅ none | ✅ |

Evidence: `artifacts/samples/realapp_{tiny,base,small}_tagalog.log`. Detailed findings in
[`BENCHMARK_REPORT.md`](BENCHMARK_REPORT.md) (Slice 8).

**Decision (recorded per user):** no production change. **`ggml-base` stays the frozen default**
(ADR-0003) for acceptable responsiveness. `small` gives better Tagalog accuracy but unacceptable
real-time latency; `tiny` is fastest but does not solve recognition. Model exploration deferred;
testing done — no automated test count change (284/284 still the current suite). ADR-0007 remains
**Proposed** (acceptance gate unchanged).

## Faster-whisper selectable STT engine — Slice 9 (2026-08-04)

**Purpose/Hardware path:** the Slice 8 T3 gap (no whisper.cpp model gives both Tagalog quality and
responsiveness) was addressed with a **parallel faster-whisper `ISpeechToTextEngine`** selected via
`UC_STT_ENGINE=fasterwhisper`, without touching the frozen `ggml-base` default. Hardware/real-path:
real App Release build, faster-whisper `small` int8 in the `%TEMP%\fwv` venv (faster-whisper 1.2.1,
CTranslate2 4.8.1), real WASAPI loopback, `artifacts/samples/first_meeting_tagalog_90s.wav` played
through the loopback device, overlay committed-FINAL lines polled via UI Automation.

**Automated tests — PASS.** Full suite **293/293 passing, 0 failed, 0 skipped**, Release build 0
warnings / 0 errors.

```
Passed!  - Failed: 0, Passed: 66, Total: 66  - UniversalCaptions.Audio.Tests.dll
Passed!  - Failed: 0, Passed: 72, Total: 72  - UniversalCaptions.Captions.Tests.dll
Passed!  - Failed: 0, Passed: 68, Total: 68  - UniversalCaptions.Speech.Tests.dll
Passed!  - Failed: 0, Passed: 27, Total: 27  - UniversalCaptions.Translation.Tests.dll
Passed!  - Failed: 0, Passed: 60, Total: 60  - UniversalCaptions.App.Tests.dll
```

Changes under test: `ISTTDecoder`/`WhisperCppDecoder` extraction (no behavior change to the
whisper.cpp path), `FasterWhisperDecoder`/`LineProtocolFasterWhisperProcess`/
`FasterWhisperProcessException`/`IFasterWhisperProcess`/`FasterWhisperEngineOptions`,
`FasterWhisperSpeechToTextEngine`, and the `App.xaml.cs` `UC_STT_ENGINE` selection +
`ResolveFasterWhisperPython()`. New tests: `FasterWhisperSpeechToTextEngineTests` (5 — finals
pipeline, changing partials no premature commit, Stop idempotent, decode-failure → EngineFailed,
restart reset) + `FasterWhisperDecoderTests` (4 — startup failure → FileNotFoundException,
float→int16 + language passthrough, clamp, worker failure → InvalidOperationException).

**Real-App validation — PASS.** Same 90 s Tagalog slice, STT `tl`, frozen config
(st2 / window 8 s / interval 0.5 s / min-audio 0.5 s). faster-whisper `small` int8 committed clean
**bilingual finals with no `1.`/`one` hallucination**, STT latency **10.7–11.7 s** (vs whisper.cpp
small 16.9–21.9 s), first final 16.5–29.9 s, 3–4 finals. A 1.5 s-interval variant gave the cleanest
complete sentences (first final 16.5 s ≈ base 17.5 s). Metric-by-metric comparison confirming the
Slice 8 target is met:
`artifacts/samples/realapp_fasterwhisper_small_tagalog.log` (+ `_int1_5_` variant). Full findings in
[`BENCHMARK_REPORT.md`](BENCHMARK_REPORT.md) (Slice 9).

**Protocol fixes surfaced by the real App (not covered by the fake seam):** the live run exposed two
wire-format bugs in `LineProtocolFasterWhisperProcess` — (1) magic constant byte order
(`0x55435746` → `0x46574355` "UCWF") and (2) a 16-byte segment-header read that should be 20 bytes.
Both fixed; the pre-fix build committed only `Listening.`, the post-fix build produced transcripts.
Adds a lasting argument for a direct protocol round-trip test (TD-013-style).

**Decision (recorded per user):** faster-whisper is **opt-in** (`UC_STT_ENGINE=fasterwhisper`);
**`ggml-base` stays the frozen default**; no default promotion happens without explicit user
approval.

**Decision-gate (startup + responsiveness) — PASS, NOT promoted (2026-08-04).** The promotion
candidate (faster-whisper `small` int8) was measured on startup and steady-state latency against the
frozen default. Worker cold-start decomposition (direct probe): spawn 0.006 s + Python import/model
load **2.6 s** + first 8 s-window decode 2.5 s = ~5.2 s. Real-App first-caption harness (UIA-driven
Release App, same 90 s Tagalog slice, STT `tl`): faster-whisper `small` first caption **16.5–17.4 s**
(vs ggml-base **25.0 s**) but steady-state STT latency **13.7–15.8 s** (vs ggml-base **2.4–3.7 s**).
faster-whisper finals were composed sentences (7) with no hallucination; ggml-base finals were
fragmented (10) with `1.`/`One.`/`May name is`/`Mabuterin` hallucinations. Window/interval tuning:
1.0 s ≈ no change (13.7 s), 1.5 s worse (24.2 s), 4 s window produced **no captions** — the frozen
8 s/0.5 s config is already near-optimal for the faster-whisper path. Pre-warm would save only ~2.6 s.
**Result: no promotion. `ggml-base` remains the production default; faster-whisper `small` stays
opt-in until steady-state latency can be materially reduced.** Evidence:
`artifacts/samples/firstcaption_{fw_small,i1_fw_small,base,w4_fw_small}.log`; findings in
[`BENCHMARK_REPORT.md`](BENCHMARK_REPORT.md) (Slice 9 decision-gate).
## TD-016 - `LineProtocolFasterWhisperProcess` protocol-contract suite (2026-08-04)

**Purpose:** close the Slice 9 finding that the two wire bugs (magic byte order `0x46574355`; the
16 to 20-byte segment-header read) were surfaced only by the real-App run. The existing unit-test
fake seam (`IFasterWhisperProcess`) never exercised the wire format. This suite drives the real
production reader against a deterministic fake-worker byte stream - no Python/venv/model.

**Automated tests - PASS.** `LineProtocolFasterWhisperProcessProtocolTests` (9/9). Full suite now
**302/302 passing, 0 failed, 0 skipped**, Release build 0 warnings / 0 errors (66 Audio + 72
Captions + 77 Speech + 27 Translation + 60 App).

```

Passed!  - Failed: 0, Passed: 77, Total: 77  - UniversalCaptions.Speech.Tests.dll
```

**Seam:** a new internal `LineProtocolFasterWhisperProcess(FasterWhisperEngineOptions, Stream stdin,
Stream stdout)` constructor injects the worker streams directly. `StartAsync` skips the real
`Process.Start` when streams are injected; `WriteRequestAsync` no longer requires a live `_process`
handle (it checks the stdin stream instead). Production behavior of the real worker path is
unchanged; only the opt-in faster-whisper path is touched - the `ggml-base` default is untouched.

**Cases (deterministic protocol contract):**

1. `StartAsync_And_TranscribeAsync_ValidFrame_20ByteHeader_ParsesExactly` - golden frame parses to
   exact text/timestamps; the reader consumes exactly 16 + 20 + 7 bytes.
2. `RequestHeader_WritesCorrectMagic_AndLayout` - request header magic/version/sample-rate/sample-
   count/language length + int16 PCM layout verified byte-for-byte.
3. `WrongMagic_IsRejected` - magic `0x55435746` (byte-swapped) is rejected as `Protocol`, not parsed.
4. `TwentyByteHeader_DoesNotConsumePayload` - a 16-byte reader would read "Kums" as the text length
   (huge length -> EOF -> failure); the 20-byte reader consumes exactly 20 and the frame parses.
5. `TwoSegments_ParseInOrder_WithDistinctTimestamps` - two segments parse in order with distinct
   timestamps (catches cursor-offset bugs a one-segment test hides).
6. `FragmentedPipeReads_ReconstructFrame` - frame served in 3/7/1/9-byte chunks; reader reconstructs
   (pipe reads are not message boundaries).
7. `TruncatedSegmentHeader_IsDeterministicProtocolError` - 19 of the 20-byte segment header then EOF;
   deterministic `EngineUnavailable` "closed the protocol stream", never a partial segment.
8. `TruncatedResponseHeader_IsDeterministicProtocolError` - 15 of the 16-byte response header;
   deterministic error, no hang.
9. `PayloadBoundary_ConsumesExactlyDeclaredBytes` - multi-byte UTF-8 ("Kumustañ") consumed exactly the
   declared byte length.

**Evidence:** `tests/UniversalCaptions.Speech.Tests/LineProtocolFasterWhisperProcessProtocolTests.cs`;
findings in `BENCHMARK_REPORT.md` (Slice 9) and CHANGELOG v0.5.14.


## Environment

| Item | Value |
|---|---|
| OS | Windows 10 Pro (build 19045, NT 10.0.19045) |
| Runtime | .NET 8.0.29 |
| Solution | `UniversalCaptions.slnx` |
| Test framework | xUnit (net8.0) |
| Capture dependency | NAudio 2.2.1 (WASAPI loopback, no VB-CABLE) |
| Translation engine | Argos Translate 1.11.0 (Python 3.11 venv under `artifacts/argos/`, git-ignored) |

## Build Verification

Command: `dotnet build UniversalCaptions.slnx`

```
    0 Warning(s)
    0 Error(s)
```

## Automated Test Results

Command: `dotnet test UniversalCaptions.slnx`

```
Passed!  - Failed:     0, Passed:    66, Skipped:     0, Total:    66, Duration: 371 ms - UniversalCaptions.Audio.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    58, Skipped:     0, Total:    58, Duration: 215 ms - UniversalCaptions.Captions.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    41, Skipped:     0, Total:    41, Duration: 1 s - UniversalCaptions.Speech.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    21, Skipped:     0, Total:    21, Duration: 89 ms - UniversalCaptions.Translation.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 630 ms - UniversalCaptions.App.Tests.dll (net8.0)
```

### Coverage by Area

| Area | Tests | Result |
|---|---|---|
| `ByteToFloatConverter` — 8/16/24/32-bit int and 32-bit float PCM, extensible sub-formats, stereo interleave, unsupported encodings, range validation | 10 | Passed |
| `SampleRateConverter` — frequency preservation (16k→48k upsample, 48k→16k downsample, 44.1k→48k), channel count, empty/short input, identity 48k→48k | 12 | Passed |
| `EnergyVad` — silence vs speech detection, thresholds, boundary behavior | 6 | Passed |
| `PcmRingBuffer` — ordering, wrap-around, overflow, read/write boundaries | 10 | Passed |
| `AudioProcessor` — pipeline chaining, resample + convert + VAD integration | 8 | Passed |
| `AudioLevelMeter` — RMS/peak per chunk, window aggregation | 8 | Passed |
| `WasapiLoopbackCaptureSource` — fake `IWaveIn` boundary: start/stop, chunk delivery, device-invalidated (0x88890004 → Disconnected), 0x8889000A → Unavailable, unknown → Unknown | 12 | Passed |
| `ISpeechToTextEngine` + `FakeSpeechToTextEngine` — contract: process-before-start ignored, start/stop, idempotent stop, partials by duration, partial→final ordering, monotonic sequence, stop-cancels, runtime error, start-throw, start-model-error, continuous chunks, timestamp/latency, dispose | 14 | Passed |
| `FakeSpeechToTextEngine` — scripted direct emit, offline trigger, partial-then-error | 4 | Passed |
| `StreamingTranscriptCommitter` — stability-based commit: partial vs stable vs final, word-boundary common-prefix back-off, epoch reset, committed boundary, no premature commit, reset | 10 | Passed |
| `WhisperSpeechToTextEngine` (injected deterministic decoder) — partial→final as stable text is confirmed, changing partials don't commit early, final not emitted twice, stop doesn't commit incomplete audio, restart resets state, decoder failure doesn't leave stale text, decode→Stop→DisposeAsync regression, process-before-start ignored, stop cancels decode, invalid-format → `InvalidAudioFormat`, decode failure → `EngineFailed`, missing model → `ModelNotFound`, `StabilityWindow` < 2 rejected | 13 | Passed |
| `ITranslationEngine` + `FakeTranslationEngine` — contract: mapped text + languages, auto-detect source, pivot metadata, monotonic sequence + call ordering, cancellation, configured failure → `TranslationException`, empty input → `EmptyInput`, source-equals-target → `SourceEqualsTarget` | 8 | Passed |
| `ArgosTranslationEngine` (injected `FakeArgosProcess`) — request mapping (text/source/target), detected-source + pivot metadata, process failure → mapped `TranslationException`, null text → `ArgumentNullException`, empty text → `EmptyInput`, missing target → `UnsupportedLanguage`, source-equals-target → `SourceEqualsTarget` (process not started), start failure → mapped `TranslationException`, cancellation, monotonic sequence, concurrent calls serialized, restart-after-fatal-error (process relaunches on the next call), dispose disposes process | 13 | Passed |
| `CaptionState` — sequence-ordered history, duplicate-sequence replace, bounded history (drop oldest, capacity 0), active-line replace/clear + state validation, translation update by exact line identity (stale instance rejected), missing sequence no-op, active-line translation replace by exact line identity (apply, stale instance rejected, after-clear no-op, state validation), translation on/off + normalization, session begin/end, reset, negative-capacity rejected | 20 | Passed |
| `CaptionSnapshot` — immutable snapshot of active line + history (detached from later commits, thread-safe against concurrent mutations), `GetSnapshot` matches current state | 5 | Passed |
| `CaptionService` (deterministic `StubTranslationEngine`/`GatedTranslationEngine`) — partial updates active line + events, partial/final before-start ignored, final commits history + clears active, committed event, after-stop ignored, idempotent start, translation on → background request + completed line, explicit target override, translation off → no request, enabled without engine → no request, translation failure preserves source text, unexpected engine exception doesn't break the pipeline, gated completion applies when released, updated event, stale translation result doesn't overwrite a re-delivered line, stop/reset cancels in-flight (line stays pending), bounded history, dispose stops, options validation, missing-target exception, target normalization, **live active-line translation** (partial translates in the target language, off makes no active-line request, failure preserves source, single-slot serialization + self-replenish to a newer partial, stale partial result discarded and never surfaced, result discarded when the line was committed, result discarded when translation disabled mid-flight, updated event, enabling translation mid-session translates the current partial) | 33 | Passed |
| `CaptionDisplayPolicy` (Q1 display-policy resolution) — null/empty state, active line rendered verbatim from the latest partial, committed finals newest-first in bounded history, translated text replaces source only when `Completed`, source preserved when translation not-requested/pending/failed, target-language badge exposed when translation enabled / absent when disabled | 10 | Passed |
| `CaptionPipeline` (fakes at the capture/STT boundaries: `FakeAudioCapture`/`FakeSpeechToTextEngine`/passthrough processor) — wiring capture→processor→STT→caption service, format conversion, partial/final flow, latency, capture error, recognition error, capture-factory error, audio-processing exception, stop/dispose, teardown ordering (`Stop` returns before teardown completes; `Dispose` waits), fail-on-start teardown paths, chunks-after-stop ignored | 20 | Passed |
| `AudioSourceLoader` — enumerates devices with preferred default selected, empty list has no preferred, enumeration failure surfaces without throwing, blank device normalized | 4 | Passed |
| `TranslationGuard` — source-equals-target rejected (case-insensitive), null/blank target rejected, different languages allowed | 4 | Passed |

### Notable Fixes Found by Testing

- **32-bit float conversion test** used `new WaveFormat(48000, 32, 1)`, which is 32-bit *integer* PCM; corrected to `WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)` to exercise the float path.
- **Extensible sub-format parsing**: NAudio 2.2.1 `WaveFormat.FromFormatChunk` returns `WaveFormatExtraData` for `WAVE_FORMAT_EXTENSIBLE`; the test helper now builds the native `WAVEFORMATEXTENSIBLE` struct in unmanaged memory and uses `WaveFormat.MarshalFromPtr`, which dispatches to `WaveFormatExtensible` (verified `SubFormat == MFAudioFormat_Float/PCM`).
- **`SampleRateConverter` rewrite**: the original kernel loop and ring buffer produced phase-skewed output (~889 Hz for a 1000 Hz sine). Rewritten as a sliding-window resampler with explicit dropped-frame eviction and pre-stream zero padding; output measures 1000.32 Hz for a 1000 Hz input.
- **`LoopbackDeviceEnumerator`**: removed invalid `using` on `MMDeviceCollection` (not `IDisposable` in NAudio 2.2.1); disposes each `MMDevice`.

## Manual Device Verification

Command: `dotnet run --project src/UniversalCaptions.Diagnostics -- --seconds 5`

```
Universal Live Captions - Audio Diagnostics
Runtime: 8.0.29 on Microsoft Windows NT 10.0.19045.0
Privacy: audio is processed in memory only; nothing is recorded or transmitted.

Output devices found: 1
  [0] Speaker/HP (Realtek(R) Audio)
Capturing system audio via WASAPI loopback.
Format: 48000 Hz, 2 ch, 32-bit. Press Ctrl+C to stop.

[================================] peak  0.985  rms  0.235    2 chunks /   140 ms  seq #4        elapsed 00:00:00
[================================] peak  0.985  rms  0.197    2 chunks /   130 ms  seq #6        elapsed 00:00:00
[========================        ] peak  0.736  rms  0.166    2 chunks /   120 ms  seq #8        elapsed 00:00:00
... (live meter tracks system playback levels)
```

Second run (timed stop): `--seconds 3` → `Capture stopped after 3.0s. Last chunk sequence: 47.` — capture starts, streams chunks continuously (~120–140 ms per 2-chunk window), and stops cleanly.

**Result: Passed.** Device enumeration works, loopback capture works, meter reacts to real audio, clean shutdown on timeout/Ctrl+C. Privacy respected: in-memory only, no persistence.

## Real-Model Verification (Slice 2)

Harness: `src/UniversalCaptions.Benchmarks` (Release) feeding `jfk.wav`, `jfk_noisy.wav`, `jfk_long.wav`, and `OSR_us_000_0010_8k.wav` through `WhisperSpeechToTextEngine` with the real ggml-tiny and ggml-base models (Whisper.net 1.9.1, CPU, 4 threads); streaming chunks fed at realtime pacing (0.5 s chunks / 0.5 s sleep). Full results in [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md).

```
=== Sample: jfk.wav (11.00s) ===
  === ggml-tiny.bin ===
    stream:   12.25s wall (1.11x realtime, 40.95s cpu); 6 partials, 2 finals
    first partial:  3.614s  avg lat 1404ms
    first final:    6.612s  avg lat 4002ms
  === ggml-base.bin ===
    stream:   12.47s wall (1.13x realtime, 42.44s cpu); 3 partials, 1 finals
    first partial:  4.509s  avg lat 2303ms
    first final:    9.876s  avg lat 6974ms

=== Sample: jfk_long.wav (22.50s) ===
  === ggml-tiny.bin ===
    stream:   23.01s wall (1.02x realtime, 85.42s cpu); 16 partials, 5 finals
    first partial:  2.886s  avg lat 1127ms
    first final:    5.267s  avg lat 794ms

=== Sample: OSR_us_000_0010_8k.wav (33.62s) ===
  === ggml-tiny.bin ===
    stream:   35.32s wall (1.05x realtime, 134.11s cpu); 22 partials, 11 finals
    first partial:  3.273s  avg lat 1450ms
    first final:    5.752s  avg lat 6184ms
  === ggml-base.bin ===
    stream:   35.66s wall (1.06x realtime, 134.31s cpu); 11 partials, 4 finals
    first partial:  4.590s  avg lat 2798ms
    first final:   10.045s  avg lat 9037ms
```

**Result: Passed.** Real models load (~0.3–0.8 s), **partial and final transcripts stream at realtime pacing** (≤1.16× wall), finals are committed progressively across windows on every sample, and Stop + DisposeAsync unwind cleanly with no exception.

### Notable Fix Found by Real-Model Testing

- **`WhisperSpeechToTextEngine.Dispose`**: Whisper.net 1.9.1 `WhisperProcessor.Dispose()` throws `"Cannot dispose while processing, please use DisposeAsync instead"` when a native decode is in flight (a stop can race an in-progress decode). Fixed by implementing `IAsyncDisposable` on the engine and disposing the processor via its `DisposeAsync()` (which waits for the decode to unwind), with sync `Dispose()` blocking on it. Locked by the `Stop_AndDisposeAsync_WhileDecodeInProgress_IsClean` regression test.

## Real-Argos Verification (Slice 3)

Ran end-to-end through `ArgosTranslationEngine` (real Python 3.11 + Argos 1.11.0 child process, `argos_translate_server.py` line protocol). Verified pairs and behavior:

| Request | Result |
|---|---|
| `en→tl` "Hello world, this is a live caption test." | **Passed** — "Hello world, ito ay isang live kapsiyon test." (first call includes ~12–14 s process+model load) |
| `ja→en` "こんにちは世界" | **Passed** — "Hello world" |
| `en→ja` "Good morning everyone" | **Passed** — "おはようございます" |
| `ja→tl` (no direct model; pivots via `en`) | **Passed** — `usedPivot=true`, `pivotLanguage=en` |
| unknown language code `zz` | **Passed** — `UnsupportedLanguage` |
| empty text | **Passed** — `EmptyInput` |
| source == target | **Passed** — `SourceEqualsTarget` |
| `tl` as **source** | **Known limitation** — Argos sentence-boundary detection does not support `tl` as a source; MVP pairs use `tl` only as a target (see ADR-0006) |

Offline check: translations ran with no network traffic during requests (packages installed during setup only; runtime is local). **Result: Passed** — direct pairs, pivoting, and error mapping all verified through the real engine. Benchmark details in [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md) (Slice 3 section).

## Acceptance Criteria Status (Slice 1)

| Criteria | Status | Evidence |
|---|---|---|
| Capture source starts, receives PCM, stops cleanly | **Passed** | `WasapiLoopbackCaptureSourceTests` (12) + manual run |
| Conversion handles 8/16/24/32-bit int and 32/64-bit float PCM | **Passed** | `ByteToFloatConverterTests` (10) |
| Ring buffer preserves ordering and handles wrap-around/overflow | **Passed** | `PcmRingBufferTests` (10) |
| Resampler preserves frequency content within tolerance | **Passed** | `SampleRateConverterTests` (12) |
| VAD distinguishes silence from speech deterministically | **Passed** | `EnergyVadTests` (6) |
| Level meter reports RMS/peak per chunk | **Passed** | `AudioLevelMeterTests` (8) |
| Device-disconnect/init failure maps to a user-readable error | **Passed** | `WasapiLoopbackCaptureSourceTests` failure mapping |
| Manual run shows live meter from system audio | **Passed** | Manual verification above |

### Acceptance Criteria Status (Slice 2)

| Criteria | Status | Evidence |
|---|---|---|
| `ISpeechToTextEngine` streaming contract verified with a fake engine | **Passed** | `ISpeechToTextEngineTests` (14) + `FakeSpeechToTextEngineTests` (4) |
| Local Whisper produces partial transcripts from captured audio | **Passed** | Real-model runs above (partials streamed at realtime pacing) |
| Streaming finals emitted (commit tuning) before Slice 4 | **Passed** | Stability-based committer (10 tests) + engine (13 tests); real-model runs commit finals on all four samples |
| Model selection benchmark recorded | **Passed** | [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md) — quality now discriminated on OSR sample (tiny 16.0% vs base 4.9% WER) |

### Acceptance Criteria Status (Slice 3)

| Criteria | Status | Evidence |
|---|---|---|
| `ITranslationEngine` contract verified with a fake engine | **Passed** | `FakeTranslationEngineTests` (8) |
| Argos translates source transcripts to the target language offline/local | **Passed** | Real-Argos verification above (direct + pivot pairs, offline) |
| Translation benchmark recorded (latency + quality per pair) | **Passed** | [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md) — Slice 3 section |
| Translation consumes Whisper FINAL segments (partials ignored) | **Passed (design)** | Finals feed is implemented in Slice 4 caption service; the engine contract is one-shot (text in/out) and the benchmark finals-stream path feeds discrete final segments |

## Slice 5 — Overlay + Control Window Verification (App)

Automated (done, 2026-08-01):

- `CaptionDisplayPolicyTests` (8) — resolved Q1 policy: active line rendered verbatim from the latest partial (`CaptionState.ActiveLine`); committed finals rendered newest-first as bounded history; translated text replaces the source on a committed line only when `CaptionTranslationStatus.Completed`; source preserved when translation is off/pending/failed.
- `CaptionPipelineTests` (20) — wiring verified against fakes at the capture/STT boundaries (`FakeAudioCapture`, `FakeSpeechToTextEngine`, passthrough processor): audio → processor → STT → caption-service flow, format conversion, partial/final flow, latency propagation, capture-error/recognition-error/capture-factory-error/audio-processing-exception surfacing, teardown ordering (`Stop` returns before component teardown completes; `Dispose` waits for teardown), fail-on-start teardown paths, idempotent stop/dispose, and chunks-after-stop ignored.
- `AudioSourceLoaderTests` (4) — device enumeration (preferred default), empty list, enumeration failure surfacing, blank-device normalization.
- `TranslationGuardTests` (4) — source-equals-target rejection (case-insensitive), null/blank target rejection, different-language allowance.

Manual (completed 2026-08-01, this Windows 10 machine — build 19045):

- [x] Launched `dotnet run --project src/UniversalCaptions.App` (built exe `src/UniversalCaptions.App/bin/Debug/net8.0-windows/UniversalCaptions.App.exe`). Both windows created: control window "Universal Live Captions" (400×448) and overlay "Captions" (720×180). Overlay verified via UIA: `Topmost=True`, `Layered=True` (transparent). No startup errors on stdout/stderr.
- [x] Device enumeration: audio source combo auto-selected the default render device `Speaker/HP (Realtek(R) Audio)` (WASAPI loopback endpoint id `{0.0.0.00000000}.{d16c2292-...}`).
- [x] **Real end-to-end capture + STT:** Started captions (status "Capturing system audio."). Played speech through SAPI (`System.Speech`) on the machine; real WASAPI loopback audio was captured, processed, fed through the real Whisper `ggml-base` model, and **live captions appeared in the overlay** — partials updated the active line and finals committed to the bounded history (hint text replaced by real transcripts). Ambient machine audio was also transcribed (expected — loopback captures all system audio).
- [x] Overlay interaction: drag-move (overlay follows the mouse), resize via grip (720×180 → 722×225), and click-through toggle (sets/clears `WS_EX_TRANSPARENT`) all behaved as designed (ADR-0004).
- [x] Stop/restart: Stop → status "Captions stopped.", Stop button disabled; Start again → "Capturing system audio." and a second session transcribed fresh audio.
- [x] Lifecycle: rapid Stop → close (Stop at 18:27:25.105, WM_CLOSE +118 ms) exited the process cleanly in ~2 s with no errors on stdout/stderr and no lingering process — bounded background teardown verified.
- [x] Error path: launched with `UC_STT_MODEL_PATH=C:\does-not-exist\ggml-base.bin` → Start surfaced the user-readable status "Whisper model file 'C:\does-not-exist\ggml-base.bin' was not found." with no crash.
- [x] Translation config guard: with source == target (`en`→`en`), enabling translation rejected it live with "Translation into en is not supported because the captions are already in en." (the `TranslationGuard` message; `SourceEqualsTarget`).
- [x] **Real-Argos wiring (end-to-end through the App):** Recreated the Argos venv (`argostranslate==1.11.0` + en→tl, tl→en, ja→en, en→ja packages under `C:\Users\TOGODB~1\AppData\Local\Temp\argosv`, short 8.3 path per TD-011), prepended its `Scripts` dir to PATH, and verified the line-protocol server directly (en→tl "Hello world, ito ay isang live kapsiyon test." ~2.5 s) with both the full venv python path and the bare PATH-resolved `python`. In the live App run (2026-08-01): toggled translation ON, selected **Tagalog (tl)** in the target combo, started captions, played speech via SAPI, and **committed overlay lines displayed real translated Tagalog** — `tamad aso.` and `Ang mabilis na kayumangging sorra ay tumatalon sa ibabaw ng eruplano` — with `IsTranslated = True` on the committed history lines. The App spawned the Argos child chain (`python` venv shim → UV base python running `argos_translate_server.py`), which served the translations. This also exercised the `ControlWindow.ApplyTranslationSettings` guard fix (toggle stays ON + target combo stays enabled on a guard error so a valid target can be selected). Engine-level real-Argos verification (direct + pivot pairs, offline) is in [BENCHMARK_REPORT.md](BENCHMARK_REPORT.md) (Slice 3 section).
- Note (latency): first-session Whisper latency read ~23 765 ms / 38 581 ms on the second (under ambient machine load with the `ggml-base` model on this CPU). This is a real observed measurement, not a code defect; latency tuning is Slice 6 work (window size, decode interval, `StabilityWindow`).

**Result: Passed** for overlay/control-window behavior, device enumeration, real capture → Whisper → overlay captions, interaction (move/resize/click-through), lifecycle (stop/restart/clean close), the model-not-found + source-equals-target error paths, and the **real-Argos wiring end-to-end through the App** (committed overlay lines translated to Tagalog by a real local Argos child process).

## Slice 5 Post-Close-Out Refinement — Live Active-Line Translation + Chrome-Style Overlay (Entry 7)

Automated (done, 2026-08-01):

- `CaptionService` (9 new tests) — live active-line translation: a partial is translated into the target language while the speaker is still talking; translation off makes no active-line request; a failure preserves the source; a **single in-flight slot** serializes requests and self-replenishes to translate a newer partial that arrived meanwhile; a **stale result** for a superseded partial is discarded and never surfaced; a result is discarded when its line was committed or when translation was **disabled mid-flight**; `CaptionLineUpdated` fires when a live translation applies; enabling translation mid-session translates the current partial.
- `CaptionState.ReplaceActiveLine` (4 new tests) — active-line translation replacement by exact line identity (applies; stale instance rejected; after-clear no-op; state validation).
- `CaptionDisplayPolicy` (2 new tests) — the overlay model exposes an uppercase target-language badge when translation is enabled, none when disabled.

Manual (**completed 2026-08-01**, this Windows 10 machine — build 19045): with the redesigned overlay (auto-sized translucent pill, white text, target-language badge, expand/collapse chevron, hide button) and live active-line translation, ran the real audio + real-Argos pass with the App started against the WASAPI loopback device and the dev Argos venv on PATH (target `tl`). **Passed.** SAPI-paced English speech was captured by loopback, transcribed by Whisper `ggml-base`, and **live-translated into Tagalog on the in-progress overlay line while the speaker was still talking, before commit**. A 300 ms UIA poll timeline of the `ActiveCaption` element shows the English partial being replaced by Tagalog on the active line within ~0.2–1 s of the partial appearing (e.g. `TS=4.011 ' world Okay'` → `TS=4.241 'Daigdig Okay'`; `TS=8.517 '...This.'` → `TS=8.724 '...Ito.'`; `TS=12.514 'Ito ay'`; `TS=23.233 'Pagsasalin'`; `TS=27.118 'pagsubok'`; `TS=53.475` full-sentence `Ang mabilis na brown fox ay lumukso sa ibabaw ng tamad na aso.`). The `TL` badge (`LanguageBadge`) was present in every sample. Chevron expand/collapse verified: expanding reveals the committed-history list (8 committed `CaptionDisplayLine` items, all `IsTranslated = True`, e.g. `Daigdig Okay`, `Okay Hello Ito`, `mabuhay. Pagsasalin. pagsubok.`, `TUST 1. 2. 3.`), and the pill auto-sizes 235→109 px. Close (X) verified: overlay window leaves the UIA tree. "Show Captions" verified: overlay re-appears (`IsOffscreen=False`). Pipeline-while-hidden verified: with the overlay hidden, spoken English was still transcribed and live-translated (`The meeting starts at nine o'clock` → `Nagsisimula ang pulong sa alas - 9.` on re-show; history grew to 9 committed lines, all translated). Note: the previous manual items that referenced the 720×180 fixed-size overlay, the resize grip, and "translated text on committed lines only" are superseded by this redesign.

## Slice 6 — Phase 1c: App-Level SAPI E2E Validation (shortlist vs baseline)

Completed **2026-08-01**, this Windows 10 machine (build 19045), Release App build. **Purpose:** the validation gate between the controlled OFAT sweep (Phase 1b) and real-world apps (Phase 2) — measure real end-to-end latency through the real App with real WASAPI loopback audio + the real local Argos child process, at baseline + shortlisted configs.

### Protocol (identical for every run)

- **Harness:** fresh `UniversalCaptions.App.exe` process per run (Release, `bin/Release/net8.0-windows`), working dir = repo root; Argos dev venv `Scripts` dir prepended to PATH; translation ON + target **Tagalog (tl)** selected in the control window; Start Captions; SAPI-paced fixed English corpus; then Stop + window-close (all via UIA). No parameters changed between repetitions within a config.
- **Speech:** fixed 6-sentence English script (≈30 s, `SpeechSynthesizer` rate 0, volume 100) played through the default render device captured by WASAPI loopback. Same text/device/settings every run.
- **Measurement:** a 100 ms UIA poll of the control-window **E2E latency row** (`partial: … ms · final: … ms`, Phase 1a) and the **STT latency** row for the whole speech + a 12 s settle tail; every distinct value that the gauges advanced to was recorded (each advance = a translated caption actually published to subscribers). Per run we record the multiset of observed E2E partials/finals + last STT latency + the overlay's final active-caption text (Tagalog evidence). Raw series: `artifacts/reports/e2e/series.csv`; per-run aggregates: `artifacts/reports/e2e/runs.csv` (git-ignored).
- **Configs × 3 runs each:** baseline/control `base 8 s/1 s/st3` (App defaults, via `UC_STT_STABILITY=3`), shortlist A `base 8 s/1 s/st2` (`UC_STT_STABILITY=2`), shortlist B `tiny 8 s/1 s/st2` (`UC_STT_MODEL_PATH=…ggml-tiny.bin` + `UC_STT_STABILITY=2`).

### Results (all E2E = audio capture time `CapturedAtUtc` → translated caption published; see Phase 1a definition)

| Config (model / window / interval / stability) | E2E final median | E2E final worst | Warm last-final E2E median | E2E partial median | Last STT latency median | Translated finals observed | Translation published? |
|---|---|---|---|---|---|---|---|
| **base / 8 s / 1 s / st3** (baseline/control) | 20.96 s | 29.3 s | 9.06 s | 5.54 s | 6.49 s | 10 (3–4 per run) | Yes — all 3 runs |
| **base / 8 s / 1 s / st2** (shortlist A, accuracy-first) | 19.65 s | 29.4 s | 10.98 s | 6.00 s | 4.18 s | 16 (4–8 per run) | Yes — all 3 runs |
| **tiny / 8 s / 1 s / st2** (shortlist B, latency-first) | 16.25 s | 24.6 s | 7.45 s | 4.41 s | 3.61 s | 18 (4–8 per run) | Yes — all 3 runs |

Reading the table:
- **E2E final median/worst** = distribution of every translated-final E2E sample across the 3 runs. These are inflated by the **Argos cold start** on the first translated line of each session (~14 s process + model load; every run launches a fresh App process, so every run pays it) plus the trailing-window component; the **warm last-final E2E median** isolates the steady-state last line (the last final of the script, warm Argos).
- **E2E partial median** = live active-line translation E2E samples. These are sparse (1–3 per run; none in `tiny-st2` run 2) by design: the single in-flight Argos slot supersedes most partials before their translation completes, so only 0–3 translated partials actually apply per session. Treat partial medians as low-confidence.
- **Last STT latency median** = capture→emit latency of the last committed final (the `LatencyUpdated` value; unchanged metric).
- **Translated finals observed** = count of distinct translated-final E2E samples (≈ translated committed lines published per run), a commit-rate proxy consistent with the Phase 1b finding.
- Sample counts per run are 3–8 finals, so **P95 ≈ worst** (no meaningful P95 below the max at these N); the "worst" column is the max across run maxima.

### Findings

- **tiny/8/1/st2 is the latency winner end-to-end:** lowest E2E final median (16.25 s vs 20.96 s baseline, −4.7 s, i.e. ~22% lower), lowest warm last-final E2E (7.45 s), lowest STT latency (3.61 s), and the most translated finals (18 vs 10) — it commits text soonest. Consistent with Phase 1b (tiny decodes faster → more stability passes → more commits), with the known accuracy trade-off (OSR full-file WER 16.0% vs base 4.9%).
- **base/8/1/st2 ≈ baseline on E2E final** (19.65 vs 20.96 s, within run-to-run noise) **but commits more finals** (16 vs 10) and has lower STT latency (4.18 vs 6.49 s) — st2 commits faster with identical model accuracy, as the OFAT sweep predicted (first-final cut ~2.1–2.4 s).
- **base/8/1/st3 (control)**: fewest finals, highest STT latency — correct as the conservative default.
- **All 9 runs published real translated Tagalog** (`SawTranslation = True`), e.g. overlay active captions `"Sinusubukan namin ngayon ang live na mga kapsiyon sa makinang ito."`, `"Nabihag ng sistema ang audio, nakilala ang talumpati, at isinalin ito sa Tagalog."` — no missed/failed translations observed. No lingering processes after runs.
- **Caveats:** run-to-run variance is real (ambient CPU, Argos session warm-up); 3 reps is a minimum; first-final E2E is dominated by Argos cold start in every config, so the config ranking rests on warm finals + STT latency + commit rate.

### Phase 1c conclusion

The shortlist is validated end-to-end through the real App. The latency-first candidate is **tiny/8/1/st2**; the accuracy-preserving candidate is **base/8/1/st2**; the previous-default control is **base/8/1/st3**. Per the user's decision, the validated baseline **`base/8/1/st2` was promoted to the App default on 2026-08-01**: `StabilityWindow` 3→2 (`WhisperEngineOptions` + App + benchmark, one authoritative config), model default `ggml-base` unchanged (see `PROJECT_STATUS.md` "Slice 6 Baseline Defaults"). This is the **validated baseline for the current release**; Phase 2 (YouTube/Chrome, VLC, Zoom) remains **deferred per user** — real-world validation/reassessment, not additional optimization sweeps — and the defaults may be revisited after it.

## Known Gaps / Deferred

- Real-device verification was performed on this machine only; a second output device or a different machine is not recorded.
- Resampler benchmark against speech signals: **closed (TD-001, 2026-08-05)** — no recognition regression to fix; current windowed-sinc kept (see BENCHMARK_REPORT TD-001).
- Device-change notifications + automatic recovery (TD-002): **contract + production wiring implemented + tested (2026-08-05)**; real hotplug verification is pending (TD-002 stays Open until it passes).
- Default model: **ggml-base** (user-approved; tiny kept as a low-resource fallback) — see ADR-0003 / BENCHMARK_REPORT.
- Slice 5 manual overlay/device verification **completed 2026-08-01** (recorded in the Slice 5 section above), including the **real-Argos wiring end-to-end through the App** (committed overlay lines translated to Tagalog via a real local Argos child process); **Slice 5 closed out 2026-08-01**.
- **Slice 6 is complete (close-out 2026-08-01)** — Phases 1a (E2E metric + tests), 1b (OFAT sweep + shortlist), and 1c (App-level SAPI E2E validation) all complete; the validated baseline **base/8/1/st2 was promoted to the App default** (`StabilityWindow` 3→2; model `ggml-base` unchanged — see `PROJECT_STATUS.md` "Slice 6 Baseline Defaults"). **Phase 2 — real-application validation (YouTube/Chrome, VLC, Zoom) — is deferred per user** and is a future reassessment pass over the baseline defaults, not a prerequisite for the current release.

## Conclusion

Slice 1 Definition of Done is satisfied (build green, tests pass, real-device capture recorded). Slice 2 Definition of Done is satisfied on tests and evidence (107 tests total, streaming finals committed on every sample, build 0 warnings/0 errors, default model user-approved). Slice 3 Definition of Done is satisfied on tests and evidence: the `ITranslationEngine` contract is verified with a fake engine (8 tests), `ArgosTranslationEngine` is verified with a fake process seam (13 tests) and against the real Argos 1.11.0 process (direct pairs + pivoting + error mapping, offline), and the translation benchmark is recorded (128 tests total, build 0 warnings/0 errors). Fresh-context review findings were fixed (stale-process recovery, unwrapped exceptions, Python crash path, options validation, `ArgumentList`, UTF-8 pinning) and the remaining items logged as TD-013–TD-015.

Slice 4 — Caption Service is **complete** (close-out approved 2026-08-01): `ICaptionService`/`CaptionLine`/`CaptionState` contracts live in `UniversalCaptions.Core.Captions`, and `CaptionService` in `UniversalCaptions.Captions` consumes only Core. The partial→active→final→committed transition, optional background translation (failure preserves the source caption), ordering, duplicate prevention, bounded history, session lifecycle, and cancellation are verified with deterministic fake translation engines (40 tests, build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, no vulnerable packages). A fresh-context review was completed and its findings fixed (snapshot history reads, deferred CTS disposal, atomic translation-start token, stale-translation identity guard, event-raising moved out of the translation catch, target normalization). All Slice 4 Definition-of-Done items are satisfied.

Slice 5 — Overlay + Control Window is **complete (close-out 2026-08-01)**: `UniversalCaptions.App` implementation and its deterministic unit tests are complete — 209/209 tests total (66 Audio + 45 Captions + 41 Speech + 21 Translation + 36 App), build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, no vulnerable packages. The resolved Q1 display policy and the capture→processor→STT→caption-service wiring are verified with fakes. **Manual overlay/device verification completed 2026-08-01**: real system audio → Whisper `ggml-base` → live overlay captions, always-on-top/transparency, drag/resize/click-through, stop/restart, rapid Stop→close (clean ~2 s exit), and the model-not-found + source-equals-target error paths all verified on this Windows 10 machine. **Real-Argos wiring verified end-to-end through the App 2026-08-01**: with the dev Argos venv (recreated under a short 8.3 path per TD-011), committed overlay lines displayed real translated Tagalog (`tamad aso.`, `Ang mabilis na kayumangging sorra ay tumatalon sa ibabaw ng eruplano`, `IsTranslated = True`) served by the App-spawned Argos child process; this also exercised the `ApplyTranslationSettings` guard fix (toggle stays ON + target combo enabled on guard error). All Slice 5 Definition-of-Done items are satisfied.

**Post-close-out refinement (Entry 7, 2026-08-01):** live active-line translation + Chrome-style overlay redesign are implemented with automated tests **224/224** (66 Audio + 58 Captions + 41 Speech + 21 Translation + 38 App), build 0 warnings/0 errors, format clean. **Manual verification of the redesigned overlay + live active-line translation completed 2026-08-01** (recorded in the Slice 5 refinement note above): Tagalog appears on the in-progress overlay line before commit, `TL` badge, chevron expand/collapse of history, close-hide, "Show Captions" re-show, and pipeline-continues-while-hidden all verified against real audio + real Argos. **Entry 7 closed out 2026-08-01.**

**Slice 6 (Entry 8) — complete (close-out 2026-08-01):** Phase 1a (E2E latency metric + tests, **238/238**) and Phase 1b (OFAT sweep + shortlist) are complete, and **Phase 1c — App-level SAPI E2E validation — completed 2026-08-01** (recorded in the Slice 6 Phase 1c section above): baseline + shortlist configs × 3 runs each through the real App (loopback → Whisper → Argos en→tl → overlay), every run publishing real translated Tagalog. Latency winner **tiny/8/1/st2** (E2E final median 16.25 s incl. Argos cold start; warm last-final 7.45 s; STT 3.61 s; 18 translated finals); accuracy-preserving candidate **base/8/1/st2** (commits faster than the old default with identical model accuracy); control **base/8/1/st3**. **The validated baseline `base/8/1/st2` was promoted to the App default on 2026-08-01** (`StabilityWindow` 3→2, model `ggml-base` unchanged — one authoritative config) as the validated baseline for the current release. A fresh-context review of the Phase 1a E2E metric code completed clean (no findings). Phase 2 real-app validation (deferred per user) is a future reassessment pass over the baseline defaults. **All MVP slices (0–6) are complete.**

## Argos Pre-Warm — First-Caption Latency Verification (2026-08-02)

**Manual verification of the Argos background pre-warm, real Windows 10 machine.** Objective: reduce first-caption latency from the ~28-34 s Argos cold-start to ~5-7 s by warming Argos (Python + language discovery + model load, then first `en->tl` inference) in the background when translation is enabled, so the first real caption reuses a warmed process/model.

**Harness:** UIA-launched `UniversalCaptions.App.exe` (Release, cwd = repo root), translation ON + target **Tagalog (tl)**, Argos dev venv python via `UC_ARGOS_PYTHON`, SAPI-paced English clip over default render (WASAPI loopback), App stderr captured to files for `[DIAGNOSTICS] T0-T8` + `[ARGOS-DIAG]` traces.

### Case A — warm-up finishes before playback (headline fix)

| Measurement | Before (cold) | Case A observed |
|---|---|---|
| First audio (T1-T0) | ~0.06 s | **0.064 s** ✓ |
| Whisper Partial (T3-T2) | ~2.0 s | **2.060 s** ✓ |
| Whisper Final (T4-T3) | ~3.6 s | **3.553 s** ✓ |
| Argos first translation (T6-T5) | **23.06 s+** | **0.463 s** (real id=3 round-trip **0.454 s**) |
| First caption (E2E final) | **~28-34 s** | **3.80 s / 6.85 s** |

- `pre-warm ready in 24.2 s` finished ~30 s clip; cold costs paid before first caption.
- Post-warm translation round-trips **0.17-1.50 s**. The 20-30 s Argos gap is gone; first caption ~4-7 s.

### Case B — speech starts during warm-up (concurrency)

- Clip played ~2 s after Start, before background pre-warm finished.
- Exactly **one** process spawn (`T5b ... 0.011 s`) and **one** model load (`T5c/T5d ... 13.4 s`); the first real translation awaited the same shared `_startTask`/`_warmTask` and completed in **0.355 s** once warm finished — **no second process/initialization**.
- The ~23 s first caption in this case is expected/correct: playback began before warm-up was done, so the real caption waits on the one warm-up rather than triggering a duplicate.

**Result: Passed.** Case A meets the ~5-7 s first-caption target; Case B confirms the single shared initialization + lazy-fallback concurrency requirement. Tests 260/260, build 0 warnings/0 errors, format clean; baseline defaults unchanged.

## Slice 7 — Caption Layout & Stable Incremental Rendering (2026-08-02)

**Slice:** stable incremental rendering (A) + scope-limited bottom scrolling (C), after a measurement-first diagnosis of the reported "whole text re-flows / newest content jumps" symptom. Translation (Whisper/Argos/latency) path untouched.

### Task B — width/measurement diagnosis (probe, deterministic STA)

Layout probe `CaptionLayoutProbeTests` recreates the exact overlay tree `ScrollViewer(522px viewport) → Grid → StackPanel → TextBlock(font 20, L260)` and measures real WPF layout:

| Case | Realized width | Available text width | Wrapped lines |
|---|---|---|---|
| "two words" (short) | ~522 px | ~522 px | **1** |
| long sentence | ~522 px | ~522 px | ≥ 2 (wraps only on exhaustion) |
| "the quick" vs long tail | ~522 px (constant) | ~522 px | grows, width constant |

**Verdict: width is correct.** A caption fills the full ~522px viewport and stays on one line for short utterances (it does not measure at its natural word width, so appended tails don't force premature new lines); long text wraps only when 522px is exhausted; growing tails keep a constant fill width. The reported reflow is therefore **not** a width/measurement problem — it must be in the render path.

### Task A — stable incremental rendering (fixed + verified by render-identity test)

`UpdateCaptionItems`/`ReconcileHistory` now return whether a new block was inserted. A Partial only mutates the live `TextBlock`'s `Text` in place; history `TextBlock` instances are reused by sequence and never rebuilt; a Final inserts the committed line as a fresh history block while the single live block is reused for the next phrase.

`CaptionRenderIdentityTests` (4) drive the real `CaptionOverlayWindow` (STA + reflection) and assert **block instance identity is preserved**:
- Partial stream → identical history instances before/after, only active text changes;
- growing Partial → same active instance, text updated in place;
- Final → finalized text becomes history, live block reused for next partial;
- multiple finals → first/second history instances stay `Assert.Same`, order + text preserved.

### Task C — verification via scope-limited bottom scroll

The overlay no longer forces `ScrollToBottom` and no longer re-runs the bottom re-anchor on every caption render. It scrolls only when a new caption block was inserted (a Final or the first line) and the content overflows the fixed-height viewport; a Partial that rewrites the live line alone never scrolls and never reflows history. Window re-anchor runs only on Loaded / collapse / hover (where size actually changes).

**Gates:** App tests 51 → **58** (3 layout probe + 4 render-identity); solution **267/267** (66 Audio + 71 Captions + 45 Speech + 27 Translation + 58 App); build 0 warnings / 0 errors; `dotnet format --verify-no-changes` clean; baseline defaults unchanged; Whisper/Argos/latency path untouched.

## TD-001 - Windowed-sinc vs NAudio `WdlResampler` benchmark (2026-08-05)

**Purpose:** close TD-001's open question — does replacing the current `<SampleRateConverter>`
(windowed-sinc) with NAudio `WdlResampler` improve real-time STT performance without degrading
audio/recognition? Benchmark-only first pass; no production replacement is made here.

**Automated suite - PASS.** Full suite **302/302 passing** (66 Audio + 72 Captions + 77 Speech + 27
Translation + 60 App), Release build 0 warnings / 0 errors. No product code changed — a `resample`
command was added to `UniversalCaptions.Benchmarks` (new `ResamplerBenchmark.cs`; the benchmark
project now references `UniversalCaptions.Audio` for `SampleRateConverter`).

**Manual execution evidence (Release, ggml-base, 6 STT threads, best-of-5 runs, 0.5 s chunks):**

```
Command: dotnet run --project src/UniversalCaptions.Benchmarks -c Release -- resample --repeats 5   (jfk.wav clean)
impl      path         wall_ms realtime  cpu_ms  MB_alloc out_frames
control   16k->16k           0   0.00x       0        0.0      176000
sinc      44.1k->16k       400   0.04x     375        5.7      175984
wdl       44.1k->16k        13   0.00x      16        3.0      175992
sinc      48k->16k         356   0.03x     359        6.1      175984
wdl       48k->16k          13   0.00x      16        3.2      175992

STT (ggml-base full-file, lang en):
control 16k->16k : decode 2163 ms (0.20x)  WER 0.0%
sinc  44.1k->16k : decode 2144 ms (0.19x)  WER 0.0%
wdl   44.1k->16k : decode 2170 ms (0.20x)  WER 0.0%
sinc  48k->16k  : decode 2133 ms (0.19x)  WER 0.0%
wdl   48k->16k  : decode 2158 ms (0.20x)  WER 0.0%
```

Second run (`--wav artifacts/samples/jfk_noisy.wav`, +10 dB SNR): sinc 401–411 ms / 5.7–6.1 MB;
wdl 13–14 ms / 3.0–3.2 MB; **all five rows again 0.0% WER**.

**Result — TD-001 closed (2026-08-05): keep the current windowed-sinc resampler.** WDL is ~30x
faster and ~half the allocations, but STT/audio is equivalent (0.0% WER clean + noisy) and the
current sinc already runs 0.03–0.04x realtime, so resampling does not materially contribute to live
latency (decode dominates by >10x; ~0.4 ms/chunk saving is unobservable). No production change.
Findings + decision in `docs/reports/BENCHMARK_REPORT.md` (TD-001).

## TD-002 — Device-change notifications + automatic recovery (2026-08-05)

**Purpose:** close the TD-002 recovery UX gap (device hotplug requires a manual restart; a default-device
switch silently keeps the old endpoint) with the trace → design → deterministic-tests discipline. The
**notification/recovery contract** and the **production wiring** (monitor → `DefaultDeviceAutoRecovery` →
`CaptionPipeline` → App DI) are both implemented and driven with deterministic fakes; **real hotplug
verification is the only remaining gate** (TD-002 stays Open until it passes).

**Trace (current behavior):** `WasapiLoopbackCaptureSource` wraps NAudio `WasapiLoopbackCapture`; on
device invalidation WASAPI raises `AUDCLNT_E_DEVICE_INVALIDATED` → `RecordingStopped` → `CaptureFailed`
(`DeviceDisconnected`) → `CaptionPipeline.OnCaptureFailed` stops the session → **manual restart only**.
No `RegisterEndpointNotificationCallback` exists; a default-device switch to a still-live endpoint
silently continues capturing the old device.

**Contract added:**

- `UniversalCaptions.Core.Capture` — `IDeviceChangeMonitor` (`DeviceChanged` event + `Start`/`Stop`),
  `DeviceChangeNotification` (`Kind`/`DeviceId`/`State`), `DeviceChangeKind`, `DeviceState` (Core-pure).
- `UniversalCaptions.Audio` — `WasapiDeviceChangeNotifier`: implements `IMMNotificationClient`,
  registered via `MMDeviceEnumerator.RegisterEndpointNotificationCallback`; **lazy** `MMDeviceEnumerator`
  so unit tests invoke the `IMMNotificationClient` methods directly with no COM/audio service; surfaces
  only `DataFlow.Render` (output) changes.
- `UniversalCaptions.App` — `DefaultDeviceAutoRecovery`: while the session is on the system default
  device, restarts it on default-device change, unplug/not-present, or a device removal; explicit-device
  sessions never auto-restart; burst notifications coalesce into one restart.
- `UniversalCaptions.App` — `CaptionPipeline` wiring: an optional `IDeviceChangeMonitor` composes a
  `DefaultDeviceAutoRecovery`; a live default-device session calls `Start` (monitor on) and stops it on
  teardown. `RestartCaptureAsync` detaches + disposes the stale capture, re-queries the default device,
  and recreates a capture chain **while preserving the speech engine unchanged**; guarded against stop/
  dispose races, duplicate sessions, and faulted/disposed restarts. A failed recovery stops the session
  in a controlled error state. `App.xaml.cs` registers `WasapiDeviceChangeNotifier` as the monitor.

**Automated tests — PASS.** `WasapiDeviceChangeNotifierTests` (11/11) + `DefaultDeviceAutoRecoveryTests`
(9/9, incl. `Removed` now a restart trigger). Plus **7 `CaptionPipeline` recovery tests**. Full suite now
**329/329 passing, 0 failed, 0 skipped** (77 Audio + 72 Captions + 77 Speech + 27 Translation + 76 App),
Release build 0 warnings / 0 errors.

**Notifier contract (direct `IMMNotificationClient` invocation, no hardware):**

1. `DefaultDeviceChanged` on `DataFlow.Render` → `DefaultDeviceChanged` notification with the new endpoint id.
2. `DefaultDeviceChanged` on `DataFlow.Capture` → filtered (no event).
3. `DeviceStateChanged` `Unplugged`/`Disabled`/`Active`/`NotPresent`/`All` → `StateChanged` with the mapped state + id.
4. `DeviceAdded`/`DeviceRemoved` → `Added`/`Removed`.
5. `PropertyValueChanged` → ignored. Dispose → no further events.

**Recovery coordinator (fake monitor, deterministic):**

1. Default-device change while on default device → restart default.
2. Default-device change while on an explicit device → **no** restart.
3. `StateChanged` `NotPresent`/`Unplugged` while on default → restart; `Active` → no restart.
4. `Removed` while on default → restart; `Added` → no restart.
5. A burst of notifications while a restart is in flight → coalesced into one restart.
6. After `Stop` / `Dispose` → no restart.

**Pipeline recovery (fake monitor + fake capture, deterministic):**

1. Default-device change → stale capture disposed, a new capture created + started on the default, the
   same speech engine kept recognizing, pipeline running.
2. `Removed` on default → capture recreated + started.
3. Explicit-device session → monitor not started, no recovery, original capture preserved.
4. Burst of notifications → exactly one recovery session (no duplicates).
5. `Stop` → no recovery; `Dispose` (shutdown) → no recovery.
6. Recovery failure (no default device) → controlled Error status + session stopped.

**Gates:** full suite **329/329**; Release build 0 warnings / 0 errors; `dotnet format --verify-no-changes`
clean. Change-impact analysis Entry 9; TD-002 row status updated. Production wiring is complete;
**real hotplug verification remains pending — TD-002 stays Open until it passes.**

## Path A — Tagalog real-world validation (v0.5.31, 2026-08-10)

**Purpose:** minimal apples-to-apples validation that the v0.5.31 production STT default
(`fasterwhisper-native` + live partials, `UC_NATIVE_THREADS=4`) materially outperforms the
`ggml-base` fallback on real Tagalog audio in the live App, using only the operator recording
and the existing harness. The earlier WER evidence (~33 % vs ~51.2 % committed WER, Entry 14 /
Slice 10) is **context** — this run independently measures first-caption latency, first
engine FINAL, total system CPU, and naturalness of the live overlay output on the same audio.

**Protocol (identical for both legs):**

- Audio: `artifacts/samples/first_meeting_tagalog_90s.wav` (operator Tagalog, 90 s).
- App: `src/UniversalCaptions.App/bin/Release/net8.0-windows/UniversalCaptions.App.exe` (Release).
- Settings: `smoke_settings_raw_tl.json` (Tagalog source, translation OFF).
- Player: VLC `--intf dummy --no-video --volume 256 --play-and-exit` against the default WASAPI
  loopback device.
- Harness: `acceptance-tagalog-compare.ps1` (sidecar to `acceptance.ps1`; same UIA sampling
  loop, CSV / `_captions.txt` / log format; **does not** reset `UC_STT_ENGINE` so the engine
  can be pinned per leg). `-Duration 120 -SampleMs 500 -Warmup 12`.
- Killed between legs: App, faster-whisper workers, Argos worker, VLC.

**Results:**

| Metric                                         | Production default (`fasterwhisper-native` + partials, threads=4) | `ggml-base` (`UC_STT_ENGINE=ggml-base`) |
| ---------------------------------------------- | ----------------------------------------------------------------- | --------------------------------------- |
| Engine confirmed                               | `[FW-DIAG] worker spawned`; 2 python workers (faster-whisper + small int8) | 0 python workers (in-process ggml-base) |
| First overlay caption                          | **9.80 s**                                                       | **36.24 s**                             |
| First Whisper Partial (engine T3)              | **5.11 s**                                                       | 16.37 s                                 |
| First Whisper Final (engine T4)                | **8.23 s**                                                       | 27.99 s                                 |
| Caption snapshots in 120 s                     | 29                                                               | 40                                      |
| App CPU system mean                            | 1.3 %                                                            | 205 % (in-process — includes STT)        |
| STT CPU system mean                            | 32.2 %                                                           | 0 % (in-process)                        |
| Total system CPU mean (App + STT)              | ~33.5 %                                                          | ~205 %                                  |
| Clean exit / 0 orphaned workers                | ✅                                                               | ✅                                       |
| First Tagalog phrase visible                   | `Kumusta?` (T40.3 s)                                             | `Magandang umaga. Good morning.` (T54.2 s) |
| Natural Tagalog word order produced             | Yes — `Kumusta?`, `Magandang umaga!`, `Anong pangalan mo?`, `Masaya ako makilalaka`, `Kumusta ka?`, `Mabuti naman.` | Partial — heavy phonemic errors |
| `ako` rendered as                              | `ako` / `ako'ng` (correct)                                       | garbled / `alpangalan ko`               |
| `Maria` rendered as                            | `Maria` (correct)                                                | `May neymun`, `Maria` (inconsistent)   |
| `Juan` rendered as                             | `Juan` (correct)                                                 | not yet produced                        |
| Known `"one"`/`"ako"` residual artifact         | Present — `ang pangalan ko ay one.`                              | Absent in this run; matching mangling (`May neymun`) instead |

**Live overlay evidence (representative):**

- Production default (T86–T108 s): `... || ang pangalan ko ay one. || Masaya ako makilalaka. || My name is Juan. Nice to meet you. || Masayarin ako'ng maakilalaka ONE || Kumusta ka? || Nice to meet you, one. How are you? || Mabuti naman.`
- ggml-base (T71–T78 s): `... || Maria, alpangalan ko. Ikao. Anong pangalan mo. || May neymun. || May name is Maria and you. What is your name?`

**Decision:** **Retain `fasterwhisper-native` + live partials (threads=4) as the v0.5.31
production default.** Production default delivers **3.7× faster first overlay caption**,
**3.4× faster first engine FINAL**, materially cleaner Tagalog naturalness, and **~6× less
total system CPU** than `ggml-base`. The known `"one"`/`"ako"` residual artifact remains
documented; this validation does not claim perfect Tagalog accuracy. Live behavior is
consistent with (not independently re-measuring) the prior ~33 % vs ~51.2 % WER gap.

**Artifacts (untracked, retained locally for forensic comparison):**

- `acceptance_tl_prod.log`, `acceptance_tl_prod.csv`, `acceptance_tl_prod_captions.txt` —
  Run 1, production default.
- `acceptance_tl_ggml.log`, `acceptance_tl_ggml.csv`, `acceptance_tl_ggml_captions.txt` —
  Run 2, `ggml-base`.
- `acceptance_summary.csv` — appended both rows.
- `acceptance-tagalog-compare.ps1` — the sidecar harness.

**No changes made to the frozen v0.5.31 release, the production defaults, or the core.**
