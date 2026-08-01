# Universal Live Captions Risk Register

Last updated: 2026-07-31

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Track, assess, and mitigate technical, product, schedule, security, and infrastructure risks |
| Scope | All risks affecting project delivery, quality, security, and operations |
| Audience | Engineering, Product |
| Owner | Engineering |
| Status | Active |
| Related Documents | [SECURITY_PLAN.md](SECURITY_PLAN.md), [ARCHITECTURE.md](ARCHITECTURE.md), [RELEASE_PLAN.md](implementation/RELEASE_PLAN.md) |

---

## Risk Assessment Matrix

- Probability: 1 = Rare (<10%), 2 = Unlikely (10–30%), 3 = Possible (30–60%), 4 = Likely (60–90%), 5 = Almost Certain (>90%)
- Impact: 1 = Negligible, 2 = Minor, 3 = Moderate, 4 = Major, 5 = Catastrophic
- Score = Probability × Impact. 1–4 Low, 5–9 Medium, 10–14 High, 15–25 Critical

---

## Risk Register

### Risk R-001: WASAPI loopback does not capture some content

| Attribute | Value |
|---|---|
| Category | Technical |
| Description | Exclusive-mode and copy-protected audio is not captured by loopback; some apps route audio outside the default render mix |
| Probability | Possible (3) |
| Impact | Moderate (3) |
| Risk Score | **9** — Medium |
| Owner | Engineering |
| Status | Mitigating |

**Mitigation:** Document known limitations; offer VB-CABLE as an optional future input (out of MVP scope); verify against Chrome/YouTube, VLC, and Zoom in Slice 6.

**Contingency:** Add VB-CABLE as an optional alternate capture source behind the same `IAudioCapture` abstraction.

**Triggers:** A target app's audio is not captured in Slice 5 verification.

**Review Date:** 2026-08-31

---

### Risk R-002: Whisper accuracy/latency does not meet the < 1 s target

| Attribute | Value |
|---|---|
| Category | Technical |
| Description | Model size, CPU performance, and audio chunking determine perceived latency and accuracy; unmeasured until Slice 2/6 |
| Probability | Possible (3) |
| Impact | Moderate (3) |
| Risk Score | **9** — Medium |
| Owner | Engineering |
| Status | Mitigating |

**Mitigation:** Defer model selection until benchmarking (per approved direction); benchmark candidate models on the actual target hardware; abstraction allows engine/model swap without pipeline changes.

**Contingency:** Use a smaller model with faster partial hypotheses; adjust chunking/VAD; document measured latency as the accepted value.

**Triggers:** Slice 2 or Slice 6 benchmark shows > 1 s perceived latency.

**Review Date:** 2026-08-31

---

### Risk R-003: Windows 10 device-state variance causes capture failures

| Attribute | Value |
|---|---|
| Category | Technical |
| Description | Device disconnect, driver churn, and format changes during a session can stop capture |
| Probability | Likely (4) |
| Impact | Minor (2) |
| Risk Score | **8** — Medium |
| Owner | Engineering |
| Status | Mitigating |

**Mitigation:** Map NAudio errors to user-readable messages; detect device disconnection via `RecordingStopped`; provide retry/restart; add device-change notification handling in Slice 2.

**Contingency:** Automatic restart of capture on device availability changes.

**Triggers:** Device unplugged during capture in manual testing.

**Review Date:** 2026-08-31

---

### Risk R-004: Privacy perception blocks adoption

| Attribute | Value |
|---|---|
| Category | Product |
| Description | A global system-audio capturer may be perceived as spyware if capture indication is unclear |
| Probability | Possible (3) |
| Impact | Major (4) |
| Risk Score | **12** — High |
| Owner | Engineering |
| Status | Mitigating |

**Mitigation:** Always-visible capture indicator; explicit start/stop; no persistence; local-only processing; privacy model documented in [SECURITY_PLAN.md](SECURITY_PLAN.md) and [PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md).

**Contingency:** Add first-run privacy disclosure screen and OS-level permission documentation.

