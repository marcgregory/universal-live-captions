# Universal Live Captions Change Impact Analysis

Last updated: 2026-08-06

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Record the impact analysis performed before each change (per [CHANGE_IMPACT_PROCESS.md](CHANGE_IMPACT_PROCESS.md)) |
| Scope | Every feature, fix, refactor, or infrastructure change |
| Audience | Engineering, reviewers |
| Owner | Engineering |
| Status | Active |

---

## Entry 16 - CPU Optimization: Cap Faster-Whisper Decode Threads at 4 (UC_NATIVE_THREADS)

Date: 2026-08-06

> **Implementation status (2026-08-06): COMPLETE - production default `Threads` capped at 4.**
> The promoted path sustained ~77% of the machine in the STT worker (every partial and FINAL decode
> used all 12 cores via `FasterWhisperEngineOptions.Threads` = `Environment.ProcessorCount`). The
> `UC_NATIVE_THREADS` env knob (default 4, clamped to [1, ProcessorCount]) is wired through
> `SpeechEngineFactory.CreateNative` to the existing `--threads` worker arg. Formal `sttnative` gate:
> FINAL stream 100% text-identical at 4t vs 12t (WER 33.2% both, 1.18x realtime both, same split
> points), no latency/backlog change. Real-App CPU probe: STT worker system mean **77.4% -> 31.6%**
> (max 88.2% -> 37.6%), App ~1%, first caption 3.72 s, overlay still producing. Full suite **382/382**
> (8 new tests), Release 0 warnings/0 errors, `dotnet format` clean. Out of scope (unchanged): STT
> engine selection, worker wire protocol, segmentation/8 s cap, partial behavior, overlay, Argos,
> TD-001/002/005/016.

### 1. Change Summary

```text
Change Title: Entry 16 - CPU optimization: cap faster-whisper decode threads at 4
Change Type:        Performance tuning of the promoted production path (no behavior change; caption
                    stream text-identical)
Requirement Source: User decision (2026-08-06): proceed with UC_NATIVE_THREADS knob + production
                    default Threads=4, isolated as a CPU optimization slice
Priority:           High
Estimated Effort:   Low (factory knob + worker-arg wiring + benchmark knob + tests)
```

### 2. Affected Modules

- `src/UniversalCaptions.App/SpeechEngineFactory.cs` - `CreateNative` sets `FasterWhisperEngineOptions
  .Threads = ResolveNativeThreads()` (new `UC_NATIVE_THREADS` knob: default 4, unparseable or
  out-of-range [1, ProcessorCount] falls back to 4). ggml-base and windowed-fasterwhisper paths
  untouched.
- `src/UniversalCaptions.Speech/LineProtocolFasterWhisperProcess.cs` - worker arguments extracted into
  `BuildWorkerArguments()` (behavior identical); `--threads` remains the single decode-thread control.
- `src/UniversalCaptions.Benchmarks/NativeStreamingBenchmark.cs` - `sttnative` gains `--threads`
  (default 4) so the gate can sweep decode threads.
- Tests - `SpeechEngineFactoryTests` (default 4 / override / invalid fallback) and
  `LineProtocolFasterWhisperProcessProtocolTests` (worker args carry `--threads`). The engine gains an
  internal `Options` seam + `InternalsVisibleTo("UniversalCaptions.App.Tests")` for factory assertions.
  Pre-existing flaky `CaptionPipelineTests` race hardened (`List` -> `ConcurrentQueue`).

### 3. Impact Analysis

- **Behavioral impact:** none - decode wall is thread-count-invariant for real speech; the FINAL stream
  is text-identical (0 textual diffs across 32 FINALs), WER/realtime factor unchanged. Sustained STT
  worker CPU drops ~2.4x (77.4% -> 31.6% system mean).
- **Data/privacy:** none.
- **Testability:** worker-arg propagation now asserted directly; factory defaults unit-tested; full
  formal benchmark gate + real-App CPU probe repeated.
- **Performance:** intended positive (the machine-share cut); a low-core machine keeps the default
  clamped to its own ProcessorCount.
- **Risks/mitigations:** knob preserved for machines that want more decode cores; no automatic engine
  fallback added (ADR-0003 untouched).

### 4. Verification Evidence

- Full suite **382/382** (Release, 0 warnings/0 errors), `dotnet format` clean.
- `sttnative` gate threads=12 vs 4 on `uc_video_full_16k.wav` vs `fil-orig` (logs
  `%TEMP%\opencode\cpu_gate_t12.log/.csv`, `cpu_gate_t4.log/.csv`): WER 33.2% both, 1.18x both,
  first FINAL 17.98 vs 18.12 s, emit-lag comparable, FINAL text identical.
- Real-App CPU probe (speech + partials, translation OFF, default Threads=4): STT worker
  379.6% single-core mean = **31.6% system** (was 77.4%); first caption 3.72 s; overlay max 16 lines
  (`cpu_speech.csv` post-fix row, `cpu_summary.csv`).

### 5. Decision

**Approved and closed out (2026-08-06).** Production default `Threads = 4` via `UC_NATIVE_THREADS`.
Docs updated (TEST_REPORT Entry 16 close-out, BENCHMARK_REPORT Entry 16 gate, CHANGELOG v0.5.24,
PROJECT_STATUS/BUILD_PLAN/ROADMAP, CLAUDE.md).

---

## Entry 15 — Overlay Integration: Paint the Live Partial Active Line (Chrome-Like Live Partials Visible)

Date: 2026-08-06

> **Implementation status (2026-08-06): COMPLETE — overlay integration applied.**
> The WPF overlay now paints the live/partial active line as a single mutable block that partials
> rewrite in place, and freezes it into committed history on FINAL. This restores the Entry 7 display
> policy (active line = latest partial) that commit `7d1c057` switched off in favour of
> committed-FINAL-only. The change was driven by the real-App smoke test on the Entry 14 promoted
> default (2026-08-06), which showed the engine emits partials but the overlay never displayed them —
> so "Chrome-like live partials" was engine-level only. Slice 12 proved the engine's partial timing;
> Entry 15 proves the WPF overlay displays those partials. Display-model policy (Tagalog-only display
> while a translation is pending, leading-overlap strip, hide-until-translated) is unchanged. Full
> suite **374/374** (2 new overlay render tests), Release 0 warnings/0 errors, `dotnet format` clean.

### 1. Change Summary

```text
Change Title: Entry 15 — overlay integration: paint the live partial active line (replaces the
              committed-FINAL-only display layer)
Change Type:        Display-layer fix surfaced by the Entry 14 real-App smoke test (no STT / engine /
                    workflow changes; ADR-0008 promotion untouched)
Requirement Source: User decision (2026-08-06): do not undo ADR-0008; do not call the product
                    Chrome-like until the overlay actually displays partials; smoke checks 2-3 stay
                    N/A until the overlay paints partials
Priority:           High
Estimated Effort:   Low (restore Slice 7 active-block painting; keep the display-model policy)
```

### 2. Affected Modules

- `UniversalCaptions.App/Overlay/CaptionOverlayWindow.xaml.cs` — `UpdateCaptionItems` now paints the
  active line as a single mutable `TextBlock` (created on first appearance, text rewritten in place
  on later partials, removed when `model.ActiveLine` is null: committed, stopped, or hidden while its
  translation is pending). History reconciliation is unchanged (identity reuse by sequence); a Partial
  never inserts or removes a history block; scroll fires only on a new-block insertion (the active
  line's first appearance or a FINAL freeze). The `shouldUpdate` gate is unchanged (it already holds
  the display while a translation-enabled partial is hidden, so no source-language flash). Class and
  comment text updated from committed-FINAL-only to live-line painting.
- Tests — `CaptionRenderIdentityTests` rewritten (4 → 6): partial paints and rewrites the same block
  (identity preserved), a growing partial stream paints one block with no history churn, no partial
  ever enters committed history, a FINAL freezes the active line into history and removes the active
  block, a cleared active line (Stop) removes the block and keeps history, finalized blocks keep their
  text instances and order.
- Docs — Entry 15, CHANGELOG v0.5.23, PROJECT_STATUS, ROADMAP, BUILD_PLAN, CLAUDE.md, TEST_REPORT
  (smoke honesty + overlay verification), ADR-0007 display-clause supersession note.

### 3. Affected APIs

- None public. Internal: `CaptionOverlayWindow.UpdateCaptionItems` now paints `model.ActiveLine` and
  `_activeBlock` is populated (it was only ever null). **API changes required: none.**

### 4. Database Changes

Not applicable — this application has no database.

### 5. Security and Privacy Implications

- [x] Capture behavior change — none (same WASAPI loopback path; display layer only).
- [x] Audio/transcript handling change — none (local, in-memory; same CaptionService/engine path).
- [ ] New external communication — none.
- [ ] Sensitive data handling — in-memory only.
- [x] Security review required: No (local-only; no new boundaries).

### 6. Test Updates Required

- [x] Unit tests — `CaptionRenderIdentityTests` rewritten (6 tests; 2 new: no-partial-in-history,
  cleared-active-line-removes-block). `CaptionDisplayPolicyTests` and all caption-service tests are
  unchanged and still pass (the display model already carried the active line). Full suite **374/374**
  (App 89, Speech 109, Captions 72, Audio 77, Translation 27), Release 0 warnings/0 errors.
- [ ] Integration tests — n/a (no boundary change).
- [x] Manual/device verification — real-App smoke re-run on the promoted default (see TEST_REPORT
  Entry 15): first visible partial on the overlay, active line updates, FINAL freeze, history does not
  churn, Stop clears the partial, first-visible-partial latency + CPU impact measured.

### 7. Documentation Updates Required

- [x] `CHANGELOG.md` (v0.5.23), `PROJECT_STATUS.md`, `ROADMAP.md`, `BUILD_PLAN.md`, `CLAUDE.md`,
  `TEST_REPORT.md`, `ADR-0007.md` (display-clause note).
- [ ] `PRD.md` — n/a (PRD already requires streaming captions; this realizes it).
- [ ] `ARCHITECTURE.md` — n/a (no architecture change; same caption events → overlay path).
- [ ] `ADR-0008.md` — n/a (promotion unchanged; partials were already the default engine behavior).

### 8. Dependencies and Risks

- [x] Blocked by: none — the promoted default (Entry 14) already emits partials into CaptionService.
- [ ] Blocking: nothing downstream.
- [ ] Risks identified: (1) re-enables per-partial overlay updates — mitigated by the single mutable
  block (no rebuild), identity reuse, and no scroll on text mutation; (2) a translation-enabled active
  line is still hidden until its translation completes (no source flash) — display-model policy
  unchanged; (3) the overlap strip keeps the active line from repeating the previous FINAL's words.
