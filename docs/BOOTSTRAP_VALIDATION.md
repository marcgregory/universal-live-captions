# Universal Live Captions Bootstrap Validation

Last updated: 2026-07-31

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Verify all required artifacts are generated, consistent, and complete before declaring the bootstrap finished |
| Scope | The complete bootstrap output |
| Audience | The agent performing the bootstrap |
| Owner | Engineering |
| Status | Active |
| Related Documents | [PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md), [ARTIFACT_REGISTRY.md](ARTIFACT_REGISTRY.md) |

---

## Instructions

Run this validation pass after generating all bootstrap documents. Record results in `docs/BOOTSTRAP_VALIDATION_REPORT.md`. Every check must pass or be explicitly waived with a documented reason before the bootstrap is declared complete.

---

## Required Templates Generated

- [ ] `README.md` exists and maps the project documentation
- [ ] `CLAUDE.md` exists and includes all required engineering rules
- [ ] `docs/PRD.md` exists with functional and non-functional requirements
- [ ] `docs/PROJECT_SCOPE.md` exists with scope, non-goals, assumptions, and constraints
- [ ] `docs/ARCHITECTURE.md` exists with system design, boundaries, and data flow
- [ ] `docs/DOMAIN_MODEL.md` intentionally omitted (MVP desktop app, no domain model required)
- [ ] `docs/SECURITY_PLAN.md` exists
- [ ] `docs/DATA_GOVERNANCE.md` intentionally omitted (MVP, no persistent data stores)
- [ ] `docs/QUALITY_ASSURANCE.md` exists
- [ ] `docs/CODE_REVIEW.md` intentionally omitted (MVP — review workflow documented in AI_ENGINEERING_GUIDELINES.md and CLAUDE.md)
- [ ] `docs/AI_ENGINEERING_GUIDELINES.md` exists
- [ ] `docs/API_STANDARDS.md` intentionally omitted (no server API)
- [ ] `docs/DATABASE_STANDARDS.md` intentionally omitted (no database)
- [ ] `docs/TECH_STACK.md` exists with selected stack and rejected alternatives
- [ ] `docs/DEPLOYMENT.md` exists
- [ ] `docs/OBSERVABILITY.md` intentionally omitted (MVP)
- [ ] `docs/MONITORING.md` intentionally omitted (MVP)
- [ ] `docs/INCIDENT_RESPONSE.md` intentionally omitted (MVP)
- [ ] `docs/PERFORMANCE.md` intentionally omitted (MVP — latency instrumentation is documented in ARCHITECTURE.md)
- [ ] `docs/UX_REVIEW.md` intentionally omitted (MVP)
- [ ] `docs/RISK_REGISTER.md` exists
- [ ] `docs/FOUNDER_OS.md` intentionally omitted (not a commercial/founder-led project)
- [ ] `docs/PROJECT_CONSTITUTION.md` exists
- [ ] `docs/ARTIFACT_REGISTRY.md` exists
- [ ] `docs/AGENT_DECISION_POLICY.md` exists
- [ ] `docs/REPOSITORY_STANDARDS.md` exists
- [ ] `docs/BOOTSTRAP_VALIDATION.md` exists (this document)
- [ ] `docs/CHANGE_IMPACT_PROCESS.md` exists
- [ ] `docs/adr/README.md` exists
- [ ] `docs/implementation/ROADMAP.md` exists
- [ ] `docs/implementation/BUILD_PLAN.md` exists
- [ ] `docs/implementation/CHANGELOG.md` exists
- [ ] `docs/implementation/TECHNICAL_DEBT.md` exists
- [ ] `docs/implementation/RELEASE_PLAN.md` exists
- [ ] `docs/implementation/PROJECT_STATUS.md` exists
- [ ] `docs/reports/TEST_REPORT.md` exists
- [ ] `docs/reports/SECURITY_REVIEW.md` intentionally omitted (MVP)
- [ ] `docs/reports/CODE_REVIEW_REPORT.md` intentionally omitted (MVP)

---

## Required ADRs Generated

- [ ] Technology stack ADR exists (ADR-0001)
- [ ] Audio capture architecture ADR exists (ADR-0002)
- [ ] Speech-to-text abstraction ADR exists (ADR-0003)
- [ ] Overlay/UI architecture ADR exists (ADR-0004)
- [ ] Testing strategy ADR exists (ADR-0005)
- Authentication, authorization, API style, state management, database, realtime transport, and background job ADRs: not applicable (local desktop application, no server, no auth, no database)

---

## Cross-Reference Validation

- [ ] All cross-references between documents are valid (referenced files exist)
- [ ] No broken `](` paths in any markdown file
- [ ] `CLAUDE.md` references match actual file paths
- [ ] `ARCHITECTURE.md` cross-references are valid

---

## No Duplicate Planning Documents

- [ ] No document duplicates another document's purpose (see ARTIFACT_REGISTRY.md)
- [ ] Product requirements exist only in `PRD.md`
- [ ] Sprint execution exists only in `BUILD_PLAN.md`
- [ ] Backlog exists only in `ROADMAP.md`
- [ ] History exists only in `CHANGELOG.md`
- [ ] Current state exists only in `PROJECT_STATUS.md`
- [ ] Technical debt exists only in `TECHNICAL_DEBT.md`
- [ ] Release criteria exists only in `RELEASE_PLAN.md`

---

## Folder Structure Validation

- [ ] Only approved top-level directories exist (`docs/`, `src/`, `tests/`)
- [ ] `docs/adr/` contains only ADR files (`README.md`, `ADR-*.md`)
- [ ] `docs/implementation/` contains only planning files
- [ ] `docs/reports/` contains only report files
- [ ] No files exist in unexpected locations

