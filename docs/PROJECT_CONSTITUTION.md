# Universal Live Captions Project Constitution

Last updated: 2026-07-31

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define immutable project rules, conventions, and boundaries |
| Scope | Every file, commit, and decision in this repository |
| Audience | All engineers, AI agents, and reviewers |
| Owner | Engineering |
| Status | Active |
| Related Documents | [REPOSITORY_STANDARDS.md](REPOSITORY_STANDARDS.md), [AGENT_DECISION_POLICY.md](AGENT_DECISION_POLICY.md), [ARTIFACT_REGISTRY.md](ARTIFACT_REGISTRY.md), [CLAUDE.md](../CLAUDE.md), [ARCHITECTURE.md](ARCHITECTURE.md) |

---

## Preamble

This constitution defines the immutable rules of this project. Every engineer — human or AI — must read and follow this document. No other document, template, or instruction may override the rules herein unless the constitution itself provides an amendment process.

---

## 1. Canonical Folder Structure

The project root may contain only these top-level directories:

```text
docs/      # All project documentation
src/       # Application source code (.NET projects)
tests/     # Test projects (.NET xUnit)
```

No additional top-level directories may be created unless explicitly approved by the user or defined in a documented ADR.

All source code lives under `src/`. Documentation lives under `docs/`. Configuration files (`Directory.Build.props`, `.editorconfig`, `.gitignore`) live at the project root unless a tool requires otherwise.

---

## 2. Canonical Documentation Locations

| Document | Location | Required By Complexity |
|---|---|---|
| Project Constitution | `docs/PROJECT_CONSTITUTION.md` | All levels |
| Artifact Registry | `docs/ARTIFACT_REGISTRY.md` | All levels |
| Agent Decision Policy | `docs/AGENT_DECISION_POLICY.md` | All levels |
| Repository Standards | `docs/REPOSITORY_STANDARDS.md` | All levels |
| Bootstrap Validation | `docs/BOOTSTRAP_VALIDATION.md` | All levels |
| Change Impact Process | `docs/CHANGE_IMPACT_PROCESS.md` | All levels |
| Product Requirements | `docs/PRD.md` | All levels |
| Project Scope | `docs/PROJECT_SCOPE.md` | All levels |
| Architecture | `docs/ARCHITECTURE.md` | All levels |
| Domain Model | `docs/DOMAIN_MODEL.md` | Production+ (omitted for this MVP) |
| Security Plan | `docs/SECURITY_PLAN.md` | MVP+ |
| Data Governance | `docs/DATA_GOVERNANCE.md` | Enterprise+ (omitted for this MVP) |
| Quality Assurance | `docs/QUALITY_ASSURANCE.md` | MVP+ |
| Code Review Guide | `docs/CODE_REVIEW.md` | Production+ (omitted for this MVP) |
| AI Engineering Guidelines | `docs/AI_ENGINEERING_GUIDELINES.md` | MVP+ |
| API Standards | `docs/API_STANDARDS.md` | Production+ (omitted for this MVP) |
| Database Standards | `docs/DATABASE_STANDARDS.md` | Production+ (omitted for this MVP) |
| Tech Stack | `docs/TECH_STACK.md` | All levels |
| Deployment | `docs/DEPLOYMENT.md` | MVP+ |
| Observability | `docs/OBSERVABILITY.md` | Production+ (omitted for this MVP) |
| Monitoring | `docs/MONITORING.md` | Enterprise+ (omitted for this MVP) |
| Incident Response | `docs/INCIDENT_RESPONSE.md` | Enterprise+ (omitted for this MVP) |
| Performance | `docs/PERFORMANCE.md` | Production+ (omitted for this MVP) |
| UX Review | `docs/UX_REVIEW.md` | Production+ (omitted for this MVP) |
| Risk Register | `docs/RISK_REGISTER.md` | MVP+ |
| ADR Index | `docs/adr/README.md` | All levels |
| Architecture Decision Records | `docs/adr/ADR-*.md` | All levels |
| Roadmap | `docs/implementation/ROADMAP.md` | All levels |
| Build Plan | `docs/implementation/BUILD_PLAN.md` | All levels |
| Changelog | `docs/implementation/CHANGELOG.md` | All levels |
| Technical Debt | `docs/implementation/TECHNICAL_DEBT.md` | All levels |
| Release Plan | `docs/implementation/RELEASE_PLAN.md` | All levels |
| Project Status | `docs/implementation/PROJECT_STATUS.md` | All levels |
| Test Report | `docs/reports/TEST_REPORT.md` | MVP+ |
| Security Review | `docs/reports/SECURITY_REVIEW.md` | Enterprise+ (omitted for this MVP) |
| Code Review Report | `docs/reports/CODE_REVIEW_REPORT.md` | Production+ (omitted for this MVP) |
| Change Impact Analysis | `docs/CHANGE_IMPACT_ANALYSIS.md` | When implementing changes |
| Bootstrap Validation Report | `docs/BOOTSTRAP_VALIDATION_REPORT.md` | After bootstrap |

---

## 3. Naming Conventions

