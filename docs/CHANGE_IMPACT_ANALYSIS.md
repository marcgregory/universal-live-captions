# Universal Live Captions Change Impact Analysis

Last updated: 2026-08-01

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Record the impact analysis performed before each change (per [CHANGE_IMPACT_PROCESS.md](CHANGE_IMPACT_PROCESS.md)) |
| Scope | Every feature, fix, refactor, or infrastructure change |
| Audience | Engineering, reviewers |
| Owner | Engineering |
| Status | Active |

---

## Entry 2 — Slice 2: Speech-to-Text Spike (`ISpeechToTextEngine` + Fake + Whisper)

Date: 2026-07-31

### 1. Change Summary

```text
Change Title: Slice 2 — STT spike (ISpeechToTextEngine, FakeSpeechToTextEngine, WhisperSpeechToTextEngine, benchmark)
Change Type:        Feature
Requirement Source: BUILD_PLAN.md Slice 2; ADR-0003; PRD speech requirements
Priority:           High
Estimated Effort:   Medium (abstraction + fake + tests), then Whisper integration + benchmark (environment-dependent)
```

### 2. Affected Modules

- `UniversalCaptions.Core` — new `UniversalCaptions.Core.Speech` contracts: `ISpeechToTextEngine`, `SpeechTranscript`, `PartialTranscript`, `FinalTranscript`, `SpeechRecognitionError`, `SpeechRecognitionErrorKind`, `SpeechRecognitionException`.
- `UniversalCaptions.Speech` (new project) — `WhisperSpeechToTextEngine` (whisper.cpp binding).
- `UniversalCaptions.Speech.Tests` (new project) — STT unit tests with a fake engine.
- `UniversalCaptions.slnx` — add the new projects.
- `UniversalCaptions.Benchmarks` (new console project) — Whisper model benchmark harness (Slice 2 benchmark deliverable). Diagnostics cannot host it: it may only reference Core + Audio per REPOSITORY_STANDARDS.

### 3. Affected APIs

- New: `ISpeechToTextEngine`, transcript types, `SpeechRecognitionError`, `SpeechRecognitionException`.
- **API changes required:** Additive (new contracts in Core; no existing API modified).

### 4. Database Changes

Not applicable — no database.

### 5. Security and Privacy Implications

- [x] Capture behavior change — no (reuses Slice 1 capture pipeline).
- [x] Audio/transcript handling change — yes: captured audio now flows into a local STT engine. Privacy is preserved: Whisper runs locally on-device; no audio or transcript leaves the machine (PRD/SECURITY_PLAN privacy model). Models are downloaded once under `artifacts/models/` (git-ignored).
- [ ] New external communication — none at runtime; model files are downloaded during dev/setup only.
- [ ] Sensitive data handling — transcripts remain in-memory; no persistence.
- [x] Security review required: No (local-only processing; no new network path).

### 6. Test Updates Required

- [x] Unit tests — `UniversalCaptions.Speech.Tests`: partials, finals, cancellation, errors, ordering, continuous chunks, start/stop.
- [ ] Integration tests — none automated; real-model verification is manual (recorded in TEST_REPORT).
- [x] Manual/device verification — Whisper benchmark on this Windows 10 machine (recorded).

### 7. Documentation Updates Required

- [x] `ARCHITECTURE.md` — reflect that `ISpeechToTextEngine` and transcript contracts live in `UniversalCaptions.Core` (per ADR-0005/REPOSITORY_STANDARDS dependency table), Speech owns engines.
- [x] `TECH_STACK.md` — record the selected whisper.cpp .NET binding package.
- [x] ADR required: No new ADR for the abstraction (ADR-0003 exists); ADR-0003 wording updated to note contracts live in Core. New ADR not needed for the benchmark (decision recorded in ADR-0003 + this analysis). The Whisper binding choice is a Level 3 decision (flagged).
- [x] `CHANGELOG.md`, `PROJECT_STATUS.md`, `TEST_REPORT.md`, `BUILD_PLAN.md` (Slice 2 progress).

### 8. Dependencies and Risks

