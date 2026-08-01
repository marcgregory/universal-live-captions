# Universal Live Captions Release Plan

Last updated: 2026-07-31

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define release criteria, quality gates, and final go/no-go decision framework |
| Scope | All release criteria, quality gates, and blocking issues |
| Audience | Engineering |
| Owner | Engineering |
| Status | Active |
| Related Documents | [QUALITY_ASSURANCE.md](../QUALITY_ASSURANCE.md), [SECURITY_PLAN.md](../SECURITY_PLAN.md), [BUILD_PLAN.md](BUILD_PLAN.md), [DEPLOYMENT.md](../DEPLOYMENT.md), [TEST_REPORT.md](../reports/TEST_REPORT.md), [RISK_REGISTER.md](../RISK_REGISTER.md), [CHANGELOG.md](CHANGELOG.md) |

---

## Traceability

| Requirement | Implementation | Tests | Release Gate |
|---|---|---|---|
| FR-1 (loopback capture, no VB-CABLE) | `WasapiLoopbackCaptureSource` | `WasapiLoopbackCaptureSourceTests` | Capture verified on real audio |
| FR-2 (buffer/process PCM) | `PcmRingBuffer`, `SampleRateConverter` | `PcmRingBufferTests`, `SampleRateConverterTests` | Unit tests pass |
| FR-3 (VAD) | `EnergyVad` | `EnergyVadTests` | Unit tests pass |
| FR-10 (error handling) | `AudioCaptureError` mapping | `WasapiLoopbackCaptureSourceTests` failure cases | No unresolved blockers |
| NFR-1 (Windows 10) | Verified build target | Manual device verification | Recorded in TEST_REPORT |
| NFR-4 (no raw persistence) | No file writes in pipeline | Security tests | Security checklist |

## Required Release Gates

- [x] Build passed (0 warnings, warnings-as-errors)
- [x] Unit tests passed (Slice 1 suite)
- [ ] Integration (fake-boundary) tests passed (Slice 2+)
- [ ] Manual device verification completed (Slice 1 diagnostics run)
- [x] Security review for MVP scope completed (no persistence, no network)
- [ ] Latency measured (Slice 5)
- [x] No unresolved Blocker or High findings
- [x] Test evidence attached (TEST_REPORT.md)
- [x] Known risks documented (RISK_REGISTER.md)

## Release Criteria

This milestone (Slice 1) is a spike with no user-facing release. Formal release criteria apply to the MVP completion in Slice 5: MVP Definition of Done in [PRD.md](../PRD.md) must be fully satisfied before any release is marked Ready.

## Quality Gates

- `dotnet build UniversalCaptions.slnx` — 0 errors, 0 warnings
- `dotnet test UniversalCaptions.slnx` — all tests pass
- `dotnet format --verify-no-changes` — clean (after formatting is applied)
- Dependency scan clean (`dotnet list package --vulnerable`)

## Demo Checklist

- [ ] Diagnostics console lists output devices
- [ ] Live meter responds to system audio (Chrome/VLC playback)
- [ ] Capture stops cleanly with Ctrl+C
- [ ] Error message readable when device unavailable

## Performance Goals

- Perceived caption latency < 1000 ms where practical (measured in Slice 5)
- No audio dropouts during sustained capture on typical playback workloads

## Release Decision

Decision: **Not a release** — Slice 1 spike milestone only.

Reason: This milestone proves audio capture viability (Slice 1 success criterion). MVP release decision is recorded here at Slice 5 completion.

## Blocking Issues

- None known at Slice 1. Open risk: R-001 (loopback does not capture some content) — under verification.
