# Universal Live Captions Artifact Registry

Last updated: 2026-07-31

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define every document artifact's owner and ensure each concept has exactly one authoritative source |
| Scope | All project documentation |
| Audience | All engineers, AI agents, and reviewers |
| Owner | Engineering |
| Status | Active |
| Related Documents | [PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md), [CLAUDE.md](../CLAUDE.md) |

---

## Rule

Every concept has exactly one authoritative document. An agent must update that document rather than creating a duplicate. When a new cross-cutting concern arises, either find an existing document that owns it or create a new entry in this registry (not a standalone file without governance).

---

## Artifact Ownership

| Artifact | Owner Document | Purpose | Created By |
|---|---|---|---|
| Requirements & Behavior | [PRD.md](PRD.md) | Define product behavior, user stories, and acceptance criteria | Bootstrap |
| Boundaries & Constraints | [PROJECT_SCOPE.md](PROJECT_SCOPE.md) | Define scope, non-goals, assumptions, and constraints | Bootstrap |
| System Design | [ARCHITECTURE.md](ARCHITECTURE.md) | Define system architecture, component boundaries, data flow | Bootstrap |
| Security Architecture | [SECURITY_PLAN.md](SECURITY_PLAN.md) | Define threat model, controls, privacy model, security test cases | Bootstrap |
| Quality Strategy | [QUALITY_ASSURANCE.md](QUALITY_ASSURANCE.md) | Define test types, evidence requirements, release criteria | Bootstrap |
| AI Development Standards | [AI_ENGINEERING_GUIDELINES.md](AI_ENGINEERING_GUIDELINES.md) | Define AI-assisted development standards | Bootstrap |
| Technology Selections | [TECH_STACK.md](TECH_STACK.md) | Record selected stack, packages, tools, rejected options | Bootstrap |
| Deployment | [DEPLOYMENT.md](DEPLOYMENT.md) | Define packaging, release process, rollback | Bootstrap |
| Risks | [RISK_REGISTER.md](RISK_REGISTER.md) | Track technical, product, schedule, security, infrastructure risks | Bootstrap |
| Product Backlog | [ROADMAP.md](implementation/ROADMAP.md) | Answer "What should be built?" | Bootstrap |
| Sprint Execution | [BUILD_PLAN.md](implementation/BUILD_PLAN.md) | Answer "How will we build it?" | Bootstrap |
| History | [CHANGELOG.md](implementation/CHANGELOG.md) | Record versioned changelog | Bootstrap |
| Cleanup List | [TECHNICAL_DEBT.md](implementation/TECHNICAL_DEBT.md) | Track technical debt items | Bootstrap |
| Release Criteria | [RELEASE_PLAN.md](implementation/RELEASE_PLAN.md) | Define finished | Bootstrap |
| Current Snapshot | [PROJECT_STATUS.md](implementation/PROJECT_STATUS.md) | Show current project state | Bootstrap |
| Test Evidence | [TEST_REPORT.md](reports/TEST_REPORT.md) | Record test execution evidence | Bootstrap |
| Engineering Rules & AI Handoff | [CLAUDE.md](../CLAUDE.md) | Engineering rules, commands, AI continuation handoff | Bootstrap |
| Project Map | [README.md](../README.md) | Project overview and documentation map | Bootstrap |
| Project Constitution | [PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md) | Immutable project rules | Bootstrap |
| Artifact Registry | [ARTIFACT_REGISTRY.md](ARTIFACT_REGISTRY.md) | Artifact governance | Bootstrap |
| Agent Decision Authority | [AGENT_DECISION_POLICY.md](AGENT_DECISION_POLICY.md) | AI agent decision boundaries | Bootstrap |
| Repository Standards | [REPOSITORY_STANDARDS.md](REPOSITORY_STANDARDS.md) | Folder layout, naming, import rules | Bootstrap |
| Bootstrap Validation | [BOOTSTRAP_VALIDATION.md](BOOTSTRAP_VALIDATION.md) | Self-validation checklist | Bootstrap |
| Change Impact | [CHANGE_IMPACT_PROCESS.md](CHANGE_IMPACT_PROCESS.md) | Pre-implementation impact analysis | Bootstrap |
| No Silent Assumptions | [CHANGE_IMPACT_PROCESS.md](CHANGE_IMPACT_PROCESS.md) (Section "No Silent Assumptions Policy") | Requirement derivation policy | Bootstrap |
| Architectural Decisions | [ADR-*.md](adr/ADR-0001.md) | Consequential decision records | Bootstrap + Ongoing |
| Audio Capture Model | [ARCHITECTURE.md](ARCHITECTURE.md) (Section "Audio Capture Model") | WASAPI loopback capture design | Bootstrap |
| Privacy Model | [SECURITY_PLAN.md](SECURITY_PLAN.md) (Section "Privacy Model") | How sensitive audio is handled | Bootstrap |
| Landing Page | `landing/` (governed top-level; see [PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md) §1) | Public product landing page (HTML/CSS/JS + assets) — the user-facing download surface tied to [RELEASE_PLAN.md](implementation/RELEASE_PLAN.md) | Bootstrap (de facto) + formalized 2026-08-07 |
| Installer Packaging | `packaging/` (governed top-level; see [PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md) §1) | Inno Setup `.iss` + `launcher.cmd` + `build-package.ps1` that produce `UniversalCaptions-Setup-*.exe` — governed like code; `output/` is gitignored build output | Bootstrap (de facto) + formalized 2026-08-07 |
| Local Dev Artifacts | `artifacts/` (governed top-level; see [PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md) §1) | Gitignored developer-only caches — Whisper models, sample audio, Argos venv + packages, benchmark report outputs. The directory is canonical; its contents are not release-tracked. | Bootstrap (de facto) + formalized 2026-08-07 |
| Credentials (Gemini API key, Windows Credential Manager) | [SECURITY_PLAN.md](SECURITY_PLAN.md) (Section "Secret Management") + [adr/ADR-0009.md](adr/ADR-0009.md) | One row per secret class; classification lives in SECURITY_PLAN, lifecycle/invariants live in ADR-0009. The raw value never appears in any document. | 2026-08-08 |

