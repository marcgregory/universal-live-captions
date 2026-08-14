# CLAUDE.md

## Project

Universal Live Captions

## Purpose

Chrome-Live-Caption-like live captions for any Windows application. Captures system audio via WASAPI loopback (no VB-CABLE), runs local streaming speech-to-text, optionally translates locally, and renders an always-on-top caption overlay. Windows 10 target (build 17763+).

## Current Sprint

**Corpus-driven phrase-guard validation — CLOSED (2026-08-14; decision: INSUFFICIENT EVIDENCE — do
not ship; no production change; v0.5.40 gate untouched; no v0.5.41).** Second, corpus-driven validation
authorized by the closed segmentation-matrix decision. `PhraseGuardCorpusValidationTests.cs` (11 tests,
43-case labeled corpus) drove the real engine gate per case (baseline, measured — never assumed) and
layered a **test-side** phrase guard, measuring **false-split reduction − over-join cost** per candidate.
**Measured:** all 7 observed Cat 2 false splits FLUSH under the current gate (gap real); every Tagalog
phrase guard nets positive (`At pagkatapos` **+4** best, `Sige, gawin` +2, `At makinig`/`Kaya
kailangan`/`Pero pagkatapos`/`Dahil dito`/`Hindi <fragment>` +1) while the rejected bare
`at|kaya|sige|hindi` allowlist over-joins **8** genuine new sentences (negative control); multi-word
guards are exact-token (`At bukas…`, `Kaya narito…`, `Sige, magsisimula…` NOT caught — the concrete
improvement over the bare allowlist); **English equivalents (`And then`/`So we need`/`But then`/`Not`)
all net 0** (reduction cancels over-join on the en side); two **irreducible same-surface ambiguities**
proven (`Kaya kailangan nating magmadali.` is both the fix and a genuine new-sentence reading; the
`Hindi` prefix fixes `Hindi Lunes.` but over-joins `Hindi ko alam kung saan ito.`). **Decision (user
gate): do not ship** — the validation proved the guard's *mechanics* but not the real-world over-join
cost; the over-join cases are constructed, not frequency-measured. **Established:** bare-word allowlist
= reject (unsafe); English equivalents = no net benefit; phrase guard = technically reduces observed
Cat 2 failures; same-surface ambiguity = irreducible with lexical info alone; **frequency-weighted
real-world cost = unknown** (the deciding unknown). **Do not keep expanding the lexical phrase list**
until that frequency question is answered. Full suite **711/711** (106 Audio + 89 Captions + 111 Speech
+ 42 Translation + 184 App + 179 Speech.Gemini; the 49 matrix tests stay green), Release 0 warnings/0
errors, `dotnet format` clean. Evidence + results + decision:
`docs/implementation/investigations/phrase-guard-validation.md`.

