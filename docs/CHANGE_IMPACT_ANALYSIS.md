# Universal Live Captions Change Impact Analysis

Last updated: 2026-08-05

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Record the impact analysis performed before each change (per [CHANGE_IMPACT_PROCESS.md](CHANGE_IMPACT_PROCESS.md)) |
| Scope | Every feature, fix, refactor, or infrastructure change |
| Audience | Engineering, reviewers |
| Owner | Engineering |
| Status | Active |

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
