# Universal Live Captions Technical Debt

Last updated: 2026-08-01

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Track known technical debt, prioritize remediation, and plan cleanup |
| Scope | Code quality, architecture, performance, and test coverage improvements |
| Audience | Engineering |
| Owner | Engineering |
| Status | Active |
| Related Documents | [BUILD_PLAN.md](BUILD_PLAN.md), [ARCHITECTURE.md](../ARCHITECTURE.md), [QUALITY_ASSURANCE.md](../QUALITY_ASSURANCE.md) |

---

| ID | Item | Priority | Reason | Impact | Planned Sprint | Owner | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| TD-001 | Replace windowed-sinc resampler with NAudio `WdlResampler` or a benchmarked alternative if STT accuracy/latency demands it | Medium | Initial resampler is a fixed-kernel FIR chosen for determinism; quality not yet benchmarked against speech | Potential STT accuracy impact at extreme ratios | Slice 2 (benchmark) | Engineering | Open |
| TD-002 | Add device-change notifications (`RegisterEndpointNotificationCallback`) for automatic recovery | Medium | Slice 1 relies on `RecordingStopped` + manual retry | Manual recovery UX gap on device hotplug | Slice 2 | Engineering | Open |
| TD-003 | Centralize DI composition with `Microsoft.Extensions.DependencyInjection` | Low | Slice 1 uses constructor injection only | Wiring cost grows with pipeline stages | Slice 4 | Engineering | Open |
| TD-004 | Configure line coverage measurement tooling | Low | Coverage targets exist in QA plan but no coverage collector configured | Coverage not measured | Slice 5 | Engineering | Open |
| TD-005 | Settings persistence (file-based) for overlay/caption preferences | Low | MVP keeps settings in-process | User prefs reset on restart | Slice 4/5 | Engineering | Open |
| TD-006 | `StreamingTranscriptCommitter` word-boundary back-off does not apply when the stable prefix equals the whole shortest hypothesis (`i == length`), so a repeatedly decoded mid-word truncation (e.g. `"hello wor"`) can be committed as final. Fix risk: whisper segments carry no trailing whitespace, so backing off at `i == length` could drop the final word of every caption — needs a real-streaming check before changing | Low | Documented by fresh-context review (non-blocking); current behavior is rarely reached (windows grow, so truncations diverge) and matches the tested trailing-space contract | A caption could lock a partially recognized word | Slice 6 (real-device) | Engineering | Open |
| TD-007 | Immutable finals: if Whisper later revises text already committed (epoch-boundary re-segmentation), the committer appends the divergent text instead of replacing it, producing a garbled transcript. Needs a caption-service-level policy (e.g. re-anchoring or clearing the line) in Slice 4 | Low | Fresh-context review (non-blocking); no test revises committed text | Rare garbled captions after boundary re-segmentation | Slice 4 | Engineering | Open |
| TD-008 | No backpressure in `WhisperSpeechToTextEngine`: unbounded channel + growing window means a decoder slower than realtime grows memory without bound. Current models decode ≤0.28× so it is latent, but a slow model/device would drift | Medium | Fresh-context review (non-blocking); benchmark measures decode ≤0.28× realtime | Unbounded memory on slow hardware | Slice 6 (latency/CPU) | Engineering | Open |
| TD-009 | Benchmark harness: `streamFactor` ≈ 1.0 by construction (feed sleeps 0.5 s per 0.5 s chunk) so it reflects harness pacing, not engine throughput; 8 kHz→16 kHz upsampling is naive linear interpolation (aliasing, only affects the OSR pseudo-reference); downloads have no timeout/cancellation | Low | Fresh-context review (non-blocking) | Overstated/understated throughput and WER figures; hung downloads | Slice 6 | Engineering | Open |
| TD-010 | Argos translation: `tl` as a source is unsupported (stanza has no `tl` SBD pipeline); `ja→tl` requires a pivot through `en` (~1050 ms/call) because no direct `ja→tl` model is installed. Fix risk: none today — MVP pairs use `tl` as target only and pivoting is verified | Low | Argos 1.11 limitation (documented in ADR-0006 / BENCHMARK_REPORT) | `tl`-source or high-frequency `ja→tl` captions unsupported; ~3–5× latency on pivot | Slice 4 (if pair becomes MVP) | Engineering | Open |
| TD-011 | Argos dev venv must live outside the repo (Windows `MAX_PATH` 260-char limit on `artifacts\argos\venv`; WinError 206 during torch install, `LongPathsEnabled=0`, non-admin). Workaround: venv at a short 8.3 path under `%TEMP%`; re-creatable dev tooling documented in TECH_STACK | Medium | Windows MAX_PATH limitation; original `artifacts\argos\venv` install failed | Dev onboarding friction; argosvenv is not reproducible in-repo | Slice 4 | Engineering | Open |
| TD-012 | Argos identical-input caching returns ~0.3 ms for repeated identical text — misleading for latency measurement and, if a caption line re-sends the same final, would return stale results. Benchmark uses distinct texts; caption service must not rely on repeated identical finals being re-translated | Low | Argos internal cache (documented in BENCHMARK_REPORT) | Stale/latency-misleading reads if identical finals re-translated | Slice 4 | Engineering | Open |
| TD-013 | `LineProtocolArgosProcess` has no direct unit tests — the risky protocol logic (startup ping, `ok:false` + `kind` mapping, id-mismatch, malformed JSON, timeout→`Timeout`, kill-on-timeout, restart-after-kill) is only exercised by manual smoke + benchmark. Fix risk: a scripted fake Python child (echo harness) could cover the seam deterministically without the real venv | Low | Fresh-context review (Slice 3 close-out); engine-level recovery is covered via `FakeArgosProcess` | A protocol regression could slip through unit gates | Slice 4 | Engineering | Open |
| TD-014 | `LineProtocolArgosProcess.Dispose()` does not acquire `_gate`, so a dispose racing an in-flight `WaitAsync`/`ExchangeAsync` can throw `ObjectDisposedException` (unwrapped) or release a disposed semaphore. Fix risk: engine typically disposes at shutdown, but a Slice 4 service could dispose while a translation is in flight | Low | Fresh-context review (Slice 3 close-out) | Hard-to-reproduce shutdown crash | Slice 4 | Engineering | Open |
| TD-015 | `LineProtocolArgosProcess` accumulates stderr unboundedly in a `StringBuilder` for diagnostics; a chatty child process could grow memory without limit | Low | Fresh-context review (Slice 3 close-out); stderr is best-effort diagnostics only | Memory growth on a verbose child | Slice 4 | Engineering | Open |