**Triggers:** User feedback questioning capture behavior.

**Review Date:** 2026-08-31

---

### Risk R-005: Scope creep into a transcription recorder

| Attribute | Value |
|---|---|
| Category | Product |
| Description | Pressure to add recording/export/cloud features before the core loop works |
| Probability | Unlikely (2) |
| Impact | Moderate (3) |
| Risk Score | **6** — Medium |
| Owner | Engineering |
| Status | Monitoring |

**Mitigation:** Explicit non-goals in [PROJECT_SCOPE.md](PROJECT_SCOPE.md); roadmap discipline in [ROADMAP.md](implementation/ROADMAP.md).

**Contingency:** Re-scope via the change impact process.

**Triggers:** Feature requests outside MVP accepted without analysis.

**Review Date:** 2026-08-31

---

### Risk R-006: Loopback latency introduced by audio processing

| Attribute | Value |
|---|---|
| Category | Technical |
| Description | Buffering and resampling can add latency if sized incorrectly |
| Probability | Possible (3) |
| Impact | Minor (2) |
| Risk Score | **6** — Medium |
| Owner | Engineering |
| Status | Mitigating |

**Mitigation:** Small capture buffer; latency timestamps from capture to render; measured in Slice 6.

**Contingency:** Reduce buffer size / process audio in shorter chunks.

**Triggers:** Measured latency approaching 1 s.

**Review Date:** 2026-08-31

---

### Risk R-007: Argos translation latency/quality does not meet the target

| Attribute | Value |
|---|---|
| Category | Technical |
| Description | Python-based Argos process startup, per-pair model installs, and pivoting through an intermediate language add latency and may hurt quality; unmeasured until Slice 3/6 |
| Probability | Possible (3) |
| Impact | Moderate (3) |
| Risk Score | **9** — Medium |
| Owner | Engineering |
| Status | Mitigating |

**Mitigation:** Isolate Argos behind a local process with a simple line protocol; keep the process warm across caption sessions; defer model/pair selection until benchmarking (per ADR-0006); abstraction allows engine swap without pipeline changes.

**Contingency:** Prefer direct pairs over pivoting; cache warm Argos processes; document measured latency/quality as the accepted values.

**Triggers:** Slice 3 or Slice 6 benchmark shows translation latency or quality below the acceptable bar for sentence-length captions.

**Review Date:** 2026-08-31

---

## Risk Categories

| Category | Description | Typical Risks |
|---|---|---|
| Technical | Architecture, technology, code quality | Loopback limits, resampler quality, device variance |
| Security | Vulnerabilities, breaches, compliance | Silent capture, persistence, exfiltration |
| Product | Requirements, market fit, user adoption | Privacy perception, scope creep |
| Schedule | Timeline, delivery, dependencies | Toolchain setup, model availability |

## Risk Response Strategies

| Strategy | When to Use | Used For |
|---|---|---|
| **Avoid** | High probability and high impact | R-004 (privacy: local-only, no persistence) |
| **Mitigate** | Moderate to high risk, feasible to reduce | R-001, R-002, R-003, R-006, R-007 |
| **Accept** | Low probability or low impact | — |
| **Transfer** | When transfer is cost-effective | — |
| **Escalate** | Beyond project authority | — |

## Risk Status Definitions

Identified / Mitigating / Monitoring / Accepted / Realized / Closed

## Risk Review Process

1. **Identify** — during planning, reviews, and retrospectives
2. **Assess** — probability and impact using the matrix above
3. **Assign** — an owner for each risk
4. **Respond** — mitigation and contingency plans
5. **Monitor** — risks reviewed at each slice completion
6. **Close** — risks that no longer apply

## Risk Register Checklist

- [x] All identified risks are documented with probability and impact scores
- [x] Risk scores calculated using the assessment matrix
- [x] Owners assigned
- [x] Mitigation plans exist for medium, high, and critical risks
- [x] Contingency plans exist for high risks
- [x] Risks reviewed at the defined cadence
- [x] New risks added when identified