---

## Naming Convention Validation

- [ ] Documentation files follow `UPPER_SNAKE_CASE.md` naming
- [ ] ADR files follow `ADR-NNNN.md` naming with zero-padded numbers
- [ ] No files have spaces, special characters, or inconsistent casing in their names

---

## Technology Consistency

- [ ] Stack in `ARCHITECTURE.md` matches `TECH_STACK.md`
- [ ] Stack in `DEPLOYMENT.md` matches `TECH_STACK.md`
- [ ] Stack in `CLAUDE.md` dev commands matches `TECH_STACK.md`
- [ ] All ADRs reference the same stack decisions
- [ ] No document references a technology not in `TECH_STACK.md`

---

## Traceability

- [ ] Functional requirements in `PRD.md` map to user stories and acceptance criteria
- [ ] Non-functional requirements map to quality gates and performance targets
- [ ] User stories map to sprint tasks in `BUILD_PLAN.md`
- [ ] Risks in `RISK_REGISTER.md` are reflected in `RELEASE_PLAN.md` or `BUILD_PLAN.md` where relevant
- [ ] Security requirements map to security test cases in `SECURITY_PLAN.md`
- [ ] Test strategy in `QUALITY_ASSURANCE.md` maps to test commands in `CLAUDE.md`

---

## Documentation Metadata

- [ ] Every generated document has a metadata table with Purpose, Scope, Audience, Owner, Status, and Related Documents
- [ ] Every metadata table has no empty fields
- [ ] Every document's Status field is consistent (Active/Approved)

---

## Internal Link Validation

- [ ] All relative paths in cross-references resolve correctly from the document's location
- [ ] Documents in `docs/` reference sibling docs relative to their location
- [ ] Documents in `docs/implementation/` use `../` to reference docs in `docs/`
- [ ] Documents in `docs/adr/` use `../` to reference docs in `docs/`

---

## Content Quality

- [ ] No generated file contains `{{` or `}}`
- [ ] No generated file contains `TODO`, `TBD`, or `Lorem ipsum` unless explicitly requested
- [ ] Every generated markdown document contains project-specific content instead of generic filler
- [ ] `ROADMAP.md` contains at least one completed phase and one active sprint
- [ ] `BUILD_PLAN.md` contains a fully written Sprint 1 (Slice 1) with goal, scope, dependencies, tasks, Definition of Done, acceptance criteria, and demo
- [ ] `PROJECT_STATUS.md` reflects the current project state, sprint, progress, focus, blockers, next milestone, and last updated date
- [ ] `CLAUDE.md` contains project-specific commands, architecture, engineering rules, current sprint, known gaps, and next implementation priority
- [ ] `PRD.md` contains actual requirements, user stories, acceptance criteria, metrics, and risks
- [ ] ADRs contain context, decision, consequences, and alternatives considered

---

## Bootstrap Integrity Checks

### No Silent Self-Repair
- [ ] All bootstrap violations were recorded as findings before any fix was applied
- [ ] No violation was silently repaired without an audit trail
- [ ] Blocking violations stopped the bootstrap until resolved

### No Bootstrap Drift
- [ ] All files were created within the approved top-level structure
- [ ] No unapproved directories (`planning/`, `notes/`, `analysis/`, `spec/`, `todo/`, etc.) were created
- [ ] No concept has duplicate authoritative documents
- [ ] All placeholder content was expanded — the bootstrap did not commit its own template syntax
- [ ] Agent Decision Policy authority levels were respected during generation
- [ ] Required approvals (technology selection, architecture) were obtained (user approved .NET 8 + WPF + NAudio + local Whisper behind ISpeechToTextEngine on 2026-07-31)

### Findings Separation

#### Bootstrap Findings

| # | Category | Description | Rule Violated | Severity | Fixed |
|---|---|---|---|---|---|
| B-001 | Baseline | The task prompt described an "existing repository using the bootstrap", but the working directory was empty; the bootstrap was instantiated from the installable skill rather than an existing bootstrap. No governance was bypassed — the missing bootstrap was the baseline condition, not a self-repair. | None | Low | N/A |
| B-002 | Structure | No violations found during generation. | — | — | — |

**Total Bootstrap Findings:** 1
- Critical: 0
- High: 0
- Medium: 0
- Low: 1

#### Project Findings

| # | Category | Description | Severity | Fixed |
|---|---|---|---|---|
| P-001 | Validation | Slice 1's WASAPI loopback capture must be verified against real Windows audio (a manual diagnostic run), which cannot be fully automated in a unit test. | Medium | No (manual check in TEST_REPORT.md) |

**Total Project Findings:** 1

---

## Clarification Quality

| Metric | Count | Notes |
|---|---|---|
| Questions asked (Level 4 — Must Ask) | 2 | Tech stack approval + STT engine approval |
| Inferences made (Level 2 — May Infer) | 1 | MVP complexity classification inferred from desktop-app requirements |
| Confirmations flagged (Level 3 — Should Confirm) | 2 | VAD thresholds and buffer sizes (defaults flagged in Slice 1 code review) |
| Incorrect inferences (corrected by user) | 0 | — |
| Unnecessary interruptions (could have been inferred) | 0 | — |
| Silent assumptions (Level 5 violations) | 0 | — |
| Inference accuracy rate | 100% | `(1 - 0) / 1` |
| Interruption necessity rate | 100% | `(2 - 0) / 2` |

---

## Final Decision

Recorded in `docs/BOOTSTRAP_VALIDATION_REPORT.md`.

**Validation performed by:** opencode agent (deepseek-v4-flash-free)
**Date:** 2026-07-31
