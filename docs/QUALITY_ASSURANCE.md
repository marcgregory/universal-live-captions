# Universal Live Captions Quality Assurance Plan

Last updated: 2026-07-31

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define QA strategy, test types, evidence requirements, and release criteria |
| Scope | All testing activities |
| Audience | Engineering, QA, Product |
| Owner | Engineering |
| Status | Active |
| Related Documents | [TEST_REPORT.md](reports/TEST_REPORT.md), [RELEASE_PLAN.md](implementation/RELEASE_PLAN.md), [SECURITY_PLAN.md](SECURITY_PLAN.md) |

---

## Testing Scope

Automated unit tests for every non-hardware pipeline component, plus fake-boundary tests for hardware-dependent stages (audio device, STT engine) and manual device verification for real capture. Integration path verified with fakes at the appropriate boundaries where real hardware/STT is non-deterministic.

## Test Types

### Unit Tests

- Framework: xUnit
- Coverage target: every public behavior has a test; line coverage reported when a tool is configured
- Run command: `dotnet test`
- Scope: buffering, sample conversion, resampling, VAD, level meter, capture failure mapping, caption state logic, transcript handling

### Integration Tests

- Framework: xUnit with fakes of boundary interfaces (`IWaveIn`, `ISpeechToTextEngine`)
- Run command: `dotnet test`
- Scope: capture source ↔ processor, STT abstraction ↔ caption service

### API / E2E / Accessibility / Responsive / Security / Performance Tests

- **API, responsive**: not applicable (no server, no web UI)
- **E2E**: manual device verification — run diagnostic/console and app on real hardware; recorded in [TEST_REPORT.md](reports/TEST_REPORT.md)
- **Security**: see [SECURITY_PLAN.md](SECURITY_PLAN.md) MVP security tests; run per release
- **Performance**: latency measurement harness in Slice 5 (`dotnet run` diagnostic), results recorded in TEST_REPORT.md

## Browser and Device Matrix

Not applicable — desktop application.

### OS Matrix

| OS | Minimum Build | Priority |
|---|---|---|
| Windows 10 | 1809 (build 17763) | High |
| Windows 11 | Any | Medium |

## Test Environments

| Environment | Configuration | Access |
|---|---|---|
| Local dev | .NET 8 SDK, real audio device | Developer |

## Test Data Strategy

- Generated deterministic PCM (sine waves, silence, known patterns) for buffering/conversion/VAD tests.
- Fake `IWaveIn` for capture-source tests (no real device needed).
- Real audio (playback through a browser/media player) for manual E2E verification.

### Data Seeding

- N/A — no database.

## Regression Strategy

Every slice re-runs the full `dotnet test` suite. Regression = any previously passing test fails or behavior regresses; fix before proceeding.

### Critical Regression Paths

1. WASAPI loopback → PCM → buffer → meter (Slice 1)
2. PCM → STT abstraction → partial/final transcript (Slice 2)
3. partial → final caption transition, ordering, duplicates (Slice 3)
4. overlay creation/update/visibility/config/shutdown (Slice 4)
5. full pipeline on real audio (Slice 5)

## Bug Severity Levels

| Severity | Definition | Response SLA | Fix Requirement |
|---|---|---|---|
| **Blocker** | Prevents further work or causes data loss | Immediate | Must fix before next build |
| **Critical** | Core feature broken, no workaround | < 4 hours | Must fix before release |
| **High** | Important feature broken, workaround exists | < 24 hours | Should fix before release |
| **Medium** | Feature partially broken, workaround exists | < 72 hours | Fix scheduled in sprint |
| **Low** | Cosmetic or minor issue | Next release | Fix when prioritized |
| **Suggestion** | Improvement or enhancement | Triage | Consider for backlog |

## Entry Criteria for Release

- [ ] All planned features implemented
- [ ] All unit tests pass
- [ ] All integration (fake-boundary) tests pass
- [ ] Manual device verification recorded
- [ ] Dependency scan clean
- [ ] No Blocker or Critical bugs open
- [ ] Documentation updated

## Exit Criteria for Testing

- [ ] All planned test cases executed
- [ ] Actual vs expected results recorded for every test
- [ ] All defects triaged
- [ ] Regression suite executed
- [ ] Release decision documented in TEST_REPORT.md

## Required Evidence

| Test Type | Minimum Evidence |
|---|---|
| Unit / Integration | `dotnet test` runner output with pass/fail counts |
| E2E (manual) | Console/app session log with observed output |
| Performance | Latency measurement output |
| Security | Dependency scan output |

## Release-Blocking Failures

- Any Blocker or Critical bug unresolved
- Required regression suite failed
- Dependency scan produced a critical finding
- Privacy model violated (silent capture, persistence, unexpected transmission)
- Core workflow broken (no captions from captured audio)

## Validation Statuses

| Status | Definition |
|---|---|
| **Passed** | Executed and verified with evidence attached |
| **Pending** | Implementation or verification is incomplete |
| **Skipped** | Intentionally out of scope or not applicable |
| **Not Tested** | Not attempted |

> **Important:** Code inspection, successful compilation, or the existence of a test file is not execution evidence. A check may only be marked **Passed** when the relevant command or workflow has actually been executed and its result recorded.
