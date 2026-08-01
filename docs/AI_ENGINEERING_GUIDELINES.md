# Universal Live Captions AI Engineering Guidelines

Last updated: 2026-07-31

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define the engineering standards and behaviors expected when AI agents contribute to this project |
| Scope | All AI-assisted development, code generation, testing, and documentation written by AI agents |
| Audience | AI coding agents contributing to this repository |
| Owner | Engineering |
| Status | Active |
| Related Documents | [CLAUDE.md](../CLAUDE.md), [QUALITY_ASSURANCE.md](QUALITY_ASSURANCE.md), [ARCHITECTURE.md](ARCHITECTURE.md) |

---

## No Silent Assumptions

This project enforces a **No Silent Assumptions** policy. See [CHANGE_IMPACT_PROCESS.md](CHANGE_IMPACT_PROCESS.md) for the full policy.

When a requirement, design decision, or implementation detail cannot be derived from the project's documentation or existing code:

- **Ask** the user for clarification
- **Document** the assumption for approval
- **Defer** the decision

Never silently invent business rules, requirements, API behavior, or design decisions.

## Core Principles

### 1. Never Hallucinate APIs or Libraries

- Only use APIs, functions, and libraries that are documented in the project's codebase, dependencies, or language standard library
- Verify NAudio/.NET API signatures against the actual installed package before use
- When introducing a new dependency, check that it is approved in [TECH_STACK.md](TECH_STACK.md) or explicitly discuss it first

### 2. Never Invent Business Rules

- Implement only the behavior documented in [PRD.md](PRD.md), [ARCHITECTURE.md](ARCHITECTURE.md), and acceptance criteria
- Do not add capture, privacy, or transcription behavior that is not specified
- If a requirement is ambiguous, ask for clarification rather than guessing

### 3. Do Not Fake Test Results

- Never mark a test as **Passed** unless the actual test command has been executed and produced a passing result
- Code inspection, successful compilation, or the existence of a test file does not count as execution evidence
- If tests cannot be run in the current environment, mark them as **Not Tested**
- Never claim latency or Windows 10 compatibility without measurement/verification

### 4. Reuse Before Creating

- Before creating a new component or abstraction, search the codebase for existing implementations
- Prefer extending existing patterns over introducing new ones

### 5. Avoid Duplicate Implementations

- Do not implement the same logic in multiple places
- Extract shared logic into the owning project (usually `UniversalCaptions.Core` for contracts)

### 6. Follow the Architecture

- Adhere to [ARCHITECTURE.md](ARCHITECTURE.md) and [REPOSITORY_STANDARDS.md](REPOSITORY_STANDARDS.md)
- Respect project dependency boundaries — never put NAudio or WPF code in `UniversalCaptions.Core`
- Do not bypass abstraction layers (e.g., calling NAudio directly from the UI)

### 7. Write Tests for Changed Code

- Every code change must include or update tests
- Hardware-dependent code is tested through fakes of the boundary interface

### 8. Keep Documentation in Sync

- Update documentation when the corresponding functionality changes
- At minimum update the artifact that owns the changed concept (see [ARTIFACT_REGISTRY.md](ARTIFACT_REGISTRY.md))

### 9. Scope Changes Appropriately

- Keep each change focused on a single concern
- Do not fix unrelated issues in the same change set
- Large changes are broken into smaller, reviewable increments

### 10. Respect Existing Code Style

- Match the surrounding code's style, naming conventions, and patterns
- Do not reformat or restructure code unless the change explicitly requires it

## Review Expectations

### Self-Review

Before submitting code for review, AI agents must:

1. Review their own output for correctness, completeness, and consistency
2. Verify the implementation matches the acceptance criteria
3. Run all applicable quality gates (`dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`)
4. Confirm no secrets, credentials, or placeholder content is committed
5. Check that documentation is updated

### Independent Review

AI-generated code is subject to the same review requirements as human-written code. Use a fresh review pass: the reviewer first reports findings without modifying the implementation.

## Prohibited Behaviors

- Inventing API signatures or library features
- Silently fixing "obvious" bugs in unrelated code during a change
- Marking checks as passed without execution evidence
- Adding placeholder implementations with `TODO`, `FIXME`, or `TBD`
- Generating code that is unreachable, dead, or redundant
- Modifying code outside the scope of the requested change without explicit permission
- Bypassing CI checks or testing requirements
- Committing without running the relevant tests
- Claiming Windows 10 or latency compatibility without verification

## Enforcement

These guidelines are enforced during code review. Findings related to guideline violations are categorized by severity (Blocker through Suggestion) per [QUALITY_ASSURANCE.md](QUALITY_ASSURANCE.md).