**Gemini streaming-caption segmentation investigation + matrix — COMPLETE (2026-08-14, measurement
only; no production change; decision: production gate unchanged).** 20-run real-Gemini study: Gemini
streams ~1 fragment/s (median gap 1000 ms, p90 1244 ms); the app pipeline adds **zero** latency
(FINAL→COMMIT→RENDER all 0 ms median / 1 ms p90); first visible caption median **8.72 s** (primary) /
**9.71 s** (secondary), dominated by STT first-FINAL + Gemini first-token. Root cause of residual
false splits: the v0.5.40 guard `terminal && !restate && !lowercase` only catches **lowercase**
continuations — capitalized mid-sentence continuations (`Hindi Lunes.`, `At pagkatapos`, `Sige.`) still
split ("…Friday, not Monday" in **6/10 runs**; "…plan. At pagkatapos…" in **5/10**); fragmentary
captions (len<15) rise to **9.8 %** on the boundary-stress clip (vs 2.2 % primary). **Under-
segmentation (two real sentences joined) also occurs**, so a more aggressive guard is NOT an automatic
win. **Decision-gate unit-test matrix executed (48 runs: 41 PASS / 7 FAIL; measurement only, no code
change):** Cat 1 lowercase continuation → APPEND ✓; **Cat 2 capitalized continuation idioms (7: `Hindi
Lunes.` len-12 regression, `At pagkatapos…`, `At makinig…`, `Kaya kailangan…`, `Sige, gawin…`, `Pero
pagkatapos…`, `Dahil dito…`) → FLUSH ✗ (the measured gap)**; Cat 3 bare-starter pairs (`At`/`Kaya`/
`Sige,`/`Hindi`) → both members identical, i.e. **provably ambiguous** — a bare `At|Kaya|Sige|Hindi →
APPEND` allowlist is **unsafe** (over-joins the new-sentence reading); Cat 4 genuine new sentence →
FLUSH ✓. **Decision: the dangerous axis is insufficient context, not capitalization.** The 7 Cat 2
cases are known defects with a **candidate** mitigation (phrase-level idiom guard) but not sufficient
evidence to ship it. **Production gate unchanged.** A second, smaller **corpus-driven validation**
(observed idioms → APPEND; same idioms in sentence-start contexts → FLUSH; unseen variants; short
fragments; punctuation/capitalization variations; English equivalents; negative over-join cases) must
establish **false-split reduction − over-join cost** before any guard touches production. Recommended
state: **investigation COMPLETE → matrix COMPLETE → root cause confirmed → production gate unchanged →
phrase-level guard remains a candidate pending broader corpus validation.** Full study + decision:
`docs/implementation/investigations/gemini-segmentation.md`.

**Runtime Gemini-toggle latency verification — PASS (2026-08-12, measurement only; no code change).**
Whisper STT FINAL latency identical with Gemini OFF vs ON (11.8 s vs 11.4 s mean); Gemini fully
detached when translation is OFF (first translation request fired exactly at the runtime toggle).
Full study: `docs/implementation/investigations/latency-study.md`.

**Project core done (close-out 2026-08-06).** Final real-world acceptance PASSED at the production
default (`fasterwhisper-native` + live partials, `UC_NATIVE_THREADS=4`): 300 s real loopback legs,
STT worker ~32–34% of machine, App ~1%, first caption ~3.2 s, 0 orphaned workers, clean exit. Prior
close-outs (Entry 16 `Threads=4`, Entry 15 overlay live-line, Entry 14 default promotion, Slices
10–12, Slice 5/6) are recorded in `docs/implementation/HISTORY.md`.

## Current Implementation Summary

