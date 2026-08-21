# Universal Live Captions Agent Decision Policy

Last updated: 2026-07-31

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define what AI agents may decide autonomously, what requires user approval, and what is prohibited |
| Scope | All AI agent decisions made during project work |
| Audience | AI coding agents contributing to this repository |
| Owner | Engineering |
| Status | Active |
| Related Documents | [PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md), [AI_ENGINEERING_GUIDELINES.md](AI_ENGINEERING_GUIDELINES.md), [REPOSITORY_STANDARDS.md](REPOSITORY_STANDARDS.md) |

---

## Authority Levels

Every decision an AI agent makes falls into one of five authority levels:

| Level | Label | What It Means |
|---|---|---|
| 1 | **May Decide** | Proceed without asking. Document the decision if it has downstream impact. |
| 2 | **May Infer** | Infer from available context (design files, tokens, existing conventions, domain logic). Record the inference, its confidence, and the source it was derived from. |
| 3 | **Should Confirm** | Proceed with a best guess but flag the decision for user review at the next natural checkpoint. Do not block on it unless the decision is a prerequisite for other work. |
| 4 | **Must Ask** | Present options with tradeoffs. Wait for explicit approval before proceeding. |
| 5 | **Must Not Decide** | Never act on this. Refer to the designated authority (human, document, or tool). |

### How to Choose

1. Is it explicitly documented in project materials (PRD, architecture, ADRs)? → **May Decide**
2. Can it be derived from existing context with high confidence (existing conventions, ADR decisions, architecture)? → **May Infer**
3. Can it be guessed with reasonable confidence but the guess affects downstream work? → **Should Confirm**
4. Is it a technology choice, cost-impacting decision, or architectural fork with no clear winner? → **Must Ask**
5. Is it a business rule, requirement change, or legal/privacy commitment? → **Must Not Decide**

---

## Level 1 — May Decide

### Code Style and Formatting
- Indentation, whitespace, and line length within project conventions
- Locally scoped variable, function, and class names following existing conventions
- Import ordering and namespace layout

### File Organization (Within Rules)
- Placement of new files within the approved folder structure (see [REPOSITORY_STANDARDS.md](REPOSITORY_STANDARDS.md))
- Module and component decomposition within the approved architecture
- Test file location matching the project's testing conventions

### Implementation Details
- Helper functions and utilities that do not affect public interfaces
- Loop vs. LINQ choices for internal logic
- Error message wording for internal/non-user-facing errors
- Local variable naming in scoped contexts

### Tooling and Configuration
- Editor configuration (`.editorconfig`, IDE settings)
- Linting/analysis rule selection within the agreed standard
- Patch-level dependency updates within approved packages

---

## Level 2 — May Infer

| Context Available | May Infer | Must Document |
|---|---|---|
| ADR-0001 (native stack) | Compatible packages within the .NET/NAudio ecosystem | Source ADR, inferred package |
| ADR-0003 (STT abstraction) | Engine-neutral naming for transcript types and events | Source ADR, inferred type name |
| ARCHITECTURE.md pipeline | Module boundaries for each pipeline stage | Source section, inferred project |
| Existing codebase conventions | Repeating patterns, naming style, module structure | Source file pattern |
| PRD requirements | Feature-appropriate controls for the caption UI | Source requirement |

Document every inference: "Inferred `AudioChunk` as the pipeline carrier from ARCHITECTURE.md data-flow section (high confidence)".

---

## Level 3 — Should Confirm

The agent may proceed with a best guess but must flag it for user review at the next natural checkpoint.

### Minor Technology Choices
- Exact NuGet package when multiple fit the approved stack (e.g., which DI container, which test double library)
- Which speech provider to wrap behind the live-engine abstraction (ADR-0011 settled this: Gemini Live; a change requires a new ADR)

### Default Values
- VAD thresholds, audio buffer sizes, meter update rate
- Overlay default opacity, font size, and position

### Implementation Approach
- Class/file naming when the convention is not explicit
- Internal module boundaries when the architecture is high-level

Proceed with the most reasonable default and flag it for review (marker or note in the change report).

---

## Level 4 — Must Ask

The agent MUST stop and ask the user before deciding any of the following:

### Architecture
- Changing the approved native stack (.NET 8, WPF, NAudio) from [TECH_STACK.md](TECH_STACK.md)
- Adding a new speech recognition/translation provider or switching away from the Gemini-only pipeline (ADR-0011)
- Introducing a database, web service, or additional cloud dependency
- Choosing a new major framework or UI technology

### Infrastructure / Distribution
- Deployment or packaging platform beyond the local `dotnet publish` installer path
- Signing certificates or store distribution
- Managed services with ongoing cost

### Security
- Encryption strategy for anything beyond in-memory handling
- Exposing captured audio or transcripts outside the process

### Cost-Impacting Decisions
- Any decision expected to incur ongoing operational cost

---

## Level 5 — Must Not Decide

### Business Rules and Privacy
- Changing capture/privacy behavior described in [PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md) Section 10
- Enabling microphone capture or cloud STT without explicit user instruction
- Determining what constitutes a "complete" feature beyond the Definition of Done

### Requirements
- Adding features not in [PRD.md](PRD.md) or the approved roadmap
- Removing or deprecating requirements
- Modifying acceptance criteria

### Project Direction
- Changing project goals or objectives
- Determining release readiness without following [RELEASE_PLAN.md](implementation/RELEASE_PLAN.md)
- Creating new top-level directories outside the canonical structure

### Documentation Structure
- Creating documentation files outside the approved [ARTIFACT_REGISTRY.md](ARTIFACT_REGISTRY.md) without a new registry entry
- Changing the location of canonical documents
- Duplicating content across documents

### Financial and Legal
- Signing up for paid services
- Accepting terms of service or license agreements
- Making commitments on behalf of the project or organization

---

## Escalation Protocol

### Level 2 (May Infer) — Redocument
1. **Identify** the decision and its context
2. **Trace** the inference chain (source → transformation → conclusion)
3. **Assess** confidence (High / Medium / Low)
4. **Document** the inference in the relevant project document
5. **Proceed** with the inferred decision

### Level 3 (Should Confirm) — Flag
1. **Identify** the decision and its context
2. **Choose** the most reasonable default
3. **Flag** the decision for review
4. **Proceed** with implementation
5. **Present** all flags at the next natural checkpoint

### Level 4 (Must Ask) — Escalate
1. **Identify** the decision and its context
2. **List options** with clear tradeoffs (cost, complexity, performance, maintainability)
3. **Recommend** a preferred option with rationale
4. **Wait** for explicit user selection or approval
5. **Proceed** only after receiving approval

### Level 5 (Must Not Decide) — Stop
1. **Stop** the current action
2. **Explain** why the action is outside authority
3. **Suggest** the correct path (document to update, person to contact, tool to use)

---

## Exceptions

The user may override any level assignment verbally or in writing during a session. Overrides last only for the current session unless recorded in this document.

Overrides are recorded here:

| Date | Decision | Override | Authorized By |
|---|---|---|---|
| 2026-07-31 | None (initial) | N/A | N/A |

---

## Compliance

- Violations of Level 5 are considered blocking review findings
- Repeated Level 5 violations must be escalated
- If unclear whether a decision is Level 2 or Level 3, treat it as Level 3 (flag)
- If unclear whether a decision is Level 3 or Level 4, treat it as Level 4 (ask)
- Inference documentation is reviewed for reasonableness during code review — an implausible inference is a non-blocking finding
