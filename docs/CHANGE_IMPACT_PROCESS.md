# Universal Live Captions Change Impact Process

Last updated: 2026-07-31

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define the process for analyzing the impact of a change before implementation begins |
| Scope | Every feature, fix, refactor, or infrastructure change |
| Audience | All engineers and AI agents |
| Owner | Engineering |
| Status | Active |
| Related Documents | [PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md), [AGENT_DECISION_POLICY.md](AGENT_DECISION_POLICY.md), [ARCHITECTURE.md](ARCHITECTURE.md), [AI_ENGINEERING_GUIDELINES.md](AI_ENGINEERING_GUIDELINES.md), [QUALITY_ASSURANCE.md](QUALITY_ASSURANCE.md), [SECURITY_PLAN.md](SECURITY_PLAN.md) |

---

## No Silent Assumptions Policy

When a requirement, design decision, or implementation detail cannot be derived from:

- The user's explicit instructions
- The project's documentation (PRD, scope, architecture, ADRs)
- The existing codebase

the agent must **either**:

1. **Ask** the user for clarification before proceeding, **OR**
2. **Document the assumption** for approval before acting on it, **OR**
3. **Defer** the decision and mark it as a gap in the relevant document

The agent must **never silently invent** business rules, requirements, implementation behavior, API behavior, configuration values, or design decisions.

### Applying the Policy

| Situation | Required Action |
|---|---|
| A requirement is ambiguous | Ask user for clarification |
| An edge case is not specified | Ask user or document the assumption |
| A configuration value is unknown | Document the assumption with a recommended default |
| An API behavior is undocumented in the project | Check the library or framework docs — if still unclear, ask |
| A business rule is needed but not specified | Stop — this is a Level 3 decision (see AGENT_DECISION_POLICY.md) |
| An implementation approach has tradeoffs | Ask user or document the chosen approach with rationale |

### Documenting Assumptions

When documenting an assumption, record it in the relevant project document:

1. Add the assumption to `PROJECT_SCOPE.md` under "Assumptions"
2. Note it in the implementation plan or ADR if consequential
3. Do not treat assumed behavior as verified — mark it as assumed

---

## When to Perform Change Impact Analysis

Perform a change impact analysis before implementing any change that:

- Adds a new feature or modifies an existing one
- Introduces a new dependency or infrastructure component
- Changes capture, speech, or privacy behavior
- Changes the deployment, packaging, or release process
- Refactors a non-trivial module or component
- Changes how data flows through the system

Simple changes (typo fixes, trivial refactors, documentation updates) do not require a formal analysis.

---

## Change Impact Analysis Template

Before implementing any change, complete the analysis and record it in `docs/CHANGE_IMPACT_ANALYSIS.md` (append each change; keep the file current).

### 1. Change Summary

```text
Change Title:
Change Type:        (Feature / Fix / Refactor / Infrastructure / Documentation / Other)
Requirement Source: (PRD section / User request / Bug report / ADR / Other)
Priority:           (Critical / High / Medium / Low)
Estimated Effort:
```

### 2. Affected Modules

- [ ] List each affected project/class

### 3. Affected APIs

- [ ] List affected public interfaces/classes

**API changes required:** None / Additive (backward-compatible) / Breaking (requires version bump)

### 4. Database Changes

Not applicable — this application has no database.

### 5. Security and Privacy Implications

- [ ] Capture behavior change
- [ ] Audio/transcript handling change
- [ ] New external communication
- [ ] Sensitive data handling
- [ ] Security review required: Yes / No

### 6. Test Updates Required

- [ ] Unit tests
- [ ] Integration tests
- [ ] Manual/device verification

### 7. Documentation Updates Required

- [ ] `PRD.md`
- [ ] `ARCHITECTURE.md`
- [ ] `TECH_STACK.md`
- [ ] `SECURITY_PLAN.md`
- [ ] `QUALITY_ASSURANCE.md`
- [ ] ADR required: Yes / No
- [ ] `CHANGELOG.md`

### 8. Dependencies and Risks

- [ ] Blocked by:
- [ ] Blocking:
- [ ] Risks identified:
- [ ] Mitigation plan:

### 9. Assumptions

| # | Assumption | Impact if Wrong | Source |
|---|---|---|---|
| 1 |  |  |  |

### 10. Open Questions

| # | Question | Asked Of | Status |
|---|---|---|---|
| 1 |  |  | Open / Answered / Deferred |

---

## Impact Analysis Decision

**Decision:** Proceed / Blocked / Requires Clarification

**Analysis performed by:** Engineering
**Date:**