---

## Concept-to-Document Mapping

When working on a concern, update only its owner document. Do not create parallel documentation tracks.

| Concern | Update This Document | Not These |
|---|---|---|
| A new feature requirement | PRD.md | BUILD_PLAN.md, ROADMAP.md |
| A new architectural decision | ADR-NNNN.md | ARCHITECTURE.md (unless architecture itself changes) |
| Sprint progress | PROJECT_STATUS.md | CHANGELOG.md |
| A bug fix | CHANGELOG.md (if released) | PROJECT_STATUS.md |
| Technical debt discovered | TECHNICAL_DEBT.md | BUILD_PLAN.md |
| A security finding | SECURITY_REVIEW.md (when it exists) | SECURITY_PLAN.md (update plan if control changes) |
| Test results | TEST_REPORT.md | QUALITY_ASSURANCE.md (update plan if strategy changes) |
| A risk discovered | RISK_REGISTER.md | BUILD_PLAN.md, RELEASE_PLAN.md (cross-reference only) |
| Release decision | RELEASE_PLAN.md | PROJECT_STATUS.md |
| Audio capture behavior | ARCHITECTURE.md | PRD.md |
| Privacy behavior change | SECURITY_PLAN.md | PRD.md |
| Landing-page copy or layout | `landing/index.html` + `landing/styles.css` + `landing/script.js` | PRD.md, RELEASE_PLAN.md (cross-reference only — readiness decision lives in RELEASE_PLAN.md) |
| Installer build configuration | `packaging/UniversalCaptions.iss` + `packaging/launcher.cmd` + `packaging/build-package.ps1` | RELEASE_PLAN.md (cross-reference only — readiness decision lives in RELEASE_PLAN.md) |
| Credential lifecycle / API-key handling | `adr/ADR-0009.md` + `SECURITY_PLAN.md` (Section "Secret Management") | PRD.md, RELEASE_PLAN.md (cross-reference only — provider toggle ships in v0.5.30, see ADR-0009 §Implementation Outline) |

---

## Enforcement

- During code review, verify that the correct owner document was updated
- If content about a concept exists in more than one document, file a cleanup task to deduplicate
- New document proposals must include a registry entry and a justification for why no existing owner document can absorb the concern