- `UniversalCaptions.Core`: pure contracts — `IAudioCapture`, `IAudioBuffer`, `IAudioProcessor`, `IVoiceActivityDetector`, `AudioFormat`, `AudioChunk`, `AudioCaptureError`; `UniversalCaptions.Core.Translation` — `ITranslationEngine`, `TranslationResult`, `TranslationError`/`TranslationException`, `TranslationErrorKind`; `UniversalCaptions.Core.Captions` — `ICaptionService`, `CaptionLine`, `CaptionState`, `CaptionServiceOptions`.
- `UniversalCaptions.Audio`: `WasapiLoopbackCaptureSource` (NAudio), `LoopbackDeviceEnumerator`, `ByteToFloatConverter`, `PcmRingBuffer`, `SampleRateConverter`, `EnergyVad`, `AudioLevelMeter`.
- `UniversalCaptions.Speech`: `WhisperSpeechToTextEngine` (local Whisper.net) with the decode portion extracted to the `ISTTDecoder` seam — `WhisperCppDecoder` (ggml-base, now the explicit fallback `UC_STT_ENGINE=ggml-base`) and `FasterWhisperDecoder` (windowed `UC_STT_ENGINE=fasterwhisper`; persistent binary-framed Python worker `Server/faster_whisper_worker.py`, model loaded once, `small` int8); **`FasterWhisperNativeStreamingEngine` (production default — one FINAL per VAD-detected speech segment + live partials, 8 s `MaxSegmentDuration` cap frozen, worker protocol unchanged)**; `StreamingTranscriptCommitter` (stability-based finals).
- `UniversalCaptions.Translation`: `ArgosTranslationEngine` (local Argos child process, line-protocol JSON over stdin/stdout), `ArgosTranslationEngineOptions`, bundled `argos_translate_server.py`.
- `UniversalCaptions.Captions`: `CaptionService` — partials replace the active line, finals commit to a bounded sequence-ordered history, optional background translation (failure preserves the source caption), cancellation, state events. **Live active-line translation**: the in-progress line is translated in the target language as the speaker is still talking via a single in-flight slot (Argos cannot be cancelled per partial — the slot serializes and self-replenishes to a newer partial); results are stale-guarded by line-instance identity (`CaptionState.ReplaceActiveLine`) and discarded when the line was superseded/committed or translation was disabled mid-flight. **E2E latency (Slice 6 Phase 1a)**: `CaptionLine.TranslationStartedAtUtc`/`TranslationCompletedAtUtc` stamped by an injectable clock (`utcNow`) — completion only when a result is actually applied (stale/disabled results stamp nothing); `CaptionPipeline.EndToEndLatencyUpdated` emits `EndToEndLatencySample` (Partial/Final) on every published translated caption (E2E = `CapturedAtUtc` → published; translation = request start → published); `LatencyUpdated` (STT-final) unchanged.
- `UniversalCaptions.App`: WPF DI composition root (Slice 5) — `IOverlayService` (visibility/position/opacity/font size/click-through), `CaptionOverlayWindow` (auto-sized translucent pill: white text, target-language badge, expand/collapse chevron for history, hide button; renders `CaptionState`; **paints the live partial stream in a single mutable active block — Entry 15**), `CaptionPipeline` (capture → processor → STT → caption service via `Func` factories; `StatusChanged`/`LatencyUpdated`/`EndToEndLatencyUpdated`), `ControlWindow` (audio source/language, translation on/off + target, status/latency + E2E latency, overlay sliders, Start/Stop, "Show Captions" — Start also re-shows the overlay), `AudioSourceLoader` (device enumeration), `TranslationGuard` (source-equals-target rejection), `App.xaml.cs` (DI registration; STT factory delegates to **`SpeechEngineFactory`** — production default = `FasterWhisperNativeStreamingEngine` + live partials (`UC_NATIVE_PARTIAL_INTERVAL` default 1 s / `UC_NATIVE_PARTIAL_WINDOW` default 4 s, `MaxSegmentDuration` 8 s frozen, **decode threads capped at 4 by default — `UC_NATIVE_THREADS`, clamped [1, ProcessorCount], Entry 16 CPU optimization**); `UC_STT_ENGINE=ggml-base` → the original local-Whisper engine (explicit fallback, `UC_STT_MODEL_PATH` → `artifacts/models/ggml-base.bin`); `UC_STT_ENGINE=fasterwhisper` → the windowed engine; `UC_STT_ENGINE=fasterwhisper-native` → the native path; Python via `UC_FW_PYTHON` → `%TEMP%\fwv\Scripts\python.exe`; windowed-engine knobs `UC_STT_WINDOW`/`UC_STT_INTERVAL`/`UC_STT_STABILITY`). **TD-005 settings persistence (App/Settings):** `UserSettings`/`ISettingsStore`/`SettingsStore` persist the six user-facing categories (audio source, speech language, translation on/off + target, overlay opacity/font/click-through, overlay placement, overlay view state) to `%LocalAppData%\UniversalCaptions\settings.json` (atomic `.tmp`→`File.Move(overwrite)`, unknown fields ignored, missing/malformed→defaults, store lock); `App.xaml.cs` loads before window construction; engine/env knobs (`UC_STT_*`, Argos/Python, model) are never persisted. Q1 display policy: active line = latest partial, **live-translated** into the target language once its translation completes; finals = bounded history; translated text replaces source only when completed.
- `UniversalCaptions.Diagnostics`: console app listing output devices and rendering a live audio meter.
- `UniversalCaptions.Benchmarks`: STT model benchmark (`stt`) + translation benchmark (`translate`). STT mode is parameterized (Slice 6 Phase 1b): `--window`/`--interval`/`--stability`/`--feed realtime|fast`/`--sample <substr>`/`--csv <path>`; records full-file WER, streamed-finals WER (commit-rate proxy, not accuracy), first-partial/final latency, decode/stream CPU, RAM. Slice 10 added the additive **`sttnative`** mode (`NativeStreamingBenchmark.cs`) that drives the real `FasterWhisperNativeStreamingEngine` exactly as the App composes it (`--python`/`--model`/`--language`/`--feed`/`--chunk-ms`/`--min-speech`/`--hangover`/`--max-segment`/`--csv`); Slice 11 added `timeBeginPeriod(1)`/`timeEndPeriod(1)` around the realtime feed (valid ~1.1× controlled pacing) and a **mid-sentence-split metric** (unterminated FINALs + short fragments, in gate table + CSV). OFAT sweep + shortlist in `docs/reports/BENCHMARK_REPORT.md` (Slice 6 section). Slice 9 records the faster-whisper worker round-trip characterization + the **decision-gate measurements** (startup decomposition, real-App first-caption/steady-state latency table, window/interval tuning) in the same file. Slice 11 sweep (max-segment 8/10/12 s) + decision in the same file.
- Tests (372 total, all passing): `UniversalCaptions.Audio.Tests` (77), `UniversalCaptions.Captions.Tests` (72), `UniversalCaptions.Speech.Tests` (109), `UniversalCaptions.Translation.Tests` (27), `UniversalCaptions.App.Tests` (87) — see `docs/reports/TEST_REPORT.md`.
- Docs: bootstrap governance + MVP docs + ADRs 0001–0008 in `docs/`.