- [x] Mitigation plan: `CaptionDisplayPolicyTests` (unchanged) pin the display-model policy; the
  rewritten render tests pin identity / no-churn / no-partials-in-history; the real-App smoke re-run
  confirms the visible behavior.

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 | `CaptionDisplayPolicy` is the single source of the active-line text (translation-gated, overlap-stripped); the overlay paints it verbatim. | Overlay would show untranslated source or duplicate words. | CaptionDisplayPolicyTests; ADR-0007 Tagalog-only display. |
| 2 | Painting the active line is the intended UX the promotion described ("Chrome-like live partials"); committed-FINAL-only (`7d1c057`) was an investigation-era freeze, not the product decision. | ADR-0007's "display proven stable (FINAL-only)" clause is superseded by this entry. | User decision 2026-08-06; Entry 7 close-out; smoke evidence. |
| 3 | No STT / engine / workflow change is needed — partials already reach CaptionService and the active line. | None — engine cadence unchanged. | Entry 14; Slice 12. |

### 10. Open Questions

| # | Question | Asked Of | Status |
|---|---|---|---|
| 1 | Should the active line auto-scroll to keep the newest text visible when it grows past the fixed viewport (vs. holding the offset until the FINAL)? | User | Low — the smoke run will show whether clipping is observable. |

### 11. Close-Out Record

- **Status: Completed (2026-08-06).** Overlay integration applied: the live partial active line is
  painted and updated in place; FINALs freeze it into stable history; the display-model policy is
  unchanged. `CaptionRenderIdentityTests` rewritten to pin the behavior (2 new tests). Full suite
  **374/374**, Release 0 warnings/0 errors, `dotnet format` clean. Real-App overlay verification
  recorded in `TEST_REPORT.md` (Entry 15). ADR-0007's "display proven stable (FINAL-only)" clause
  noted superseded. No commit made unless requested.

---

## Entry 14 — Production Default Promotion: Faster-Whisper Native Streaming + Live Partials

Date: 2026-08-05

> **Implementation status (2026-08-05): COMPLETE — promotion applied (ADR-0008).**
> Product decision (user-approved 2026-08-05): the production STT default is now
> `FasterWhisperNativeStreamingEngine` with live partials enabled; ggml-base is preserved as the
> explicit fallback (`UC_STT_ENGINE=ggml-base`). Engine selection moved into a testable
> `SpeechEngineFactory` seam (5 new App tests, full suite **372/372**), Release 0 warnings/0 errors.
> Faster-whisper worker protocol, the 8 s `MaxSegmentDuration` cap, the windowed engine, ADR-0007,
> TD-002, and TD-005 are all untouched.

### 1. Change Summary

```text
Change Title: Entry 14 — promote faster-whisper native streaming + live partials to the production
              STT default (ggml-base becomes the explicit fallback)
Change Type:        Production-default decision (product decision, user-approved) + small App refactor
                    (engine selection extracted to SpeechEngineFactory for testability)
Requirement Source: User decision (2026-08-05) after the Slice 12 PASS — the Chrome-like partial
                    behavior and the better Tagalog accuracy are both measured; the goal is to use
                    them, not to keep tuning
Priority:           High
Estimated Effort:   Low (default flip + fallback branch + selection tests + decision records)
```

### 2. Affected Modules

- `UniversalCaptions.App` — `App.xaml.cs`: the STT engine factory lambda now delegates to the new
  `SpeechEngineFactory` (the inline ggml-base/windowed/native selection and the resolve helpers moved
  there). `SpeechEngineFactory.cs` (new): default → native + partials; `UC_STT_ENGINE=ggml-base` →
  original local Whisper; `UC_STT_ENGINE=fasterwhisper` → windowed; `UC_STT_ENGINE=fasterwhisper-native`
  → same native path. Partials default ON via `UC_NATIVE_PARTIAL_INTERVAL` (1 s) /
  `UC_NATIVE_PARTIAL_WINDOW` (4 s); `MaxSegmentDuration` stays 8 s (frozen). No automatic runtime
  fallback (deliberate — silent engine switches violate ADR-0003).
- `UniversalCaptions.Speech` — untouched (the production path reuses the Slice 10–12 native engine).
- Tests — new `SpeechEngineFactoryTests` (5): default/fasterwhisper-native → native; ggml-base →
  Whisper; fasterwhisper → windowed; interval-0 still native (FINAL-only is a knob, not a selection).