### Files and Directories
- All documentation: `UPPER_SNAKE_CASE.md` (e.g., `PROJECT_CONSTITUTION.md`, `SECURITY_PLAN.md`)
- ADR files: `ADR-NNNN.md` (zero-padded, e.g., `ADR-0001.md`)
- Source code: C# language conventions (PascalCase types and members, camelCase locals/parameters)
- Test classes: `{TypeUnderTest}Tests`
- Configuration files: as required by the tooling standard

### Code Elements
- Follow the conventions established in [REPOSITORY_STANDARDS.md](REPOSITORY_STANDARDS.md)
- No ad-hoc naming variations — use the standard for the layer or module

---

## 4. Approved Architecture Patterns

Only these architectural patterns are approved:

| Pattern | Allowed When |
|---|---|
| Modular desktop application | Default for this project |
| Layered pipeline (Capture → Process → STT → Caption → Overlay) | Default data flow, see [ARCHITECTURE.md](ARCHITECTURE.md) |
| Event-driven pipeline stages | Streaming audio/transcript flow, see [ARCHITECTURE.md](ARCHITECTURE.md) |
| Microservices / serverless / web backends | Never — not applicable to this desktop application |

No architecture pattern may be used without appearing in either `ARCHITECTURE.md` or an ADR.

---

## 5. Coding Standards

- Every change must pass build, analysis, and tests before commit
- Every public interface, public class, and public member must have a doc comment
- Error messages must be user-facing or developer-actionable — never generic
- Secrets, credentials, and tokens must never be committed
- Dead code, commented-out code, and debug `Console.WriteLine` statements must not be committed
- Every implemented function must have at least one corresponding test
- Audio and STT code must never assume a specific vendor API inside shared abstractions (see ADR-0003)

---

## 6. Testing Policy

Test types required by this complexity level are defined in [QUALITY_ASSURANCE.md](QUALITY_ASSURANCE.md). The following rules are universal:

- No code change may be merged without tests that cover the changed behavior
- No test may be marked **Passed** without execution evidence
- Coverage requirements are defined per module in [QUALITY_ASSURANCE.md](QUALITY_ASSURANCE.md)
- Hardware-dependent boundaries (audio device, STT engine) are tested with fakes/fakes of the boundary interface; real-device verification is manual and recorded in `TEST_REPORT.md`
- Tests must be deterministic — flaky tests must be quarantined immediately

---

## 7. Documentation Governance

- Every concept has exactly one authoritative document (see [ARTIFACT_REGISTRY.md](ARTIFACT_REGISTRY.md))
- No document may duplicate content from another document — cross-reference instead
- Documentation must be updated in the same change set as the corresponding implementation
- Placeholder content (`{{ }}`, `TODO`, `TBD`, `Lorem ipsum`) is never committed
- Every document must have valid cross-references — no broken links

---

## 8. ADR Policy

- Every consequential architectural decision requires an ADR
- An ADR is consequential when it materially affects architecture, technology, security model, data model, deployment, or cost
- ADRs follow the template in [ADR_README.md](adr/README.md)
- Approved ADRs are immutable — a new ADR supersedes an old one; the old one is never edited
- ADRs are numbered sequentially: `ADR-0001.md`, `ADR-0002.md`, etc.

---

## 9. Release Policy

- Every release must pass the quality gates defined in [RELEASE_PLAN.md](implementation/RELEASE_PLAN.md)
- No release may be marked **Ready** without passing the required security review
- Breaking changes must be documented in the changelog and communicated via migration notes
- Releases follow semantic versioning (`MAJOR.MINOR.PATCH`)

---

## 10. Privacy Policy

System audio may contain sensitive information. The following rules are immutable:

- The application must clearly indicate when audio capture is active
- The application must never silently capture audio
- Raw audio must not be persisted by default
- Microphone audio is never captured unless explicitly enabled by the user
- Local speech recognition is preferred when practical
- If cloud speech recognition is ever enabled, the application must explicitly communicate that audio leaves the machine
- A clear stop-capture action must always be available

---

## 11. AI Agent Behavior

All AI agents contributing to this project must:

1. Read this constitution before generating any files
2. Read [AGENT_DECISION_POLICY.md](AGENT_DECISION_POLICY.md) before making any project decisions
3. Read [ARTIFACT_REGISTRY.md](ARTIFACT_REGISTRY.md) before creating or modifying any document
4. Read [CHANGE_IMPACT_PROCESS.md](CHANGE_IMPACT_PROCESS.md) before implementing any change
5. Read [REPOSITORY_STANDARDS.md](REPOSITORY_STANDARDS.md) before creating any file or directory
6. Follow [AI_ENGINEERING_GUIDELINES.md](AI_ENGINEERING_GUIDELINES.md) for all code generation
7. Never invent business rules, API signatures, or requirements
8. Never create files or directories outside the canonical structure without explicit approval
9. Never duplicate content across documents
10. Never mark work complete without validation evidence
11. Never claim Windows 10 compatibility without verification
12. Never claim a latency target is achieved unless measured

---

## Amendment Process

To amend this constitution:

1. Create an ADR documenting the proposed change
2. Record the rationale and expected impact
3. Obtain explicit user approval
4. Update this document
5. Update any documents that conflict with the amendment

---

## Ratification

This constitution is effective on 2026-07-31 and applies to all work in this repository from that date forward.