## Architecture Summary

Native .NET 8 desktop app. Pipeline: WASAPI loopback (NAudio) → audio processing (buffer, resample, VAD) → streaming `ISpeechToTextEngine` (local Whisper, Slice 2) → optional `ITranslationEngine` (local Argos process, Slice 3) → `ICaptionService` (Slice 4) → WPF overlay (Slice 5). Layered projects in `src/` with dependency rules in `docs/REPOSITORY_STANDARDS.md`. Approved stack and decisions in `docs/adr/`.

## Key Commands

```bash
dotnet build UniversalCaptions.slnx
dotnet test UniversalCaptions.slnx
dotnet run --project src/UniversalCaptions.Diagnostics
dotnet format --verify-no-changes
dotnet list UniversalCaptions.slnx package --vulnerable
```

## Governance

Before making any project decision, read the relevant governance document:

- [docs/PROJECT_CONSTITUTION.md](docs/PROJECT_CONSTITUTION.md) — Immutable project rules and policies (incl. privacy rules)
- [docs/ARTIFACT_REGISTRY.md](docs/ARTIFACT_REGISTRY.md) — Document ownership (every concept has one authoritative source)
- [docs/AGENT_DECISION_POLICY.md](docs/AGENT_DECISION_POLICY.md) — What agents may/must/must not decide
- [docs/REPOSITORY_STANDARDS.md](docs/REPOSITORY_STANDARDS.md) — Folder layout, naming, import rules, dependency boundaries
- [docs/CHANGE_IMPACT_PROCESS.md](docs/CHANGE_IMPACT_PROCESS.md) — Pre-implementation impact analysis and no-silent-assumptions policy

## Engineering Rules

### Product First Rule

Build user-facing value before internal polish. Every slice must produce a visible result (Slice 1: the diagnostic meter proved real capture).

### Single Sprint Rule