- Docs — ADR-0008 (new decision, supersedes ADR-0003's default-model clause), Entry 14, CHANGELOG,
  PROJECT_STATUS, ROADMAP, BUILD_PLAN, CLAUDE.md, BENCHMARK/TEST reports.

### 3. Affected APIs

- New: `UniversalCaptions.App.SpeechEngineFactory` (static: `Create(string? language)` /
  `CreateNative(string? language)`).
- Changed: none to the frozen public surface (`ISpeechToTextEngine`, worker protocol,
  `LineProtocolFasterWhisperProcess`, ADR-0007 all untouched). **API changes required:** none.

### 4. Database Changes

Not applicable — this application has no database.

### 5. Security and Privacy Implications

- [x] Capture behavior change — none (same WASAPI loopback path; engine selection only).
- [x] Audio/transcript handling change — none (local, in-memory; faster-whisper is local).
- [ ] New external communication — none (same local worker process model).
- [ ] Sensitive data handling — in-memory only.
- [x] Security review required: No (local-only; no new boundaries).

### 6. Test Updates Required

- [x] Unit tests — 5 new `SpeechEngineFactoryTests` (default selection, ggml-base fallback, windowed
  opt-in, explicit native, interval-0 knob). Constructors are side-effect-free (worker/model load on
  `Start`), so no Python/model needed. Full suite **372/372** (App 87, Speech 109, Captions 72,
  Audio 77, Translation 27).
- [ ] Integration tests — n/a (worker protocol unchanged).
- [x] Manual/device verification — the production path is exactly the engine + knobs validated by the
  Slice 12 controlled benchmark and the Slice 10 real-App run; a fresh real-App run is the remaining
  manual confirmation (optional, records to be added to TEST_REPORT when run).

### 7. Documentation Updates Required

- [x] `ADR-0008.md` (new) + `ADR-0003.md` supersession note + `ADR README.md` index.
- [ ] `PRD.md` — n/a (implementation detail; PRD already requires streaming captions).
- [ ] `ARCHITECTURE.md` — n/a (no architecture change; same engine abstraction).
- [ ] `TECH_STACK.md` — n/a.
- [x] `CHANGELOG.md` (v0.5.22), `PROJECT_STATUS.md`, `ROADMAP.md`, `BUILD_PLAN.md`, `CLAUDE.md`,
  `BENCHMARK_REPORT.md`, `TEST_REPORT.md`.

### 8. Dependencies and Risks

- [x] Blocked by: none — Slice 12 PASS provides the validated path; the faster-whisper worker is
  auto-discovered (`%TEMP%\fwv` or `UC_FW_PYTHON`).
- [ ] Blocking: nothing downstream.
- [ ] Risks identified: (1) operational dependency on the Python worker + `small` int8 model for the
  default path (ggml-base was self-contained) — mitigated by the explicit ggml-base fallback and the
  documented auto-discovery; (2) partials' ~5 % wall / ~8 s tail-latency cost becomes the default
  experience — documented and measured (Slice 12), and the interval knob still allows FINAL-only;
  (3) a worker failure now surfaces on the default path — it stops the session with a caption error
  (no silent fallback by design); (4) first FINAL still ~15 s (partials mitigate the perceived
  latency) — documented.
- [x] Mitigation plan: keep ggml-base reachable via `UC_STT_ENGINE=ggml-base`; keep the worker
  protocol unchanged; record the cost/tradeoffs explicitly; no tuning changes introduced.

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 | The default faster-whisper worker/model is present on target machines (venv or `UC_FW_PYTHON`); ggml-base is the documented escape hatch. | Worker missing → the default path errors at Start; user switches to `UC_STT_ENGINE=ggml-base`. | Auto-discovery pattern (Argos precedent). |
| 2 | Promoting partials (1 s/4 s) as the default is the right product tradeoff given the measured ~5 % wall / ~8 s tail cost. | Interval is env-tunable back to 0 (FINAL-only) without code change. | Slice 12 benchmark. |
| 3 | No automatic runtime fallback is correct (ADR-0003 no-silent-switch). | A user on a broken worker must manually restart with ggml-base. | ADR-0003. |

### 10. Open Questions

| # | Question | Asked Of | Status |
|---|---|---|---|
| 1 | Fresh real-App visual run on the promoted default (native + partials) to confirm the overlay behavior end-to-end? | User | Optional manual confirmation — can be recorded later. |

### 11. Close-Out Record

- **Status: Completed (2026-08-05).** Product decision applied (ADR-0008). `SpeechEngineFactory` is
  the single selection point; the default is native + partials (interval 1 s, window 4 s, 8 s segment
  cap frozen), `UC_STT_ENGINE=ggml-base` restores the original local-Whisper path, `fasterwhisper`
  keeps the windowed engine, and the worker protocol is unchanged. 5 new App selection tests; full
  suite **372/372**, Release 0 warnings/0 errors, `dotnet format` clean. No automatic fallback (by
  design, ADR-0003). No commit made unless requested.

---

## Entry 13 — Slice 12: Faster-Whisper Native-Streaming Live Partials (Chrome-Live-Caption-style)

Date: 2026-08-05

> **Implementation status (2026-08-05): COMPLETE — benchmark PASS (close-out 2026-08-05).**
> Implementation + deterministic tests (367/367, Release 0 warnings/0 errors, `dotnet format` clean) and
> the controlled real-audio benchmark with partials on are all done: first visible partial 5.59 s after
> speech onset (vs first FINAL 15.0 s), 19.5 partials/120 s (~3 s updates during speech), FINAL stream
> text-identical to Slice 11 (no accuracy regression, WER 33.19% in-harness), backlog bounded (plateau
> ~50 s vs 43 s FINAL-only, realtime-safe 1.18×). Additive live-partial emission on
> `fasterwhisper-native`: bounded trailing-window re-decodes of the in-progress segment at a
> configurable cadence raise `PartialTranscriptAvailable`, giving the Chrome-Live-Caption-style
> "text appears while the speaker is still talking" experience. Partials replace the active overlay
> line through the existing `CaptionService`/overlay path (the existing `CaptionDisplayPolicy` strips
> overlap); one FINAL per completed speech segment is unchanged. FINAL-only behavior (Slice 10/11) is
> exactly preserved when the interval knob is left at 0 (default). The worker wire protocol, ggml-base
> default, windowed engine, and ADR-0007 are untouched; faster-whisper stays opt-in
> (`UC_STT_ENGINE=fasterwhisper-native`); translation stays OFF for this slice.

### 1. Change Summary

```text
Change Title: Slice 12 — live partial transcripts for the faster-whisper native-streaming engine via
              bounded trailing-window re-decodes (no wire-protocol change)
Change Type:        Additive feature (opt-in native engine only; default behavior unchanged)
Requirement Source: User scope decision (2026-08-05); Slice 10/11 gate closed with the tradeoff that
                    the native engine emits one FINAL per completed segment (~9 s cadence) with 0
                    live partials; goal is Chrome-Live-Caption-style incremental text during speech
Priority:           High
Estimated Effort:   Medium (detector snapshot + engine cadence dispatch + deterministic tests +
                    optional real benchmark)
```

### 2. Affected Modules

- `UniversalCaptions.Speech` — additive: `SpeechSegmentDetector.TryGetPartial(maxSamples, out samples,
  out capturedAtUtc)` (trailing-window snapshot over the in-progress segment; refused while idle, during
  hangover, or after the segment completes; capture time = window start). `FasterWhisperEngineOptions`:
  new `PartialDecodeInterval` (default `TimeSpan.Zero` = disabled → Slice 10/11 FINAL-only preserved)
  and `PartialDecodeWindow` (default 4 s, bounds each partial decode). `FasterWhisperNativeStreamingEngine`:
  cadence dispatch with at most one partial decode in flight/queued (no growing backlog), partials cleared
  on FINAL, shared session guard, new `PartialTranscriptAvailable` event (replaces the CS0067 pragma),
  internal `Segment` → `WorkItem(..., IsPartial)` rename, static `ToPcm` helper.
- `UniversalCaptions.App` — the `fasterwhisper-native` branch default knobs `UC_NATIVE_PARTIAL_INTERVAL`
  (default 1 s) and `UC_NATIVE_PARTIAL_WINDOW` (default 4 s) via `ResolveDoubleEnv`; interval 0 restores
  FINAL-only. `ggml-base`, windowed `fasterwhisper`, worker protocol untouched.
- `UniversalCaptions.Benchmarks` — `sttnative` mode: `--partial-interval`/`--partial-window` args, live
  partial metrics (first partial, first-caption lag T4 = emit − window start, partial cadence, min/median/
  max partial lag, LIVE PARTIAL STREAM printout) and CSV partial table + summary columns. Existing
  `stt`/`translate`/`resample` modes untouched.
- Docs — `TEST_REPORT.md`, `BENCHMARK_REPORT.md`, `CHANGELOG.md`, `BUILD_PLAN.md`, `PROJECT_STATUS.md`,
  `ROADMAP.md`, `CLAUDE.md`.

### 3. Affected APIs

- New (additive, all opt-in): `SpeechSegmentDetector.TryGetPartial`; `FasterWhisperEngineOptions.
  PartialDecodeInterval` / `PartialDecodeWindow` (defaults preserve existing behavior); `PartialTranscript`
  + `FasterWhisperNativeStreamingEngine.PartialTranscriptAvailable`; internal `WorkItem`/`ToPcm`.
  **API changes required:** none to the frozen public surface (engine interface, worker protocol,
  `LineProtocolFasterWhisperProcess`, ADR-0007 untouched).

### 4. Database Changes

Not applicable — this application has no database.

### 5. Security and Privacy Implications

- [x] Capture behavior change — none (same WASAPI loopback path; opt-in engine only).
- [x] Audio/transcript handling change — none (local, in-memory; windows decoded locally; nothing
  persisted).
- [ ] New external communication — none.
- [ ] Sensitive data handling — in-memory only.
- [x] Security review required: No (local-only; worker protocol unchanged).

### 6. Test Updates Required

- [x] Unit tests — 10 new deterministic tests (6 `SpeechSegmentDetector.TryGetPartial` + 4 engine
  partial tests incl. a `BlockingFasterWhisperProcess` fake proving single-in-flight bounding). Full
  suite 367/367 (Speech 109, App 82, Captions 72, Audio 77, Translation 27).
- [ ] Integration tests — n/a (worker protocol unchanged).
- [x] Manual/device verification — controlled `sttnative` run with `--partial-interval 1
  --partial-window 4` on the actual video audio vs the `fil-orig` reference (Pending), then optionally a
  real-App visual run (live partials on the overlay while audio plays).

### 7. Documentation Updates Required

- [ ] `PRD.md` — n/a (opt-in experiment; not a product requirement change).
- [ ] `ARCHITECTURE.md` — n/a (no architecture change; partials ride the existing active-line path).
- [ ] `TECH_STACK.md` — n/a.
- [x] `BENCHMARK_REPORT.md` — Slice 12 section (first-caption lag T4, partial cadence, lag distribution).
- [x] `TEST_REPORT.md` — Slice 12 evidence.
- [x] `CHANGELOG.md`, `PROJECT_STATUS.md`, `BUILD_PLAN.md`, `ROADMAP.md`, `CLAUDE.md`.

### 8. Dependencies and Risks

- [x] Blocked by: none — Slice 10/11 provide the engine + harness; the new knobs are env-tunable.
- [ ] Blocking: nothing downstream (no promotion decision; faster-whisper stays opt-in).
- [ ] Risks identified: (1) partial decode latency lands on the critical path — mitigated by the 1-decode
  in-flight/queued bound so the loop can never accumulate a backlog (verified by the blocking-fake test);
  (2) partials overlap the FINAL text — mitigated by the existing `CaptionDisplayPolicy` overlap strip on
  the overlay and by FINAL replacing the active line; (3) extra decode load on a busy machine — the window
  is bounded (default 4 s) and cadence is 1/s; (4) stale partial windows if decode is slow — each partial
  is tagged with its window capture time (freshness visible in the benchmark lag metric).
- [x] Mitigation plan: bound in-flight/queued partials to one (test-verified); measure first-caption lag
  and partial-lag distribution in the real controlled run; validate visually in one real-App run if
  requested.

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 | The existing `CaptionService` (partials replace the active line, finals commit) and the overlay `CaptionDisplayPolicy` overlap strip need no changes for live partials. | Would require overlay/service changes, expanding scope. | Slice 4/5 behavior; Entry 7 live active-line translation. |
| 2 | A 1 s cadence with a 4 s window gives a visible incremental experience (partial every ~1 s, window advances after ~4 s of speech) without unacceptable decode load. | Lag distribution from the controlled run will confirm; knobs are env-tunable. | Slice 10/11 decode round-trips. |
| 3 | At-most-one-in-flight/queued partial keeps latency bounded even under slow decode (ticks dropped, not queued). | Unbounded backlog would regress live latency. | Tested with `BlockingFasterWhisperProcess`. |

### 10. Open Questions

| # | Question | Asked Of | Status |
|---|---|---|---|
| 1 | Should the real controlled run (and/or a real-App visual run) be executed now to close out, or is the deterministic suite sufficient for this slice? | User | Pending — knobs default off, so no production exposure either way. |
| 2 | Should partial cadence/window defaults (1 s / 4 s) be tuned after real evidence? | Engineering (measured) | Depends on Q1 run. |

### 11. Close-Out Record

- **Status: Completed (close-out 2026-08-05) — benchmark PASS.**
  Deterministic suite **367/367**, Release build 0 warnings/0 errors, `dotnet format` clean. 10 new
  tests (6 detector + 4 engine incl. the blocking-fake backlog bound). Controlled real-audio run
  (Release `sttnative`, small int8, `tl`, hangover 0.7 s, max segment 8 s, realtime feed, translation
  OFF) on `uc_video_full_16k.wav` (288.79 s) vs the `fil-orig` reference with
  `--partial-interval 1 --partial-window 4`:

  | Gate metric | Result |
  |---|---|
  | First visible partial (from feed start) | 9.19 s (speech onset 3.60 s) |
  | **First caption lag T4 (onset → first partial)** | **5.59 s** (vs first FINAL 15.0 s) |
  | Partial update cadence | 19.5 partials/120 s (~3 s apart during speech) |
  | Active-line changes while speaking | yes — text increments ("Magandang" → … → full sentence) |
  | FINALs | 32 — text-identical to the Slice 11 8 s run (no accuracy regression) |
  | WER (in-harness) | 33.19% (= Slice 11's 33.19%; the report 32.6% uses `stt_compare.py` normalization) |
  | FINAL latency after speech end | ~6 s after segment close (decode-bound, same as Slice 10/11) |
  | Backlog | bounded: plateau ~50 s (Slice 11 FINAL-only ~43 s), flat, not growing; one 17.5 s decode spike (machine contention); nothing dropped/reordered |
  | Realtime factor | 1.18× (Slice 11 1.13×; partial decodes add ~5 % wall) |
  | Hallucination/repetition | no new artifacts (the "Paano kong?" repetition is in the Slice 11 baseline and the audio) |

  **Gate PASS:** speech → partial caption appears quickly (5.59 s) → incremental updates → stable
  FINAL at/near speech end, with no growing backlog (elevated-but-bounded plateau, realtime-safe) and
  no accuracy regression (identical FINAL stream). Caveats recorded in the reports: ~5 % wall + ~8 s
  tail-latency cost of partial decodes, and the expected rolling-4 s-window tradeoff (the FINAL reveals
  the earlier words not shown by the last partial). `ggml-base` stays the production default; the
  partial knobs default off (`PartialDecodeInterval = 0`), so production behavior is unchanged. This
  benchmark does not constitute promotion. No worker protocol / ggml-base / windowed-engine changes.
  Evidence: `BENCHMARK_REPORT.md` (Slice 12), `TEST_REPORT.md` (Slice 12), CHANGELOG v0.5.21; raw log
  `%TEMP%\opencode\sttnative_partials_slice12.log` (+ `.csv`).

---

## Entry 12 — Slice 11: Native-Streaming Segment-Boundary Tuning (max-segment sweep)

Date: 2026-08-05

> **Implementation status (2026-08-05): complete — decision recorded: keep `MaxSegmentDuration = 8 s`.**
> Additive benchmark-only improvements (timer-granularity pacing fix + mid-sentence-split metric) +
> controlled 8/10/12 s sweep on the actual video audio vs the `fil-orig` reference (WER 32.6%/33.2%/30.0%,
> cadence 13.3/10.8/9.1 FINALs/120 s, splits 31%/42%/45%, 0 partials, bounded latency/backlog). No worker
> protocol / ggml-base / windowed-engine changes; no production or knob-default change.

### 1. Change Summary

```text
Change Title: Slice 11 — tune faster-whisper native streaming segment boundaries
              (UC_NATIVE_MAX_SEGMENT 8/10/12 s sweep; hangover fixed at 0.7 s)
Change Type:        Tuning / experiment (benchmark + real-App measurement; optional knob-default
                    change to the OPT-IN native engine only)
Requirement Source: User scope decision (2026-08-05) after the Slice 10 PASS — goal is not "lower WER"
                    but "accurate + natural sentence boundaries + bounded live latency", which is the
                    legitimate basis for a future default-selection decision
Priority:           High
Estimated Effort:   Medium (sweep runs + boundary metric + real-App validation + decision record)
```

### 2. Affected Modules

- `UniversalCaptions.Benchmarks` — additive: fix the realtime-feed pacing artifact in `sttnative`
  (`Thread.Sleep(10)` ≈ 15.6 ms Windows timer granularity paced audio at ~1.57× wall; raise the timer
  resolution around the feed via `timeBeginPeriod(1)` so controlled latencies are valid) and add a
  mid-sentence-split (continuation) counter so the sweep has a quantified boundary metric. Existing
  `stt`/`translate`/`resample` modes untouched.
- `UniversalCaptions.Speech` — the native engine's `SpeechSegmentDetectorOptions.MaxSegmentDuration`
  default **may** change to the sweep winner (8/10/12 s) as a knob-default tuning of the opt-in engine;
  no behavioral code change.
- `UniversalCaptions.App` — the `fasterwhisper-native` branch default for `UC_NATIVE_MAX_SEGMENT`
  **may** follow the same winner. `ggml-base`, the windowed `fasterwhisper` engine, the worker wire
  protocol, and ADR-0007 are untouched.
- Docs — `TEST_REPORT.md`, `BENCHMARK_REPORT.md` (Slice 11 sweep), `CHANGELOG.md`, `BUILD_PLAN.md`,
  `PROJECT_STATUS.md`, `ROADMAP.md`, `CLAUDE.md`.

### 3. Affected APIs

- New: none (benchmark-only metric + timer-resolution fix). Possible additive knob-default change to
  `SpeechSegmentDetectorOptions.MaxSegmentDuration` (default 8 s → winner). **API changes required:**
  None — existing public surface unchanged.

### 4. Database Changes

Not applicable — this application has no database.

### 5. Security and Privacy Implications

- [x] Capture behavior change — none (same WASAPI loopback path; opt-in engine only).
- [x] Audio/transcript handling change — none (local, in-memory; segments decoded locally).
- [ ] New external communication — none.
- [ ] Sensitive data handling — in-memory only.
- [x] Security review required: No (local-only; worker protocol unchanged).

### 6. Test Updates Required

- [x] Unit tests — none required (no engine behavior change unless the knob default changes; if it
  does, the detector tests assert the cap behavior and only the default value assertion updates).
- [ ] Integration tests — n/a (worker protocol unchanged).
- [x] Manual/device verification — controlled `sttnative` sweep at max-segment 8/10/12 s on the actual
  video audio vs the `fil-orig` reference, then a real-App validation run on the winner.

### 7. Documentation Updates Required

- [ ] `PRD.md` — n/a (opt-in experiment; not a product requirement change).
- [ ] `ARCHITECTURE.md` — n/a (no architecture change).
- [ ] `TECH_STACK.md` — n/a.
- [x] `BENCHMARK_REPORT.md` — Slice 11 sweep section (splits/WER/cadence/latency by cap; decision).
- [x] `TEST_REPORT.md` — Slice 11 validation evidence.
- [x] `CHANGELOG.md`, `PROJECT_STATUS.md`, `BUILD_PLAN.md`, `ROADMAP.md`, `CLAUDE.md`.

### 8. Dependencies and Risks

- [x] Blocked by: none — Slice 10 PASS provides the engine + harness; `UC_NATIVE_MAX_SEGMENT` is
  already env-tunable.
- [ ] Blocking: nothing downstream (no promotion decision yet; the sweep informs a future
  default-selection decision only).
- [ ] Risks identified: (1) longer segments raise the displayed STT latency (segment duration + decode)
  even though staleness-at-commit stays ~decode time — the decision must weigh splits-vs-latency; (2)
  music/pause boundaries interact with a longer cap (longer segments could span a music break with
  silence padding — hangover is fixed at 0.7 s and will cut those, but the cap interplay is measured);
  (3) controlled-run latency validity depends on the timer-resolution fix being effective.
- [x] Mitigation plan: run the full sweep at 8/10/12 s; quantify splits (continuation counter) AND
  latency (emit-lag vs segment end, bounded check); validate the winner with one real-App run; record
  the decision with the split-vs-latency tradeoff explicit.

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 | Longer `MaxSegmentDuration` (10/12 s) reduces mid-sentence splits without unboundedly growing latency or backlog, because decode stays well below realtime (~0.5×) and the cap only bounds segment length. | Longer segments could still split at the cap or bridge a music gap with silence, degrading naturalness. | Slice 10 decode round-trips (~3.5–5 s per 8 s segment); user tuning direction. |
| 2 | `SilenceHangover = 0.7 s` is the right baseline and is not swept (per user). | A different hangover might matter more; noted as future work if the sweep is inconclusive. | User (2026-08-05). |
| 3 | The winner is applied as the native engine's knob default (opt-in engine only); ggml-base production default and the windowed path stay frozen. | Applying a default to the opt-in engine does not affect production behavior. | Freeze rules; Entry 11 decision. |

### 10. Open Questions

| # | Question | Asked Of | Status |
|---|---|---|---|
| 1 | At what max-segment does the split/latency tradeoff favor a default (8/10/12 s)? | Engineering (measured) | Sweep pending. |
| 2 | Should the native engine's default cap be updated to the winner now, or left at 8 s with a documented recommendation? | Engineering (this slice) | Decide from sweep data. |

### 11. Close-Out Record

- **Status: Completed (close-out 2026-08-05) — decision recorded: keep `MaxSegmentDuration = 8 s`.**
  **Frozen by user 2026-08-05 — no further `MaxSegmentDuration` tuning; Slice 11 is closed.**
  Controlled `sttnative` sweep on the actual video audio vs the `fil-orig` reference (small int8, tl,
  realtime feed with the timer-granularity fix, hangover 0.7 s fixed):

  | MaxSegment | FINALs | Cadence | WER (norm) | Mid-sentence splits | Fragments | End-of-audio cap |
  |---|---|---|---|---|---|---|
  | 8 s | 32 | 13.3/120 s | 32.6% | 10/32 (31%) | 0 | clean (last speech segment committed before the music tail) |
  | 10 s | 26 | 10.8/120 s | 33.2% | 11/26 (42%) | 1 | capped segment spanning the music tail decoded as a `Pag-pag-pag…` stutter |
  | 12 s | 22 | 9.1/120 s | 30.0% | 10/22 (45%) | 1 | capped segment decoded as a truncated `tunog` fragment |

  Findings: longer segments do **not** reduce mid-sentence splits (fraction worsens 31% → 42% → 45%;
  the cap still force-closes mid-sentence, just less often while each cut discards more in-flight
  content). The 12 s WER gain (30.0% vs 32.6%) is a boundary-artifact effect but costs ~46%
  responsiveness (9.1 vs 13.3 FINALs/120 s) and adds end-of-audio cap risk (music-tail hallucinations
  at 10 s/12 s). Latency/backlog stays bounded at all three caps (emit ~5 s behind segment end; worst
  decode ~8 s for a capped 12 s segment < segment length). **Decision: keep 8 s as the native engine's
  `MaxSegmentDuration` default — no production or knob-default change.** 8 s reproduces the Slice 10
  WER exactly (32.6%), confirming the timer fix did not alter accuracy and the controlled run is now a
  valid pacing baseline (1.13× realtime). Real-App latency/backlog evidence for the kept default is the
  Slice 10 real-App run (`realapp_native_streaming.log`); no redundant re-run was needed. No worker
  protocol / ggml-base / windowed-engine changes. Evidence: `BENCHMARK_REPORT.md` (Slice 11),
  `TEST_REPORT.md` (Slice 11), CHANGELOG v0.5.20.

---

## Entry 11 — Slice 10: Faster-Whisper Native Streaming (C# VAD segment commit)

Date: 2026-08-05

> **Implementation status (2026-08-05):** complete — deterministic phase + benchmark/real-App
> validation PASSED. `FasterWhisperNativeStreamingEngine` + internal `SpeechSegmentDetector` behind
> `UC_STT_ENGINE=fasterwhisper-native`; 21 new deterministic tests (no Python), full suite **357/357**,
> Release build 0 warnings/0 errors, format clean; fresh-context review PASSED with fixes. Validation
> (additive `sttnative` benchmark mode + real-App run on the actual video audio vs the `fil-orig`
> reference): committed WER **32.6%** (ggml-base 51.2%), **0 partials (FINAL-only)**, commit cadence
> **13.3 FINALs/120 s** (windowed faster-whisper 2/120 s), first real-App caption **15.2 s**, STT latency
> ~4 s behind segment end, no growing backlog — the stale 20–40 s commit problem is eliminated while
> faster-whisper accuracy is preserved. Decision gate: faster-whisper stays **opt-in**, ggml-base default
> unchanged (frozen); documented tradeoff = 8 s segment cap can split sentences mid-word. Evidence:
> `TEST_REPORT.md` (Slice 10), CHANGELOG v0.5.19, BUILD_PLAN Slice 10.

### 1. Change Summary

```text
Change Title: Slice 10 — FasterWhisperNativeStreamingEngine: C#-side VAD speech-segment detection
              drives one FINAL per completed speech segment through the EXISTING faster-whisper
              worker wire protocol (no protocol change, no base-engine change)
Change Type:        Feature (new experimental engine behind UC_STT_ENGINE=fasterwhisper-native)
Requirement Source: User scope decision (2026-08-05); Slice 8/9 findings (faster-whisper small int8
                    WER 31.1% vs ggml-base 51.2% but stale 1-FINAL-per-40s cadence under the
                    ggml-base-oriented sliding-window loop); user acceptance targets (accuracy,
                    responsiveness, streaming behavior)
Priority:           High
Estimated Effort:   Medium (new engine + segment detector + deterministic tests, then benchmark +
                    real-App validation)
```

### 2. Affected Modules

- `UniversalCaptions.Speech` — new `FasterWhisperNativeStreamingEngine` (`ISpeechToTextEngine`); new
  internal segment-buffer/detector state machine (`SpeechSegmentDetector` — name TBD); **reuses**
  `FasterWhisperDecoder`/`ISTTDecoder` → `LineProtocolFasterWhisperProcess`/`IFasterWhisperProcess`
  unchanged. `WhisperSpeechToTextEngine`, `StreamingTranscriptCommitter`, `FasterWhisperSpeechToTextEngine`,
  `faster_whisper_worker.py` are all **untouched**.
- `UniversalCaptions.Core.Processing` — reuse existing `IVoiceActivityDetector` contract (no change).
- `UniversalCaptions.Audio` — reuse existing `EnergyVad` impl (no change).
- `UniversalCaptions.App` — `App.xaml.cs` STT factory gains a `fasterwhisper-native` branch (additive);
  ggml-base default and `fasterwhisper` (windowed) branches unchanged.
- Tests — `UniversalCaptions.Speech.Tests` (segment detector + native engine), `UniversalCaptions.App.Tests`
  (selector maps `fasterwhisper-native` → new engine type); optionally extend the benchmark `stt` mode
  with a segment-commit measurement path.

### 3. Affected APIs

- New: `FasterWhisperNativeStreamingEngine` (public, `ISpeechToTextEngine`), internal `SpeechSegmentDetector`
  state machine, internal ctor seam for scripted VAD + scripted decoder (mirrors the
  `FasterWhisperSpeechToTextEngine` internal ctor pattern). New option surface reuses
  `FasterWhisperEngineOptions` for process/model fields plus segment knobs (recommended defaults, provisional):
  `MinSpeechDuration` (~0.3 s), `SilenceHangover` (~0.6–1.0 s), `MaxSegmentDuration` (~8 s hard latency cap),
  trailing-silence policy (include a short pad vs cut exactly).
- **API changes required:** Additive (new engine + selector value; no existing API or behavior modified).

### 4. Database Changes

Not applicable — this application has no database.

### 5. Security and Privacy Implications

- [x] Capture behavior change — none (same WASAPI loopback path; new engine is selectable only).
- [x] Audio/transcript handling change — none (faster-whisper already local/offline; segments are
  decoded locally via the existing worker; no persistence).
- [ ] New external communication — none.
- [ ] Sensitive data handling — in-memory only.
- [x] Security review required: No (local-only; worker protocol unchanged).

### 6. Test Updates Required

- [x] Unit tests — `SpeechSegmentDetector` state machine (deterministic, synthetic PCM): speech starts;
  speech continues; short silence does not prematurely cut; sustained silence closes a segment; minimum
  speech duration; maximum segment duration (hard cap force-close); trailing silence; Stop flushes an
  in-progress segment. `FasterWhisperNativeStreamingEngine` (scripted VAD + scripted decoder): exactly one
  FINAL per completed segment, no partials emitted, no duplicate/re-emitted segments, Stop flush emits a
  FINAL for the in-progress segment, decode-failure → `RecognitionFailed`, restart resets segment state,
  `CapturedAtUtc` = segment start (E2E latency metric intact).
- [ ] Integration tests — n/a (worker protocol unchanged; fake-boundary tests only).
- [x] Manual/device verification — real-App run through the existing `diag_capture.ps1`-style harness with
  `UC_STT_ENGINE=fasterwhisper-native`, actual video audio + fil-orig reference, **after** all deterministic
  tests pass (benchmark/test first per user).

### 7. Documentation Updates Required

- [ ] `PRD.md` — n/a (experiment; not a product requirement change).
- [x] `ARCHITECTURE.md` — optional add-only note when the engine lands (new selectable engine under
  `ISpeechToTextEngine`; no architecture change).
- [ ] `TECH_STACK.md` — n/a (reuses existing faster-whisper runtime).
- [ ] `SECURITY_PLAN.md` — n/a.
- [ ] `QUALITY_ASSURANCE.md` — n/a.
- [x] ADR required: No (fits ADR-0003/0005 engine abstraction; faster-whisper already ADR-covered; no
  model/pair/stack change). **No ADR-0007 changes** (this is engine-level streaming policy, not the
  ggml-base committer).
- [x] `CHANGELOG.md`, `ROADMAP.md` (Sprint Queue), `PROJECT_STATUS.md` (current sprint), `BENCHMARK_REPORT.md`
  (findings + decision-gate), `TEST_REPORT.md` (evidence), `CHANGE_IMPACT_ANALYSIS.md` (this entry).

### 8. Dependencies and Risks

- [ ] Blocked by: faster-whisper dev venv per TD-011 (`%TEMP%\fwv`); the worker + `small` int8 model
  (already used by the existing faster-whisper path).
- [ ] Blocking: none.
- [ ] Risks identified:
  1. **EnergyVad (RMS) vs speech/noise discrimination.** The `(Song)`/`(Subscribe)` hallucinations were
     ggml-base transcribing music/UI bleeps; with C# VAD gating segments to speech-only, non-speech never
     reaches the worker. But RMS VAD may not reject music/background as well as Silero VAD. Mitigation:
     threshold tuning in deterministic tests; if music rejection proves inadequate, follow up with the
     worker's built-in `vad_filter` as a separate decision (protocol unchanged, worker flag only).
  2. **Segment decode cost.** A max-segment ~8 s at faster-whisper 5.85× realtime ≈ 1.4 s decode — bounded
     and well under the old 20–40 s backlog; `MaxSegmentDuration` is the hard latency cap (no indefinite
     wait). Risk: cutting mid-word at the cap. Mitigation: word-boundary/trim at decode, recorded as an
     acceptance observation (one coherent FINAL per segment).
  3. **Segments straddle sentence boundaries.** FINAL-only per segment may still split a sentence when
     silence is brief. Mitigation: this is the experiment's explicit target — measure inter-FINAL cadence
     and coherence; live partials are explicitly a follow-up, not mixed in.
  4. **FINAL-only UX regression risk.** No active-line partials during a segment means the overlay shows
     nothing until the segment ends — accepted per user decision; measured against the responsiveness
     acceptance target (first caption close to natural cadence).
- [x] Mitigation plan: deterministic tests first (no Python), then benchmark/real-App validation; production
  default untouched; rollback = unset `UC_STT_ENGINE`.

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 | Selector is `UC_STT_ENGINE=fasterwhisper-native` (new, additive); `fasterwhisper` keeps the windowed engine; unset/`whisper` = ggml-base production default. | Old faster-whisper benchmarks would be unreproducible or the default could silently change. | User decision (2026-08-05). |
| 2 | VAD/segment detection lives in the C# engine (Speech), consuming the existing Core `IVoiceActivityDetector` with `EnergyVad` composed in via the App (Speech must not reference Audio per REPOSITORY_STANDARDS). | Boundary rule would be violated; test seam harder. | User decision (VAD in C# engine); REPOSITORY_STANDARDS dependency table. |
| 3 | One FINAL per completed speech segment; no live partials in this experiment. | Perceived cadence is coarser than today's active-line UX. | User decision (FINAL-only). |
| 4 | Segment knob defaults are provisional (~0.3 s min speech, ~0.6–1.0 s hangover, ~8 s max duration) and are tuned via the deterministic tests + benchmark, not accepted as final. | Wrong defaults would show as cadence/coherence defects in validation. | Provisional; re-derivation planned. |
| 5 | The worker wire protocol (magic `UCWF`, version 1, request/response) is unchanged; `IFasterWhisperProcess`/`LineProtocolFasterWhisperProcess` and `faster_whisper_worker.py` are untouched. | A protocol change would expand the slice and break TD-016 contract tests. | User decision ("existing worker wire protocol does not need to change"). |

### 10. Open Questions

| # | Question | Asked Of | Status |
|---|---|---|---|
| 1 | Whether the benchmark `stt` mode should gain a native-streaming measurement path, or real-App validation alone is sufficient evidence. | User | Deferred — decide at the deterministic-tests gate. |
| 2 | Whether the final accepted engine (if the experiment passes) repoints `fasterwhisper` or keeps `fasterwhisper-native`. | User | Deferred — decision-gate at the end of the slice. |

### 11. Scope Record (2026-08-05)

- **Decision:** Scope the slice as an **isolated experiment** — new `FasterWhisperNativeStreamingEngine`
  behind `UC_STT_ENGINE=fasterwhisper-native`; **ggml-base path untouched, windowed faster-whisper path
  untouched, worker protocol untouched, ADR-0007 untouched, no default promotion.** Acceptance is NOT
  "WER improved" alone; it must demonstrate (a) accuracy substantially better than base with no recurring
  `(Song)`/`(Subscribe)` hallucinations, (b) first caption close to natural cadence with no 20–40 s stale
  backlog, (c) one coherent FINAL per speech segment, no duplicate/re-emitted segments, no dropped final
  at Stop, and the existing translation path still works. If the experiment fails any target, faster-whisper
  stays opt-in and production stays exactly as it is. Test/benchmark first; real-App validation only after
  deterministic tests pass.

---

## Entry 10 — TD-005: File-Based Settings Persistence (overlay/caption preferences)

Date: 2026-08-05

### 1. Change Summary

```text
Change Title: TD-005 — per-user file persistence of the six UI-preference categories (audio source, speech language, translation on/off + target, overlay appearance, overlay placement, overlay view state)
Change Type:        Feature (user prefs no longer reset on restart)
Requirement Source: TECHNICAL_DEBT.md TD-005; user TD sprint order (2026-08-04); recorded design (TD-005 row, 2026-08-05)
Priority:           Low
Estimated Effort:   Medium (design recorded first; implementation + six deterministic acceptance tests)
```

### 2. Affected Modules

- `UniversalCaptions.App/Settings` — new `UserSettings` (immutable record, nullable = use default, `Version` for future migration), `ISettingsStore` (injectable seam), `SettingsStore` (file-backed, `%LocalAppData%\UniversalCaptions\settings.json`).
- `UniversalCaptions.App` — `App.xaml.cs` loads settings **before** window construction and registers `ISettingsStore` + `UserSettings`; `ControlWindow` applies persisted settings on load and saves its categories on change; `CaptionOverlayWindow` applies appearance/placement/view state on load and saves placement + view state on change.
- Tests — `UniversalCaptions.App.Tests/SettingsStoreTests` (six deterministic acceptance tests).

### 3. Affected APIs

- New `UniversalCaptions.App.Settings.ISettingsStore` (`Load`/`Save`), `SettingsStore` (parameterless = LocalAppData path; `string directory` test seam), `UserSettings` record — **Additive** (new types; no existing API changes).
- `ControlWindow` and `CaptionOverlayWindow` constructors gain `ISettingsStore` + `UserSettings` parameters (DI-resolved).

**API changes required:** Additive (backward-compatible).

### 4. Database Changes

Not applicable — this application has no database.

### 5. Security and Privacy Implications

- [ ] Capture behavior change — none.
- [ ] Audio/transcript handling change — **none: the settings file stores UI preferences only, never raw audio or transcripts** (privacy rule: no raw audio persistence).
- [x] New external communication — none (local file write to `%LocalAppData%`).
- [x] Sensitive data handling — settings are per-user plain JSON on the local disk; content is limited to device ids, language codes, and overlay appearance values. `SECURITY_PLAN.md` lists user configuration as a future persistence milestone — this change documents it there.
- [ ] Security review required: No.

### 6. Test Updates Required

- [x] Unit tests — `SettingsStoreTests` (6): save→load round-trip; missing file → defaults; malformed/wrong-type → safe defaults; unknown/new fields ignored (forward compatibility); atomic write + failed write preserves last good; concurrent/rapid saves settle without torn state.
- [ ] Integration tests — n/a (file store is deterministic; windows verified manually).
- [x] Manual/device verification — the persistence wiring is exercised via the real App's normal use (settings survive restart); recorded in `TEST_REPORT.md`.

### 7. Documentation Updates Required

- [ ] `PRD.md` — n/a (TD remediation).
- [ ] `ARCHITECTURE.md` — n/a (App-local feature; no architecture change).
- [ ] `TECH_STACK.md` — n/a (in-box `System.Text.Json`, no new dependency).
- [x] `SECURITY_PLAN.md` — add the settings-file persistence note (T-6 asset: user configuration) when the feature lands.
- [ ] `QUALITY_ASSURANCE.md` — n/a.
- [x] ADR required: No (fits ADR-0004 App scope; no stack/privacy change).
- [x] `CHANGELOG.md`, `TECHNICAL_DEBT.md` (TD-005 close), `TEST_REPORT.md` (evidence), `CHANGE_IMPACT_ANALYSIS.md` (this entry).

### 8. Dependencies and Risks

- [ ] Blocked by: none.
- [ ] Blocking: none.
- [ ] Risks identified:
  1. Two windows own disjoint persisted categories, so a naive "save my fields" from either could clobber the other's. Mitigation: both windows **merge** into the currently persisted file (`load → with { own fields } → save`), so control-window saves preserve overlay placement/view state and vice versa; all saves originate on the UI dispatcher, so read-modify-write spans are serialized (plus the store lock).
  2. Chatty saves while dragging the opacity/font sliders. Mitigation: control-window saves are coalesced onto the dispatcher (Background priority) — a burst settles to the last UI state with one write; a synchronous flush on window close guarantees the final state.
  3. A persisted overlay placement could pin the overlay off-screen if the monitor layout changes. Mitigation: placement is only persisted once the user explicitly drags; a never-dragged overlay keeps the adaptive bottom-anchored default.
  4. A corrupt settings file must never block startup. Mitigation: `Load` returns defaults on missing/malformed files and never throws; `Save` is atomic (`.tmp` + `File.Move(overwrite)`), so a crash never leaves a partial file.
- [ ] Mitigation plan: encoded in the six deterministic acceptance tests + the window merge/coalesce design.

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 | The six user-facing categories are: (1) audio source device, (2) speech language, (3) translation on/off + target, (4) overlay appearance (opacity/font/click-through), (5) overlay placement, (6) overlay view state (expanded). | A category is missed or a non-UI value is persisted. | Discovery in the TD-005 row (2026-08-05); user approval of the recorded design. |
| 2 | Engine/environment knobs (`UC_STT_*`, Argos/Python paths, model selection) stay env-var-driven and are NOT persisted. | Environment-specific paths leak into user settings and break on another machine. | Recorded design; user instruction ("keep engine/environment knobs out of persistence"). |
| 3 | Settings file location `%LocalAppData%\UniversalCaptions\settings.json` (per-user, no elevation, no in-repo data). | A different location would need a migration. | Recorded design; `SECURITY_PLAN.md` per-user config model. |
| 4 | `System.Text.Json` ignores unknown JSON properties by default, giving forward compatibility for fields written by a newer app version. | A newer file's fields would be dropped on an older app (accepted; known fields still load). | .NET 8 `System.Text.Json` behavior; acceptance test 4. |

### 10. Open Questions

| # | Question | Asked Of | Status |
|---|---|---|---|
| 1 | None. | — | — |

### 11. Close-Out Record

- **Design recorded (2026-08-05):** discovery confirmed no persistence existed (SECURITY_PLAN listed file persistence as a future milestone); the TD-005 row captured the design + failure behavior + six acceptance criteria before any production change.
- **Implementation complete (2026-08-05).** New `UserSettings`/`ISettingsStore`/`SettingsStore` (camelCase JSON, camelCase→case-insensitive read, unknown fields ignored, atomic `.tmp` → `File.Move(overwrite: true)`, store lock). `App.xaml.cs` loads settings before window construction and registers the store + settings; `ControlWindow` applies persisted device/language/translation/appearance on load and saves on change (coalesced dispatcher saves + close flush, merge-into-file to preserve overlay-owned fields); `CaptionOverlayWindow` seeds opacity/font/click-through/expanded/placement and saves placement + view state on drag/collapse/close. **335/335 tests passing** (App 76→82: 6 new `SettingsStoreTests`), Release build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean. **No change to TD-002 / ADR-0007 / model selection / the resampler.**

---

## Entry 9 — TD-002: Device-Change Notifications + Automatic Recovery (contract + tests first)

Date: 2026-08-05

### 1. Change Summary

```text
Change Title: TD-002 — device-change notifications (RegisterEndpointNotificationCallback) for automatic recovery of the default-device capture session
Change Type:        Feature / Fix (recovery UX gap on device hotplug)
Requirement Source: TECHNICAL_DEBT.md TD-002; user TD sprint order (2026-08-04); TD-001-style discipline (trace → design → deterministic tests → decide whether production implementation is justified)
Priority:           Medium
Estimated Effort:   Medium (contract + tests this pass; production wiring + real-device verification gated on the decision)
```

### 2. Affected Modules

- `UniversalCaptions.Core.Capture` — new `IDeviceChangeMonitor`, `DeviceChangeNotification`, `DeviceChangeKind`, `DeviceState` (additive, Core-pure — no NAudio dependency).
- `UniversalCaptions.Audio` — new `WasapiDeviceChangeNotifier` (`IMMNotificationClient` registration, lazy `MMDeviceEnumerator` so unit tests touch no COM).
- `UniversalCaptions.App` — new `DefaultDeviceAutoRecovery` coordinator (default-device auto-restart policy; **not wired into `CaptionPipeline`/`App.xaml.cs` in this pass**).
- Tests — `UniversalCaptions.Audio.Tests` (notifier contract, 11), `UniversalCaptions.App.Tests` (recovery coordinator, 9).

### 3. Affected APIs

- `UniversalCaptions.Core.Capture.IDeviceChangeMonitor` (+ `DeviceChangeNotification` record and enums) — **Additive** (new interface; no existing API changes).
- `WasapiDeviceChangeNotifier`, `DefaultDeviceAutoRecovery` — new types. `CaptionPipeline` and `App.xaml.cs` are **unchanged** this pass (frozen baseline untouched).

**API changes required:** Additive (backward-compatible).

### 4. Database Changes

Not applicable — this application has no database.

### 5. Security and Privacy Implications

- [ ] Capture behavior change — **none in this pass** (no wiring; baseline capture path untouched).
- [ ] Audio/transcript handling change — none.
- [ ] New external communication — none (local WASAPI endpoint notification only).
- [ ] Sensitive data handling — none (endpoint IDs only).
- [ ] Security review required: No.

### 6. Test Updates Required

- [x] Unit tests — `WasapiDeviceChangeNotifierTests` (11: render/capture filter, state mapping, add/remove, property-ignore, dispose-suppression) and `DefaultDeviceAutoRecoveryTests` (9: restart-on-default-change, explicit-device non-restart, unplug/notpresent restart, active no-restart, add/remove no-restart, burst coalescing, dispose).
- [ ] Integration tests — n/a.
- [x] Manual/device verification — **deferred to the production-implementation decision** (real hotplug/unplug of the default device through the real App).

### 7. Documentation Updates Required

- [ ] `PRD.md` — n/a (TD remediation; not a new product requirement).
- [ ] `ARCHITECTURE.md` — `ARCHITECTURE.md` no longer lists Core Capture types exhaustively; add-only at wiring time if the coordinator is wired.
- [ ] `TECH_STACK.md` — n/a.
- [ ] `SECURITY_PLAN.md` — n/a.
- [ ] `QUALITY_ASSURANCE.md` — n/a.
- [x] ADR required: No (fits ADR-0001 native/NAudio stack; no stack or privacy change).
- [x] `CHANGELOG.md`, `TECHNICAL_DEBT.md` (TD-002 status), `TEST_REPORT.md` (evidence), `CHANGE_IMPACT_ANALYSIS.md` (this entry).

### 8. Dependencies and Risks

- [ ] Blocked by: none.
- [ ] Blocking: none.
- [ ] Risks identified:
  1. Real `RegisterEndpointNotificationCallback` behavior (callback thread affinity, registration lifetime, behavior when the audio service is stopped) is only verifiable on real hardware/Windows — unit tests cover the mapping/contract, not COM registration. Mitigation: lazy enumerator; `Stop`/`Dispose` always unregister+dispose; production wiring pass will record manual verification in TEST_REPORT before promoting.
  2. Auto-restart could fight an explicit user device choice. Mitigation: the coordinator only restarts while the live session is on the **default** device (`isOnDefaultDevice`); explicit-device sessions are never auto-restarted.
  3. Restart churn on a burst of endpoint events. Mitigation: coalescing guard — one restart per notification window.
- [ ] Mitigation plan: as above; all decisions are encoded in the deterministic tests.

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 | The app's capture scope is render (output) devices only, so the monitor surfaces only `DataFlow.Render` endpoint changes. | A future microphone-capture feature would need a broader monitor; the Core contract already carries state/kind so it extends. | ADR-0001; `WasapiLoopbackCaptureSource` (loopback-only). |
| 2 | Automatic recovery applies only to the system-default session; a user-picked device stays on their explicit choice. | Auto-restarting an explicit choice would be surprising and destructive. | TD-002 "automatic recovery" intent; PRD FR-10 user control. |
| 3 | Restart triggers = default-device changed, or current endpoint unplugged/not-present (monitor-driven). Disconnect-triggered restart via `CaptureFailed` is a production-wiring concern for the post-decision packet. | A pure `AUDCLNT_E_DEVICE_INVALIDATED` without a preceding state/default notification would not auto-recover until the next monitor event. | WASAPI device-invalidated behavior; TD-002 row. |

### 10. Open Questions

| # | Question | Asked Of | Status |
|---|---|---|---|
| 1 | Is production implementation justified (wire `WasapiDeviceChangeNotifier` + `DefaultDeviceAutoRecovery` into `CaptionPipeline`/`App.xaml.cs`, plus disconnect-triggered restart and manual hotplug verification)? | User | **Decided — Approved (2026-08-05):** wiring implemented + tested; **only real hotplug verification remains** (TD-002 stays Open until it passes). |

### 11. Close-Out Record (this pass)

- **Contract + deterministic tests complete (2026-08-05).** New `IDeviceChangeMonitor`/`DeviceChangeNotification`/`DeviceChangeKind`/`DeviceState` (Core), `WasapiDeviceChangeNotifier` (Audio, `IMMNotificationClient`), `DefaultDeviceAutoRecovery` (App, off-by-default). 20 new tests (11 Audio + 9 App); full suite **322/322 passing** (66→77 Audio, 60→69 App), Release build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean.
- **Production wiring complete (2026-08-05, user-approved step-6 decision).** `CaptionPipeline` composes a `DefaultDeviceAutoRecovery` when given an `IDeviceChangeMonitor` and exposes `IsOnDefaultDevice` + `RestartCaptureAsync` (capture-only restart, STT preserved, stop/dispose-guarded, coalesced); `Removed` added as a restart trigger; `WasapiDeviceChangeNotifier` registered in `App.xaml.cs`. **7 pipeline recovery tests** added; full suite now **329/329** (App 69→76). **Real hotplug verification pending — TD-002 stays Open.** No change to `ggml-base` / faster-whisper / ADR-0007 / resampler.

---

## Impact Analysis Decision

**Decision:** Proceed with the contract + deterministic tests this pass; **production implementation (wiring + manual hotplug verification) gated on the user's TD-002 step-6 decision.**

**Step-6 decision (2026-08-05): Approved.** Production wiring implemented (`CaptionPipeline` recovery + `WasapiDeviceChangeNotifier` in DI) + 7 pipeline recovery tests; full suite **329/329**. **Only real hotplug verification remains** — TD-002 stays Open until it passes (see Entry 9 Close-Out Record).

**Analysis performed by:** Engineering
**Date:** 2026-08-05

---

## Entry 8 — Slice 6: End-to-End Latency/Accuracy Baseline (E2E metric + OFAT sweep)

Date: 2026-08-01

### 1. Change Summary

```text
Change Title: Slice 6 — measure end-to-end caption latency/accuracy on real audio and baseline the latency knobs (window size, decode interval, StabilityWindow)
Change Type:        Feature / Measurement infrastructure
Requirement Source: BUILD_PLAN.md Slice 6; PRD NFR-2 (perceived latency < 1 s, "must be measured, not assumed"); BENCHMARK_REPORT.md open follow-up #1; user direction 2026-08-01 (baseline-first: add the E2E metric before any parameter sweep; OFAT 3-values-per-knob baseline, then shortlist, then real-app validation)
Priority:           High
Estimated Effort:   Medium-Large (E2E metric + tests, then sweep harness + real runs)
```

### 2. Affected Modules

- `UniversalCaptions.Core.Captions` — `CaptionLine` gains `TranslationStartedAtUtc` / `TranslationCompletedAtUtc` (additive); the originating `CapturedAtUtc` already flows onto every line.
- `UniversalCaptions.Captions` — `CaptionService` stamps translation start/completion timestamps in both translation paths (active-line live translation and committed-line translation); an injectable clock (`Func<DateTime>? utcNow`) makes the metric deterministic in tests.
- `UniversalCaptions.App` — `CaptionPipeline` raises a new `EndToEndLatencyUpdated` event (separate from the unchanged `LatencyUpdated`, which remains STT-final latency) with partial/final samples; `ControlWindow` surfaces it.
- `UniversalCaptions.Benchmarks` — STT command parameterized (`--window`, `--interval`, `--stability`, `--model`, feed mode), streamed-finals WER, CPU/RAM during streaming, CSV/table output for an OFAT matrix.
- Tests — `CaptionServiceTests`, `CaptionPipelineTests`, `CaptionStateTests` (timestamp propagation).

### 3. Affected APIs

- New: `CaptionLine.TranslationStartedAtUtc`, `CaptionLine.TranslationCompletedAtUtc` (optional ctor params + `With*` overloads); `CaptionService` optional `utcNow` ctor param; `CaptionPipeline.EndToEndLatencyUpdated`; `EndToEndLatencySample`.
- **API changes required:** Additive (optional params/new members — no existing call sites broken). `LatencyUpdated` semantics unchanged (STT-final latency).

### 4. Database Changes

Not applicable — no database.

### 5. Security and Privacy Implications

- [x] Capture behavior change — no: reuses the existing WASAPI loopback source; no microphone capture; no new audio handling.
- [x] Audio/transcript handling change — no: the metric adds in-memory timestamps only; no persistence, no new external communication.
- [ ] New external communication — none at runtime.
- [ ] Sensitive data handling — in-memory only.
- [x] Security review required: No (local-only measurement; benchmark uses local Whisper + local Argos).

### 6. Test Updates Required

- [x] Unit tests — `CaptionLine` timestamp propagation (With* methods preserve/carry timestamps); `CaptionService` stamping (normal STT→translation→publication sets start/completion; cancellation and stale results produce no timestamps/no event; translation disabled → none; a newer partial replacing an older translation stamps only the applied line; originating timestamp propagation gives E2E = completed − captured); `CaptionPipeline` `EndToEndLatencyUpdated` (partial sample for an active translated line, final sample for a committed translated line, no sample for untranslated/failed/stale, translation-latency component). Uses a fake clock + deterministic fakes.
- [x] Integration tests — none automated; real Whisper → Argos → overlay E2E remains manual (recorded).
- [ ] Manual/device verification — SAPI E2E App runs at baseline + shortlisted configs; Phase 2 real-app validation (YouTube/Chrome, VLC, Zoom) is a manual acceptance pass (requires the user to play content). Recorded when done (see TEST_REPORT).

### 7. Documentation Updates Required

- [x] `docs/reports/BENCHMARK_REPORT.md` — new Slice 6 section: methodology, OFAT baseline matrix, streamed-finals WER, CPU/RAM, shortlist.
- [x] `docs/reports/TEST_REPORT.md` — E2E metric unit tests + manual SAPI/real-app runs.
- [x] `docs/implementation/BUILD_PLAN.md`, `PROJECT_STATUS.md`, `CHANGELOG.md`, `CLAUDE.md` — Slice 6 In Progress + phased plan.
- [x] ADR required: No new ADR during the baseline phase; changing the default model/pair from benchmark results is a Level-4 Must-Ask (AGENT_DECISION_POLICY). **Answered 2026-08-01**: the user approved promoting the validated `base/8/1/st2` baseline to the App default (model unchanged, so ADR-0003 stands; no new ADR).

### 8. Dependencies and Risks

- [ ] Blocked by: none.
- [ ] Blocking: **resolved 2026-08-01** — the validated baseline `base/8/1/st2` was promoted to the App default (user-approved Must-Ask); Phase 2 real-app validation (YouTube/Chrome, VLC, Zoom) remains deferred per user and may revisit the defaults.
- [ ] Risks identified: (1) `CapturedAtUtc` for partials/finals is an *estimated* audio boundary (window end / committed-until) from `StreamingTranscriptCommitter`, not the exact speech frame — accepted and documented as the latency definition; (2) E2E is defined at caption *publication* (the caption event the overlay renders on) and excludes dispatcher render scheduling — documented; (3) the active-line single-slot design means superseded/stale translations are discarded by design, so E2E samples measure only applied (non-stale) translations; (4) OFAT grid compute time — mitigated with a 3-values-per-knob grid (~16–28 runs); (5) `streamFactor` is meaningless as throughput (TD-009) — the matrix reports decode factor + CPU as the headroom signal.
- [x] Mitigation plan: deterministic fake-clock/engine tests for the metric; OFAT + shortlist methodology; methodology recorded before results so numbers are reproducible; full gates after every code change.

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 | E2E latency = originating audio timestamp (`CaptionLine.CapturedAtUtc`) → translated caption publication (`CaptionLineUpdated` for a translated line). | Metric would measure the wrong span and make configs incomparable. | User direction ("time from the audio frame containing the speech to the moment the translated caption is published to the UI"); existing `CapturedAtUtc`. |
| 2 | OFAT grid centered on current defaults: window {6, 8, 10} s, decode interval {0.5, 1, 2} s, StabilityWindow {2, 3, 5}; models {base (default), tiny}; samples {jfk (clean/canonical), OSR (conversational/pseudo-reference)}. | Baseline would not span the operating range of each knob. | User direction (3 values per parameter, centered on the default); existing harness references. |
| 3 | Streamed-finals WER uses the full-file canonical/pseudo references already in the harness. | Accuracy numbers would not be apples-to-apples. | Existing `WER` computation (Program.cs:330–360). |
| 4 | "Same scripted speech corpus for every run": deterministic WAV samples for the engine sweep (WER-capable); the repeatable SAPI scripted corpus for App-level E2E runs. | Runs would not be reproducible. | User direction; Entry 7 SAPI technique. |
| 5 | Translation accuracy = Argos char-similarity (existing benchmark metric), reported per shortlisted config in the E2E App runs. | Translation quality would be unmeasured in the sweep. | BENCHMARK_REPORT.md Slice 3 quality metric. |
| 6 | Latency is the primary optimization metric; accuracy/stability are hard constraints (a more accurate caption arriving seconds late is not better). | Wrong configuration might be chosen. | User direction 2026-08-01. |

### 10. Open Questions

| # | Question | Asked Of | Status |
|---|---|---|---|
| 1 | Phase 2 real-app validation (YouTube/Chrome, VLC, Zoom) requires the user to play content on the desktop. | User | Deferred — scheduled after the baseline shortlist. |
| 2 | Changing the default model/pair from the baseline results is a Must-Ask. | User | **Answered 2026-08-01** — user approved promoting the validated baseline **`base/8/1/st2`** to the App default (model `ggml-base` kept; `StabilityWindow` 3→2, one authoritative config shared with the benchmark), labeled "validated baseline for the current release" with defaults revisited after Phase 2. No new ADR (no model/pair change). |

**Status: Completed (close-out 2026-08-01).** Phase 1a (E2E latency metric + tests, 238/238), Phase 1b (OFAT sweep + shortlist in `BENCHMARK_REPORT.md`), and Phase 1c (App-level SAPI E2E validation in `TEST_REPORT.md` — baseline + shortlist × 3 runs each through the real App, every run publishing real translated Tagalog) are complete. The validated Slice 6 baseline **`base/8/1/st2` was promoted to the App default** (`StabilityWindow` 3→2 via `WhisperEngineOptions` + `App.xaml.cs` + benchmark `Program.cs`; model default `ggml-base` unchanged). Phase 2 (real-app validation) remains deferred per user; defaults may be revisited after it.

---

## Entry 7 — Live Active-Line Translation + Chrome-Style Overlay Redesign

Date: 2026-08-01

### 1. Change Summary

```text
Change Title: Live-translate the in-progress caption line; redesign the overlay as a Chrome-style pill
Change Type:        Feature / UI refinement
Requirement Source: User request ("translated agad bilang text"; Chrome-style auto-sized overlay with white text, chevron, close); supersedes the "active line = verbatim latest partial only" half of the Entry 6 Q1 resolution
Priority:           High
Estimated Effort:   Medium
```

### 2. Affected Modules

- `UniversalCaptions.Core` — `CaptionState.ReplaceActiveLine(original, updated)` (instance-identity-guarded replacement of the active line).
- `UniversalCaptions.Captions` — `CaptionService`: live translation of the active line via a single in-flight slot (`MaybeStartActiveLineTranslation`, `RunActiveLineTranslationAsync`, `ApplyActiveLineTranslation`); `ProcessPartial` kicks it; the slot self-replenishes when a newer partial arrived; results are discarded when translation is disabled mid-flight or superseded by a newer partial; active-line tasks join `_inFlight` so `FlushAsync`/teardown drain them.
- `UniversalCaptions.App` — `CaptionDisplayModel` (+`TranslationEnabled`/`TargetLanguage`/`LanguageBadge`); `CaptionOverlayWindow` redesigned (auto-sized `SizeToContent`, translucent pill, white text, language badge, expand/collapse chevron, close button that hides); `ControlWindow` adds a "Show Captions" button and Start re-shows the overlay.
- Tests — `CaptionStateTests`, `CaptionServiceTests`, `CaptionDisplayPolicyTests` (14 new tests; total 224).

### 3. Affected APIs

- New: `CaptionState.ReplaceActiveLine`; `CaptionDisplayModel` gains `TranslationEnabled`/`TargetLanguage`/`LanguageBadge`.
- **API changes required:** Additive (new Core method; record extended with optional params — no existing call sites broken).

### 4. Database Changes

Not applicable — no database.

### 5. Security and Privacy Implications

- [x] Capture behavior change — no: reuses the existing WASAPI loopback source; no microphone capture.
- [x] Audio/transcript handling change — no: text for live translation is sent to the same local Argos process already used for committed lines; nothing leaves the machine; no persistence.
- [ ] New external communication — none at runtime.
- [ ] Sensitive data handling — in-memory only.
- [x] Security review required: No (local-only; existing translation path; the close button only hides the overlay — capture/translation are unaffected).

### 6. Test Updates Required

- [x] Unit tests — `CaptionState.ReplaceActiveLine` (apply, instance-mismatch, after-clear, state validation); `CaptionService` live translation (translate-on-partial, off-makes-no-request, failure-preserves-source, single-slot serialization + self-replenish, stale-result discard, discard-on-commit, disabled-mid-flight discard, event raised, enable-mid-session translates current partial); `CaptionDisplayPolicy` language badge (enabled/disabled).
- [x] Integration tests — none automated; real Whisper → Argos → overlay wiring remains manual.
- [x] Manual/device verification — completed 2026-08-01 with real system audio + real Argos (en→tl) through the App: live active-line translation (English partial → Tagalog in the in-progress line before commit), `TL` badge, chevron expand/collapse of the committed history, close hides overlay, "Show Captions" re-shows, pipeline keeps translating while hidden. Recorded with a timed sample timeline in `TEST_REPORT.md`.

### 7. Documentation Updates Required

- [x] `CHANGELOG.md`, `PROJECT_STATUS.md`, `TEST_REPORT.md`, `CLAUDE.md` — behavior + test-count updates (224/224).
- [x] This entry records the refined Q1 display policy: the active line is the latest partial, **live-translated** into the target language as soon as its translation completes (no longer verbatim-source-only).
- [ ] ADR required: No new ADR — the overlay remains ADR-0004 (`IOverlayService` unchanged in surface).

### 8. Dependencies and Risks

- [ ] Blocked by: none.
- [ ] Blocking: Slice 6 (end-to-end latency/accuracy) now measures the live-translated active line too.
- [ ] Risks identified: (1) Argos cannot be cancelled per partial (process is killed on cancellation) — mitigated with a single in-flight slot that self-replenishes, plus instance-identity stale-guarding; (2) under continuous speech with ~1 s Argos calls, most per-partial results may be stale-discarded (translated active line appears mostly during slow/hesitant speech) — by design; (3) mid-flight disable does not force-cancel (Argos constraint) — the completed result is discarded, never applied; (4) `SizeToContent` + positioning is resolved at `Loaded` after first layout; (5) the font-size slider now scales history text via inherited attached property (local template `FontSize` removed).
- [x] Mitigation plan: deterministic fake-engine tests (slot, stale-discard, disable-discard); fresh-context review (findings: font-slider history scaling fixed; disable-discard guard added; double-start race is benign-by-identity and documented).

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 | Live-translating the active line is the requested UX: the overlay reads in the target language while the speaker is still talking, not only after commit. | Overlay would show source text mid-utterance, missing the requested behavior. | User request. |
| 2 | One active-line translation at a time is acceptable (Argos serializes; no per-partial cancellation). | Concurrent per-partial requests would kill the Argos child or queue unboundedly. | Argos process constraint (TD-013). |
| 3 | A result whose request started before translation was disabled is discarded, not applied. | A completed translation would briefly appear in a language the user turned off. | User note ("let stale ones finish and don't apply the stale result"). |

### 10. Open Questions

None.

### 11. Close-Out Record

- **Status: Completed (close-out 2026-08-01).** Implementation + unit tests complete: `ReplaceActiveLine`, live active-line translation slot, display-model badge, Chrome-style overlay, ControlWindow "Show Captions" button; **224/224 tests pass** (66 Audio + 58 Captions + 41 Speech + 21 Translation + 38 App), build 0 warnings/0 errors, `dotnet format --verify-no-changes` clean. Fresh-context review completed (correctness/races/tests/architecture verified sound; font-slider history scaling + disable-discard guard fixed; double-start race benign-by-identity documented). **Manual verification completed 2026-08-01** (recorded in `TEST_REPORT.md`): with the App running against real WASAPI loopback audio and the real local Argos child process (dev venv per TD-011, target `tl`), SAPI-spoken English was transcribed by Whisper and **live-translated into Tagalog in the in-progress overlay line while the speaker was still talking, before commit** — e.g. "world"→`Daigdig`, "This is"→`Ito ay`, "translation"→`Pagsasalin`, "test"→`pagsubok`, and the full sentence "The quick brown fox jumps over the lazy dog. Thank you for listening to the translation test."→`Ang mabilis na brown fox ay lumukso sa ibabaw ng tamad na aso. Salamat sa inyong pakikinig sa pagsubok sa pagsasalin.` A 300 ms UIA poll timeline shows the English partial being replaced by Tagalog on the active line before the caption committed; the `TL` badge stayed visible throughout. Overlay controls verified via UIA: chevron expands the committed history (8 committed lines, all `IsTranslated = True`) and collapses it (pill height 235→109 px); the close button hides the overlay (window leaves the UIA tree); ControlWindow "Show Captions" re-shows it; and speech while hidden still produced a fresh live-translated active line ("The meeting starts at nine o'clock"→`Nagsisimula ang pulong sa alas - 9.`). **Entry 7 closed out.**

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
- [x] Integration tests — none automated; real Whisper → caption → overlay wiring is manual (recorded in TEST_REPORT, Slice 5/6 end-to-end).
- [x] Manual/device verification — overlay + control window run on this Windows 10 machine with real system audio, including the real-Argos wiring end-to-end (recorded in TEST_REPORT).

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
- [ ] Risks identified: (1) WPF transparency/click-through + always-on-top behavior on Windows 10 (mitigated by ADR-0004 decision + manual verification); (2) per-monitor DPI positioning of the overlay (PerMonitorV2 per ADR-0004); (3) UI thread never blocking on the audio pipeline — events marshalled to the dispatcher; (4) real Argos wiring runs only when translation is enabled and the dev Argos venv is present (verified manually 2026-08-01 with the recreated venv; unit tests use fakes); (5) Whisper model path resolution (env `UC_STT_MODEL_PATH` / default `artifacts/models/ggml-base.bin`).
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

- **Status: Completed (close-out 2026-08-01).** Implementation + unit tests complete: `UniversalCaptions.App` (DI composition root, `IOverlayService`, `CaptionOverlayWindow`, `CaptionPipeline`, `ControlWindow`, `AudioSourceLoader`, `TranslationGuard`, `App.xaml.cs`) + `UniversalCaptions.App.Tests` (36 tests); solution builds 0 warnings/0 errors, **209/209** tests pass (66 Audio + 45 Captions + 41 Speech + 21 Translation + 36 App), `dotnet format --verify-no-changes` clean, no vulnerable packages. Q1 display policy resolved and implemented. Fresh-context review of the App code **completed** — fix round closed out M1–M4 + Low-7/8/9 (teardown ordering, fail-on-start teardown paths, immutable `CaptionSnapshot`, `AudioSourceLoader`, `TranslationGuard`); post-fix review found no Critical/High and no Slice 5 blockers (M-1 `Start()` synchronization race at the reusable-class level deferred — not reachable through current UI; overlaps Low-6/TD-014; L-1…L-4 Low accepted). **Manual overlay/device verification completed 2026-08-01** (recorded in `TEST_REPORT.md`): real system audio → Whisper `ggml-base` → live overlay captions; always-on-top/transparency; drag/resize/click-through; stop/restart; rapid Stop→close (clean ~2 s exit); model-not-found error path; source-equals-target rejection live. **Real-Argos wiring verified end-to-end through the App 2026-08-01** (recorded in `TEST_REPORT.md`): with the dev Argos venv recreated (`argostranslate==1.11.0` + en→tl/tl→en/ja→en/en→ja under a short 8.3 temp path per TD-011), the App spawned the Argos child process and committed overlay lines displayed real translated Tagalog (`tamad aso.`, `Ang mabilis na kayumangging sorra ay tumatalon sa ibabaw ng eruplano`, `IsTranslated = True`); this also exercised the `ControlWindow.ApplyTranslationSettings` guard fix (toggle stays ON + target combo enabled on a guard error). **All Slice 5 Definition-of-Done items satisfied; Slice 5 marked Completed.**

---

## Impact Analysis Decision

**Decision:** Proceed (contracts + deterministic caption-service tests first; no WPF; real wiring verified in Slice 5).

**Analysis performed by:** Engineering
**Date:** 2026-08-01