- [ ] Blocked by: network access to NuGet (binding package) and Hugging Face (ggml models) for the Whisper integration phase.
- [ ] Blocking: nothing downstream blocks Slice 2; Slice 3 (translation) depends on Slice 2 completing.
- [ ] Risks identified: (1) binding package/native-runtime availability on Windows x64; (2) ggml model download size/time; (3) streaming partial transcripts depend on the binding's streaming capability — fallback is windowed chunk transcription; (4) model benchmark runtimes on CPU may be slow for larger models.
- [x] Mitigation plan: abstraction + fake + tests land first (deterministic and complete regardless of Whisper integration). Whisper integration is verified in a scratch harness before committing to the binding. Benchmark uses a short reference audio clip; model set tiny/base/small.

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 | `ISpeechToTextEngine`, `PartialTranscript`, `FinalTranscript` live in `UniversalCaptions.Core` (not `UniversalCaptions.Speech` as ADR-0003 literally states) so `UniversalCaptions.Captions` (Slice 4) can consume transcripts while referencing only Core. | Captions would violate the dependency table in REPOSITORY_STANDARDS (blocking review finding). | ADR-0005 (boundary interfaces in Core); REPOSITORY_STANDARDS dependency table; ADR-0003 says "Speech" but ARCHITECTURE/ADR-0005 are consistent with Core. |
| 2 | The Whisper .NET binding and native runtimes are available on NuGet for win-x64 and can be verified (not hallucinated). | Would switch to a whisper.cpp CLI subprocess or recorded blocker. | AI_ENGINEERING_GUIDELINES (verify, don't hallucinate). |
| 3 | Whisper model files (ggml-tiny/base/small) are downloadable from the ggerganov whisper.cpp Hugging Face repo into `artifacts/models/`. | Benchmark deferred; recorded as gap. | TECH_STACK package recommendations. |
| 4 | A new `UniversalCaptions.Benchmarks` console project under `src/` is acceptable for the benchmark deliverable. | Would move the harness to a temp scratch (less reproducible). | REPOSITORY_STANDARDS (src/ holds app projects; Diagnostics dependency rule blocks hosting it). |
| 5 | The fake engine is implemented in the test project (`Support/`), matching the `FakeWaveIn` precedent; Slice 4 can reuse it via a shared test support project when needed. | Minor duplication in Slice 4. | ADR-0005 (fakes at boundaries in tests); REPOSITORY_STANDARDS. |

### 10. Open Questions

| # | Question | Asked Of | Status |
|---|---|---|---|
| 1 | Is creating `src/UniversalCaptions.Benchmarks` acceptable? | User | Flagged (Assumption 4) |

---

## Entry 3 — Slice 2: Stability-Based Streaming Commit (Partial → Stable → Final)

Date: 2026-07-31

### 1. Change Summary

```text
Change Title: Slice 2 — streaming commit tuning: stability-based finals (partial → stable → final)
Change Type:        Feature / Fix
Requirement Source: BENCHMARK_REPORT.md follow-up 2 (no finals in streaming); user request (final-segment
                    commit tuning with deterministic tests)
Priority:           High
Estimated Effort:   Medium (committer rewrite + engine windowing + tests + benchmark)
```

### 2. Affected Modules

- `UniversalCaptions.Speech` — `StreamingTranscriptCommitter` (rewritten around a stability window), `WhisperSpeechToTextEngine` (growing-window epoch loop + trim-to-committed), `WhisperEngineOptions` (new `StabilityWindow`, `MaxSegmentLength`, `SplitOnWord`).
- `UniversalCaptions.Speech.Tests` — `StreamingTranscriptCommitterTests` (10) and `WhisperSpeechToTextEngineTests` (13, incl. decode→Stop→DisposeAsync regression).
- `UniversalCaptions.Benchmarks` — harness rewritten for multiple samples (`jfk`, noisy, long, conversational OSR) and tiny/base candidates.

### 3. Affected APIs

- **API changes required:** Additive + internal. Public surface: new options (`StabilityWindow`, `MaxSegmentLength`, `SplitOnWord`). `StreamingTranscriptCommitter` is internal; its `Update` signature changed from `(segments, windowStartUtc, windowEndUtc, commitOverlap)` to `(segments, windowStartUtc)` plus a stability-window constructor parameter. `WhisperEngineOptions.CommitOverlap` meaning changed: it now guarantees tail audio is never trimmed.

### 4. Database Changes

Not applicable — no database.

### 5. Security and Privacy Implications

- [x] Capture behavior change — no.
- [x] Audio/transcript handling change — no new data flow; commit logic is in-memory. Privacy preserved (local Whisper, in-memory only, no persistence).
- [ ] New external communication — none at runtime (benchmark samples download during dev only).
- [ ] Sensitive data handling — none (in-memory transcripts).
- [x] Security review required: No (internal algorithm change; no network/privacy impact).

### 6. Test Updates Required

- [x] Unit tests — committer (10) + engine (13) rewritten/added; deterministic fake decoder; DisposeAsync-during-decode regression.
- [ ] Integration tests — none automated; real-model verification manual (recorded in TEST_REPORT/BENCHMARK_REPORT).
- [x] Manual/device verification — multi-sample real-model benchmark on this Windows 10 machine (recorded).

### 7. Documentation Updates Required

- [x] `BENCHMARK_REPORT.md` — new harness, 4 samples, streaming finals evidence, model quality ranking (OSR sample).
- [x] `ADR-0003.md` — replace stale notes (no finals; sample does not discriminate) with new evidence; model default flagged for user confirmation.
- [x] `CHANGELOG.md`, `PROJECT_STATUS.md`, `TEST_REPORT.md`, `BUILD_PLAN.md` (Slice 2 progress).
- [ ] ADR required: No new ADR — algorithm detail, recorded here and in the committer XML docs.

### 8. Dependencies and Risks

- [ ] Blocked by: none.
- [ ] Blocking: Slice 4 (caption service) depends on streaming finals; this change unblocks it.
- [ ] Risks identified: (1) premature commit if stability window too small — mitigated with `StabilityWindow >= 2` guard and word-boundary common-prefix backing off; (2) epoch resets could re-emit text already committed — mitigated by trimming only past `CommittedUntilUtc` and `Reset()` on start; (3) `SplitOnWord`/`WithMaxSegmentLength` behavior unknown — kept opt-in, default off, flagged for benchmark.
- [x] Mitigation plan: deterministic tests first (committer then engine), then real-model benchmark across clean/noisy/long/conversational samples; regression test locks the decode→Stop→DisposeAsync path.

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 | Committing text that is byte-stable across `StabilityWindow` consecutive decodes is the right heuristic for lockable captions; word-boundary alignment prevents committing a cut word. | Wrong heuristic could commit too early (correctable by raising the window) or too late (raised latency). | User request (stability-based finals); whisper streaming behavior observed in the benchmark. |
| 2 | Trimmed audio starts a fresh epoch (new `windowStartUtc`), which resets stability so nothing committed before the trim is re-emitted after it. | A stale epoch could re-commit old text. | Engine run-loop behavior; covered by `Restart_ResetsCommitState`/`ChangingPartials_DoNotPrematurelyCommit` tests. |
| 3 | `StabilityWindow` default 3 balances latency (~2–4 s of confirmations) vs. premature-commit risk on real streaming. | Tune later with real-device data; option is public. | Default-values decision (Level 3, flagged). |
| 4 | OSR_us_000_0010 is a suitable discriminating (conversational/continuous speech) sample; ggml-small full-file decode is an acceptable pseudo-reference for WER on it. | WER ranking could differ on other content; flagged as benchmark-sample choice. | Benchmark sample selection (Level 3, flagged). |

### 10. Open Questions

| # | Question | Asked Of | Status |
|---|---|---|---|
| 1 | Which model should be the Slice 4 default now that OSR evidence ranks base (4.9% WER) above tiny (16.0%) at a latency cost (first final 10.0 s vs 5.8 s)? | User | **Answered 2026-07-31 — default ggml-base; tiny as low-resource fallback (ADR-0003)** |

---

## Entry 4 — Slice 3: Translation Spike (`ITranslationEngine` + Fake + Argos)

Date: 2026-07-31

### 1. Change Summary

```text
Change Title: Slice 3 — translation spike (ITranslationEngine, FakeTranslationEngine, ArgosTranslationEngine, benchmark)
Change Type:        Feature
Requirement Source: BUILD_PLAN.md Slice 3; ADR-0006; PRD translation requirements; user instruction
Priority:           High
Estimated Effort:   Medium-High (contract + fake + tests, then Argos process integration + env setup + benchmark)
```

### 2. Affected Modules

- `UniversalCaptions.Core` — new `UniversalCaptions.Core.Translation` contracts: `ITranslationEngine`, `TranslationResult`, `TranslationErrorKind`, `TranslationException`.
- `UniversalCaptions.Translation` (new project) — `ArgosTranslationEngine`, internal Argos process seam + line-protocol client, bundled Python server script.
- `UniversalCaptions.Translation.Tests` (new project) — translation unit tests with `FakeTranslationEngine` (in test Support) and a fake process client seam for the Argos engine.
- `UniversalCaptions.Benchmarks` — translation benchmark mode (load time, latency, throughput, memory, quality, continuous finals).
- `UniversalCaptions.slnx` — add the new projects.
- Dev environment — dedicated Python venv + `argostranslate` + language packages under `artifacts/argos/` (git-ignored).

### 3. Affected APIs

- New: `ITranslationEngine`, `TranslationResult`, `TranslationErrorKind`, `TranslationException`.
- **API changes required:** Additive (new contracts in Core; no existing API modified). ADR-0006 corrected: contracts live in `UniversalCaptions.Core.Translation` (mirrors ADR-0003 refinement), `UniversalCaptions.Translation` owns only the Argos adapter.

### 4. Database Changes

Not applicable — no database.

### 5. Security and Privacy Implications

- [x] Capture behavior change — no.
- [x] Audio/transcript handling change — yes: final transcripts now flow to a local translation engine. Privacy preserved: Argos runs locally/offline; **no transcript leaves the machine**; raw audio is not sent to translation (only final transcript text, in memory). Transcripts remain in memory; no persistence.
- [ ] New external communication — none at runtime; Argos language packages are downloaded once during dev/setup into `artifacts/argos/` (git-ignored).
- [ ] Sensitive data handling — translated transcripts remain in memory; no persistence.
- [x] Security review required: No (local-only; no new runtime network path; Python process is a spawned child, stdin/stdout only).

### 6. Test Updates Required

- [x] Unit tests — `UniversalCaptions.Translation.Tests`: contract + `FakeTranslationEngine` (success, empty input, cancellation, failure, pair validation, ordering) and `ArgosTranslationEngine` via a fake process seam (protocol, malformed responses, cancellation, error mapping, timeout).
- [ ] Integration tests — real Argos verification is manual (recorded in TEST_REPORT/BENCHMARK_REPORT).
- [x] Manual/device verification — real Argos offline translation + language-pair verification on this machine (recorded).

### 7. Documentation Updates Required

- [x] `ADR-0006.md` — correct contract location to `UniversalCaptions.Core.Translation` + rationale (domain contracts must not live in an implementation project).
- [x] `ARCHITECTURE.md` — translation package boundary reflects contracts in Core; Captions stays Core-only.
- [x] `REPOSITORY_STANDARDS.md` — add Translation + Translation.Tests to layout + dependency table.
- [x] `TECH_STACK.md` — Argos runtime: Python venv, `argostranslate`, language packages under `artifacts/argos/`.
- [x] `CHANGELOG.md`, `PROJECT_STATUS.md`, `TEST_REPORT.md`, `BUILD_PLAN.md`, `ROADMAP.md` (Slice 3 progress).
- [x] ADR required: No new ADR — ADR-0006 exists and is corrected in place (mirrors ADR-0003 refinement precedent).

### 8. Dependencies and Risks

- [x] Blocked by: network access to PyPI (argostranslate) and Argos OPUS model index (language packages) for the integration phase.
- [ ] Blocking: nothing downstream blocks Slice 3; Slice 4 (caption service) depends on the translation contract + fake + finals feed.
- [ ] Risks identified: (1) Argos on Windows/Python 3.11 wheel availability (ctranslate2/sentencepiece); (2) language-package download size/time; (3) direct pair availability — en→tl, ja→en, en→ja may need pivoting; (4) process startup + model load latency; (5) child-process lifecycle/cancellation robustness.
- [x] Mitigation plan: contract + fake + tests land first (deterministic, Python-free). Argos is verified in a dedicated venv + bundled server script before wiring the engine. Process seam allows deterministic engine tests without Python. Privacy: local-only, stdin/stdout protocol, no network at runtime.

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 | `ITranslationEngine`/`TranslationResult`/translation errors live in `UniversalCaptions.Core.Translation` so Captions (Slice 4) references only Core; `UniversalCaptions.Translation` owns the Argos adapter only. | Captions would couple to the Argos project; standards/architecture would need reverting. | User decision (2026-07-31); ADR-0003 refinement precedent; ARCHITECTURE Captions package boundary. |
| 2 | Argos is run as a child process over a newline-delimited JSON line protocol on stdin/stdout (per ADR-0006); the C# app has no Python dependency. | Alternative protocol/service needed; re-verified against actual Argos CLI/API behavior. | ADR-0006; user scope (local process). |
| 3 | Language codes are ISO 639-1 (`en`, `ja`, `tl`); Argos package codes match (`en`, `ja`, `tl`). | Code mapping needed if Argos uses different codes. | Argos package convention; verify at spike. |
| 4 | Direct models exist for en→tl, ja→en, en→ja in the Argos index; pivoting is a fallback, not the norm for the MVP pairs. | Recorded as pivot usage in benchmark; pair selection is a Level 3 flag. | Argos OPUS model index; verified at spike. |
| 5 | Python 3.11 + `argostranslate` install into a dedicated venv under `artifacts/argos/` works on Windows 10. | Would try a different Python version or document a blocker. | AI_ENGINEERING_GUIDELINES (verify, don't hallucinate). |

### 10. Open Questions

| # | Question | Asked Of | Status |
|---|---|---|---|
| 1 | Translation contract location (Core vs Translation project)? | User | **Answered 2026-07-31 — `UniversalCaptions.Core.Translation` (ADR-0006 corrected)** |
| 2 | Argos language-pair/pivot decisions and default pairs? | User | Deferred to benchmark (ADR-0006), flagged at checkpoint |

---

---

## Entry 5 — Slice 4: Caption Service (`ICaptionService` + `CaptionLine` + `CaptionState`)

Date: 2026-08-01

### 1. Change Summary

```text
Change Title: Slice 4 — caption service (ICaptionService, CaptionLine, CaptionState, CaptionService + fake/unit tests)
Change Type:        Feature
Requirement Source: BUILD_PLAN.md Slice 4; PRD FR-5/FR-14; user instruction (Slice 4 kick-off)
Priority:           High
Estimated Effort:   Medium (contracts in Core + CaptionService + deterministic tests; no WPF)
```

### 2. Affected Modules

- `UniversalCaptions.Core` — new `UniversalCaptions.Core.Captions` contracts: `ICaptionService`, `CaptionLine`, `CaptionState`, `CaptionServiceOptions`.
- `UniversalCaptions.Captions` (new project) — `CaptionService` implementation (UI-independent, no WPF concepts).
- `UniversalCaptions.Captions.Tests` (new project) — deterministic caption tests with a fake STT/translation boundary.
- `UniversalCaptions.slnx` — add the new projects.
- `UniversalCaptions.Core.Speech`/`Core.Translation` — caption service consumes `FinalTranscript` (finals) and `ITranslationEngine`; no changes to these existing contracts.

### 3. Affected APIs

- New: `ICaptionService`, `CaptionLine`, `CaptionState`, `CaptionServiceOptions` in `UniversalCaptions.Core.Captions`; `CaptionService` in `UniversalCaptions.Captions`.
- **API changes required:** Additive (new contracts in Core; no existing API modified). Mirrors ADR-0003/ADR-0006 precedent: caption contracts live in Core so the future WPF `App` depends only on Core + `Captions`.

### 4. Database Changes

Not applicable — no database.

### 5. Security and Privacy Implications

- [x] Capture behavior change — no.
- [x] Audio/transcript handling change — no: captions are derived from in-memory finals; only final transcript text flows to translation (existing Slice 3 behavior); no new persistence.
- [ ] New external communication — none (translation remains the local Argos process from Slice 3).
- [ ] Sensitive data handling — caption text stays in memory; no persistence.
- [x] Security review required: No (pure in-memory state; no new data flow or network path).

### 6. Test Updates Required

- [x] Unit tests — `UniversalCaptions.Captions.Tests`: partial→active→final→committed transitions, ordering (monotonic sequence), duplicate prevention, bounded history, translation on/off, translation failure preserves source caption, session start/stop/reset, cancellation, no WPF dependency (compile-time: Captions references only Core).
- [ ] Integration tests — none automated; real Whisper→caption wiring is manual (recorded in TEST_REPORT, Slice 5/6 end-to-end).
- [ ] Manual/device verification — none for this slice (no hardware boundary).

### 7. Documentation Updates Required

- [x] `ARCHITECTURE.md` — reflect caption contracts in `UniversalCaptions.Core.Captions` + `CaptionService` in `UniversalCaptions.Captions`.
- [x] `REPOSITORY_STANDARDS.md` — add Captions + Captions.Tests to layout + dependency table.
- [x] `CHANGELOG.md`, `PROJECT_STATUS.md`, `TEST_REPORT.md`, `BUILD_PLAN.md`, `ROADMAP.md` (Slice 4 progress).
- [x] ADR required: No new ADR — contract-in-Core follows the ADR-0003/ADR-0006 precedent; no new decision beyond applying it. If a genuinely new caption-model decision emerges (e.g., source-vs-translated display policy), an ADR would be added.

### 8. Dependencies and Risks

- [ ] Blocked by: none (Slice 3 finals feed + translation contract are in place).
- [ ] Blocking: Slice 5 (WPF overlay) consumes `CaptionState`/`ICaptionService`.
- [ ] Risks identified: (1) caption model complexity around source-vs-translated display when translation is toggled mid-session (PRD FR-14); (2) semantics of "committed caption" vs the committer's incremental finals (the caption service treats each committed final as one `CaptionLine`, consistent with the Whisper finals stream); (3) duplicate/out-of-order finals from overlapping windows.
- [x] Mitigation plan: contracts + deterministic tests first (no WPF); the service consumes the existing finals feed and optional `ITranslationEngine`; translation failure keeps the source caption (test-locked). Keep the model minimal: one active line + bounded committed history, mirroring PRD FR-5.

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 | Caption contracts (`ICaptionService`, `CaptionLine`, `CaptionState`) live in `UniversalCaptions.Core.Captions` so `UniversalCaptions.Captions` (and the future `App`) reference only Core — mirroring the ADR-0003/ADR-0006 contract-in-Core refinement. | Captions/App would couple to an implementation project; standards/architecture would need revisiting. | ADR-0003/ADR-0006 precedent; REPOSITORY_STANDARDS dependency table; user slice-4 spec (Core → Captions → WPF). |
| 2 | Each committed Whisper final becomes one `CaptionLine`; the committer's incremental finals stream already emits stable, non-overlapping text, so no intra-final reconciliation is needed. | Would need a merge/re-anchor policy (see TD-007, deferred to Slice 4 review). | Slice 2 committer behavior + Slice 3 finals-stream benchmark. |
| 3 | Translation failure leaves the source caption visible with a flag/error surfaced on the line, rather than clearing it. | Overlay shows stale/garbled text instead of graceful degradation. | User slice-4 spec ("translation failure doesn't destroy the original transcript"); PRD resilience. |
| 4 | Caption history is bounded (configurable max line count) and stores completed captions only; the active in-progress line is tracked separately. | Unbounded memory on long sessions. | PRD FR-5; user slice-4 spec (bounded history, active line). |
| 5 | Translation on/off is a service-level switch (`ITranslationEngine` is optional); when disabled, `CaptionLine.TranslatedText` is null and the source text is used. | Overlay logic wrong when toggling. | PRD FR-14 (toggle mid-session); user slice-4 spec (translation on/off path). |
| 6 | The caption service is fully synchronous on the state-modeling path (partial/final in, `CaptionState` out) and owns an optional background translation task; it does not block on translation. | UI would stall on translation latency (~56–1050 ms/pair). | Architecture state rules (UI thread never blocks on pipeline); Slice 3 benchmark latency. |

### 10. Open Questions

| # | Question | Asked Of | Status |
|---|---|---|---|
| 1 | Should the active caption be displayed verbatim from the latest partial, or should it also track the committed prefix so a "stable + partial" combined line is shown? | User | Deferred to Slice 5 overlay design; Slice 4 keeps active line = latest partial text |

### 11. Close-Out Record

- **Status:** Implemented and closed 2026-08-01 (close-out approved).
- **Evidence:** `UniversalCaptions.Core.Captions` contracts + `UniversalCaptions.Captions.CaptionService`; 40 Captions tests (16 `CaptionState` + 24 `CaptionService` with deterministic `StubTranslationEngine`/`GatedTranslationEngine`); total **168/168** tests; build 0 warnings/0 errors; `dotnet format --verify-no-changes` clean; no vulnerable packages; fresh-context review findings fixed (snapshot history, deferred CTS disposal, atomic translation-start token, stale-translation identity guard, event-raising outside the translation catch, target normalization). See `BUILD_PLAN.md` (Slice 4 Evidence), `TEST_REPORT.md`, `CHANGELOG.md` v0.4.0.

---

## Entry 6 — Slice 5: WPF Overlay + Control Window (`UniversalCaptions.App`)

Date: 2026-08-01

### 1. Change Summary

```text
Change Title: Slice 5 — WPF caption overlay + control window (UniversalCaptions.App; IOverlayService; render CaptionState; consume ICaptionService events on the dispatcher)
Change Type:        Feature
Requirement Source: BUILD_PLAN.md Slice 5; PRD FR-6/FR-7/FR-8/FR-9/FR-10/FR-14; ADR-0004; user instruction (Slice 5 kick-off)
Priority:           High
Estimated Effort:   High (WPF project + overlay/control windows + DI composition + wiring + deterministic tests + manual device verification)
```

### 2. Affected Modules

- `UniversalCaptions.App` (new WPF project, `net8.0-windows`, `UseWPF`) — the composition root. Depends on Core, Audio, Speech, Translation, Captions (per REPOSITORY_STANDARDS dependency table).
- `UniversalCaptions.App.Tests` (new test project) — deterministic tests for the testable, non-WPF logic in the App (overlay display policy, wiring/controller logic via fakes). WPF visuals are verified manually (ADR-0004).
- `UniversalCaptions.slnx` — add the two new projects.
- No changes to existing contracts or implementations in Core/Audio/Speech/Translation/Captions (per user instruction: do not move existing contracts/implementations).

### 3. Affected APIs

- New: `UniversalCaptions.App` — `IOverlayService` (owns overlay visibility, position, opacity, font size, click-through per ADR-0004), WPF overlay window rendering `CaptionState`, control window, DI composition root, and the app-side wiring that connects capture → processor → STT → caption service → overlay.
- The App consumes existing public APIs only: `IAudioCapture`/`WasapiLoopbackCaptureSource`, `AudioProcessor`, `ISpeechToTextEngine`/`WhisperSpeechToTextEngine`, `ITranslationEngine`/`ArgosTranslationEngine`, `ICaptionService`/`CaptionService`, `CaptionState`/`CaptionLine`.
- **API changes required:** Additive (new project; no existing API modified).

### 4. Database Changes

Not applicable — no database.

### 5. Security and Privacy Implications

- [x] Capture behavior change — no new capture path: the App reuses the Slice 1 WASAPI loopback source. No microphone capture (privacy-policy immutable).
- [x] Audio/transcript handling change — no: the App only renders in-memory `CaptionState`; it never persists audio or transcripts. Whisper + Argos remain local/offline.
- [ ] New external communication — none at runtime.
- [ ] Sensitive data handling — none (in-memory only; no persistence).
- [x] Security review required: No (local-only rendering; existing pipeline; no new network or persistence path). One review note: the overlay is always-on-top and may intercept clicks unless click-through is enabled — click-through is opt-in per ADR-0004 (privacy-adjacent UX, not security).

### 6. Test Updates Required

- [x] Unit tests — `UniversalCaptions.App.Tests`: overlay display policy (Q1, below), active-vs-history rendering, translated-text selection (completed translation replaces source; failure keeps source), wiring/controller logic against fakes (capture→processor→STT→caption service), start/stop lifecycle, error surfacing. WPF windows themselves are verified manually (ADR-0004).
- [ ] Integration tests — none automated; real Whisper → caption → overlay wiring is manual (recorded in TEST_REPORT, Slice 5/6 end-to-end).
- [x] Manual/device verification — overlay + control window run on this Windows 10 machine with real system audio (recorded in TEST_REPORT).

### 7. Documentation Updates Required

- [x] `ARCHITECTURE.md` — document the App composition root + IOverlayService + overlay/control window boundaries (already anticipated: overlay consumes CaptionState; App is WPF-only).
- [x] `REPOSITORY_STANDARDS.md` — add App + App.Tests to layout + dependency table (rows already drafted).
- [x] `TECH_STACK.md` — note the WPF project targets `net8.0-windows` (other projects `net8.0`), DI composition root, `IOverlayService`.
- [x] `DEPLOYMENT.md` — run command (`dotnet run --project src/UniversalCaptions.App`); remove the stale "App project exists from Slice 4" note.
- [x] `CHANGELOG.md`, `PROJECT_STATUS.md`, `TEST_REPORT.md`, `BUILD_PLAN.md`, `ROADMAP.md`, `CLAUDE.md` (Slice 5 progress + display-policy resolution).
- [x] ADR required: No new ADR — ADR-0004 already approves the overlay + `IOverlayService` + separate control window; this entry records the Q1 display-policy resolution that was deferred to the overlay design.

### 8. Dependencies and Risks

- [ ] Blocked by: none (Slices 1–4 provide the full pipeline to wire).
- [ ] Blocking: Slice 6 (end-to-end latency/accuracy) depends on the App wiring being real.
- [ ] Risks identified: (1) WPF transparency/click-through + always-on-top behavior on Windows 10 (mitigated by ADR-0004 decision + manual verification); (2) per-monitor DPI positioning of the overlay (PerMonitorV2 per ADR-0004); (3) UI thread never blocking on the audio pipeline — events marshalled to the dispatcher; (4) real Argos wiring runs only when translation is enabled and the dev Argos venv is present (this machine currently has no argostranslate on system Python — translation stays Off by default and is verified manually when the venv is available; unit tests use fakes); (5) Whisper model path resolution (env `UC_STT_MODEL_PATH` / default `artifacts/models/ggml-base.bin`).
- [x] Mitigation plan: deterministic tests for display/wiring logic first (no WPF runtime needed); WPF visuals verified manually; DI composition root keeps WPF code thin; no new infrastructure beyond the ADR-0004-approved overlay.

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 | The App project targets `net8.0-windows` with `UseWPF` while all other projects stay `net8.0`; App depends on all of Core/Audio/Speech/Translation/Captions. | A different TFM/reference set would violate REPOSITORY_STANDARDS or fail WPF compile. | TECH_STACK (net8.0-windows for App); REPOSITORY_STANDARDS dependency table. |
| 2 | `IOverlayService` lives in the App project (WPF-owned) and owns visibility/position/opacity/font-size/click-through; it renders `CaptionState` but never mutates caption state. | Divergent overlay state ownership (e.g., App bypassing IOverlayService) would break ADR-0004's separation. | ADR-0004; ARCHITECTURE overlay state. |
| 3 | **Q1 display policy (RESOLVED):** the overlay renders the active caption verbatim from the latest partial (`CaptionState.ActiveLine`), and committed finals as bounded history. A "committed prefix + partial" combined line is not rendered because `CaptionService`/`CaptionState` do not expose committed-prefix state — the model is partial→active, final→committed (PRD FR-5). Translated text replaces the source on a committed line when translation completes (`CaptionLine.TranslatedText`), and the source remains when translation is off/failed/pending (PRD FR-14; Slice 4 failure-preservation rule). | Overlay would show a combined line the state model does not produce, or mis-display translation state. | PRD FR-5/FR-14; `CaptionState` (ActiveLine + History only); Slice 4 close-out (active line = latest partial text). |
| 4 | The control window keeps the MVP mockup (audio source, language, translation on/off + target, start/stop, status) without over-building settings (PRD FR-8; "don't overbuild settings"). Overlay settings (opacity, font size, position, click-through) are applied to the overlay per PRD FR-7; MVP keeps them in-process (TD-005: no file persistence yet). | A full settings UI would exceed Slice 5 scope and add TD-005 work prematurely. | PRD FR-8; user Slice 5B mockup; ADR-0004. |
| 5 | WPF event handlers only call `ICaptionService`/`ISpeechToTextEngine`/`IAudioCapture` contracts and marshal pipeline events to the dispatcher; the App never calls Whisper/Argos/NAudio internals directly from UI code. | UI would couple to engine internals and violate the architecture boundary (WPF consumes CaptionState only). | User Slice 5 instruction; ARCHITECTURE application boundaries. |
| 6 | The App is the DI composition root (Microsoft.Extensions.DependencyInjection, TD-003): it constructs the real pipeline once and wires events; unit tests construct the wiring against fakes. | Duplicated ad-hoc wiring in UI would be untestable and spread engine references. | TECH_STACK DI; TD-003; user instruction (composition root). |

### 10. Open Questions

| # | Question | Asked Of | Status |
|---|---|---|---|
| 1 | Active-caption display policy: verbatim partial vs committed prefix + partial (deferred from Entry 5 Q1)? | User | **Resolved 2026-08-01 — verbatim latest partial as the active line; committed finals as history; translated text replaces source when completed (PRD FR-5/FR-14; CaptionState exposes ActiveLine + History only).** |

### 11. Close-Out Record

- **Status:** In progress (2026-08-01) — implementation + unit tests complete: `UniversalCaptions.App` (DI composition root, `IOverlayService`, `CaptionOverlayWindow`, `CaptionPipeline`, `ControlWindow`, `AudioSourceLoader`, `TranslationGuard`, `App.xaml.cs`) + `UniversalCaptions.App.Tests` (36 tests); solution builds 0 warnings/0 errors, **209/209** tests pass (66 Audio + 45 Captions + 41 Speech + 21 Translation + 36 App), `dotnet format --verify-no-changes` clean, no vulnerable packages. Q1 display policy resolved and implemented. Fresh-context review of the App code **completed** — fix round closed out M1–M4 + Low-7/8/9 (teardown ordering, fail-on-start teardown paths, immutable `CaptionSnapshot`, `AudioSourceLoader`, `TranslationGuard`); post-fix review found no Critical/High and no Slice 5 blockers (M-1 `Start()` synchronization race at the reusable-class level deferred — not reachable through current UI; overlaps Low-6/TD-014; L-1…L-4 Low accepted). **Manual overlay/device verification completed 2026-08-01** (recorded in `TEST_REPORT.md`): real system audio → Whisper `ggml-base` → live overlay captions; always-on-top/transparency; drag/resize/click-through; stop/restart; rapid Stop→close (clean ~2 s exit); model-not-found error path; source-equals-target rejection live. **Remaining before close-out:** real-Argos wiring verification when the dev Argos venv is available (this machine currently has no argostranslate on system Python — translation defaults Off). Slice 5 will be marked Completed only when that Definition-of-Done item is satisfied.

---

## Impact Analysis Decision

**Decision:** Proceed (contracts + deterministic caption-service tests first; no WPF; real wiring verified in Slice 5).

**Analysis performed by:** Engineering
**Date:** 2026-08-01