Only one slice/sprint may be active (Slice 5 now). Do not start future slice work until the active slice meets its Definition of Done.

### Definition of Done

A feature is complete only when:

- Acceptance criteria are satisfied
- Build passes with 0 warnings (warnings-as-errors)
- Unit and integration (fake-boundary) tests pass
- Manual device verification is recorded where hardware is involved
- Privacy rules are respected (no silent capture, no persistence)
- Code review is complete (self-review + fresh-context review for AI-generated code)
- Documentation is updated (CHANGELOG, PROJECT_STATUS, TEST_REPORT)
- Execution evidence is recorded

A feature must not be marked complete when required validation is **Pending** or **Not Tested**.

### Roadmap Discipline

`docs/implementation/ROADMAP.md` only answers "What should be built?". Keep it limited to Completed, In Progress, Sprint Queue, Future, and Blocked.

### Architecture Rules

Follow `docs/ARCHITECTURE.md` and ADRs 0001–0006. Never put NAudio or WPF code in `UniversalCaptions.Core`. Never bypass the STT or translation abstraction. Do not introduce infrastructure unless the active slice requires it.

### State Management Rules

Separate capture state, caption state, and overlay state (see ARCHITECTURE.md). UI thread never blocks on the audio pipeline.

### Package Boundaries

`Core` is a pure contract layer. `Audio`/`Speech`/`Translation`/`Captions` depend only on Core. `App` depends on all. `Diagnostics` depends on Core + Audio. See `docs/REPOSITORY_STANDARDS.md`.

### Documentation Discipline

Keep each document in its lane: PRD for behavior, scope for boundaries, architecture for design, build plan for execution, roadmap for backlog, changelog for history, project status for now, technical debt for cleanup, release plan for done.

### Testing Rules

`dotnet test` must pass after every change. Hardware boundaries are tested with fakes (`IWaveIn`); real device/model verification is manual and recorded in `TEST_REPORT.md`. Never mark a check **Passed** without execution evidence.

### Code Review Rules

Every meaningful change must pass review. For AI-generated code, run a fresh-context review pass that reports findings before modifying. Evaluate correctness, privacy, error handling, tests, architecture compliance, and documentation.

### Security Rules

Follow `docs/SECURITY_PLAN.md`. Privacy is immutable policy: no silent capture, no raw audio persistence, no microphone capture, local-first STT and translation. Never claim latency or Windows 10 compatibility without measurement.

### Observability Rules

MVP uses in-memory diagnostics + console output. Latency timestamps flow with each `AudioChunk` for later measurement.

### AI Engineering Rules

Follow `docs/AI_ENGINEERING_GUIDELINES.md`. Do not hallucinate APIs, invent business rules, or fake test results. Verify NAudio/.NET APIs against the installed package. Reuse before creating.

### Release Rules

Do not mark a release Ready unless release criteria, quality gates, and blocking issues are reviewed in `docs/implementation/RELEASE_PLAN.md`.

## Known Gaps

- **Overlay live-line residuals (Entry 15 close-out, not regressions):** rolling-4 s-window means the FINAL reveals earlier words; Tagalog `one`-for-`ako` quirks remain (`Ang pangalan ko ay one.`); tl→en Argos unsupported (stanza SBD, ADR-0006) with graceful degradation to source.
- **Argos `tl`-as-source unsupported (stanza SBD)** and `ja→tl` requires a pivot via `en` (~1050 ms/call); MVP pairs use `tl` as a target only (ADR-0006).
- **faster-whisper steady-state latency gap (Slices 8–9 decision-gate closed 2026-08-04):** `UC_STT_ENGINE=fasterwhisper` stays **opt-in** — steady-state STT latency (13.7–15.8 s vs ggml-base 2.4–3.7 s) is a live-caption responsiveness regression; Tagalog accuracy gap on the `ggml-base` default remains acknowledged as open. (Production default is `fasterwhisper-native`, which is a different, promoted path.)
- **Argos dev venv lives outside the repo** (`MAX_PATH` limit, TD-011; current dev venv at `C:\Users\TOGODB~1\AppData\Local\Temp\argosv`, argostranslate 1.11.0); this machine has no argostranslate on system Python, so translation defaults Off unless the venv `Scripts` dir is prepended to PATH.
- **Device-change notifications (TD-002):** contract + production wiring complete (2026-08-05) (`WasapiDeviceChangeNotifier` + `DefaultDeviceAutoRecovery`), **frozen/Open pending the real hotplug acceptance test** (Run 1/2/3 → evidence → PASS/FAIL) once a second device is available.
- **Phase 2 real-app validation (YouTube/Chrome, VLC, Zoom) is deferred per user** — a future reassessment pass over the baseline defaults.
- **ADR-0007 stays Proposed** until the original operator Tagalog recording is available.

## Technical Debt

See `docs/implementation/TECHNICAL_DEBT.md`. **TD-001 closed (2026-08-05)** — resampler benchmark (keep sinc). TD-002 device-change notifications (**frozen/Open** pending real hotplug test; contract + production wiring complete), TD-003 DI composition, TD-004 coverage tooling, **TD-005 closed (2026-08-05) — settings persistence** (`UserSettings`/`ISettingsStore`/`SettingsStore`, 6 `SettingsStoreTests`, full suite 357/357), TD-006 committer word-boundary edge, TD-007 immutable-final revision, TD-008 STT backpressure, TD-009 benchmark harness, TD-010 Argos `tl`-source/pivot latency, TD-011 Argos venv MAX_PATH, TD-012 Argos identical-input caching, TD-013 LineProtocolArgosProcess direct tests, TD-014 Dispose races `_gate`, TD-015 unbounded stderr, **TD-016 closed (2026-08-04) — faster-whisper protocol-contract suite** (`LineProtocolFasterWhisperProcessProtocolTests`, 9 tests, injectable-stream seam).

## Next Priority

**Corpus-driven phrase-guard validation — CLOSED (2026-08-14; decision: INSUFFICIENT EVIDENCE — do
not ship).** `PhraseGuardCorpusValidationTests.cs` (11 tests, 43-case corpus) measured
**false-split reduction − over-join cost** per candidate against the real engine gate: every Tagalog
guard nets positive (`At pagkatapos` +4 best) while the bare `at|kaya|sige|hindi` allowlist over-joins
8; English equivalents net 0; two irreducible same-surface ambiguities proven. **Decision: do not
ship** — the real-world over-join cost is unknown (the deciding unknown); the over-join cases are
constructed, not frequency-measured. Production gate unchanged; no v0.5.41; the 49 matrix tests stay
unchanged; full suite **711/711**, Release 0 warnings/0 errors, `dotnet format` clean. **The only
thing that would justify Ship: a naturally occurring annotated corpus** measuring per candidate
`false-splits-prevented / applicable continuation boundaries` and `false-joins / applicable sentence
boundaries`, frequency-weighted. **Do not keep expanding the lexical phrase list until that frequency
question is answered.** Full results + decision:
`docs/implementation/investigations/phrase-guard-validation.md`. Remaining work is feature-level/
product-level, not core architecture: **Phase 2 real-app validation (YouTube/Chrome, VLC, Zoom) stays
deferred per user**; ADR-0007 stays **Proposed** until the original operator Tagalog recording is
available; TD-002 stays **frozen/Open** until the real hotplug acceptance test can be run. The
production default (`fasterwhisper-native` + live partials, `UC_NATIVE_THREADS=4`) is the validated,
frozen configuration.

Historical close-outs (Entry 16 `Threads=4`, Entry 15 overlay live-line, Entry 14 default promotion, Slices 10-12, Slice 5/6, TD-001/005/016) are recorded in `docs/implementation/HISTORY.md`.
