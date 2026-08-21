# Universal Live Captions Repository Standards

Last updated: 2026-08-21

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define the canonical repository structure, naming conventions, import rules, and file organization |
| Scope | The entire repository file structure |
| Audience | All engineers and AI agents |
| Owner | Engineering |
| Status | Active |
| Related Documents | [PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md), [AGENT_DECISION_POLICY.md](AGENT_DECISION_POLICY.md) |

---

## 1. Allowed Top-Level Directories

The project root may contain only the following top-level directories:

```text
docs/      # All project documentation
src/       # Application source code (.NET projects)
tests/     # Test projects (.NET xUnit)
```

### Additional Allowed Root Files

- `README.md`
- `CLAUDE.md`
- `LICENSE`
- `.gitignore`
- `.editorconfig`
- `Directory.Build.props`
- `UniversalCaptions.slnx`
- `global.json`
- Tool configuration files

No other top-level directories may be created unless explicitly approved by the user or defined in an ADR.

---

## 2. Source Code Layout

```
src/
  UniversalCaptions.Core/          # Pure interfaces, models, events (no NAudio/WPF dependencies)
  UniversalCaptions.Audio/         # WASAPI loopback capture, buffering, resampling, VAD, meters
  UniversalCaptions.Speech.Gemini/ # Gemini Live engine (ILiveAudioTranslationEngine); contracts live in Core
  UniversalCaptions.Captions/      # Caption state and service
  UniversalCaptions.App/           # WPF control window + caption overlay + pipeline composition
  UniversalCaptions.Diagnostics/   # Diagnostic console apps (audio meter, etc.)

tests/
  UniversalCaptions.Audio.Tests/   # xUnit tests for the Audio project
  UniversalCaptions.Speech.Gemini.Tests/ # xUnit tests for the Speech.Gemini project
  UniversalCaptions.Captions.Tests/ # xUnit tests for the Captions project
  UniversalCaptions.App.Tests/     # xUnit tests for the App project
```

### Project Dependency Rules

| Project | May Reference | Must NOT Reference |
|---|---|---|
| `UniversalCaptions.Core` | Nothing in the repo | Everything else |
| `UniversalCaptions.Audio` | Core | Speech.Gemini, Captions, App |
| `UniversalCaptions.Speech.Gemini` | Core | Audio, Captions, App |
| `UniversalCaptions.Captions` | Core | Audio, Speech.Gemini, App |
| `UniversalCaptions.App` | Core, Audio, Speech.Gemini, Captions | — |
| `UniversalCaptions.Diagnostics` | Core, Audio | App |
| `tests/*` | Their target project (+ Core) | Projects they do not test |

**Documented architectural test-dependency exception:** a test project may additionally reference
a non-target production project when it must exercise an explicitly documented architectural
boundary or integration contract. Such dependencies must be documented by the relevant ADR and
listed here. Current exceptions:

- `UniversalCaptions.Speech.Gemini.Tests` → `UniversalCaptions.Audio`: the Gemini spike's
  `WavLoader` consumes ADR-0010's canonical audio boundary (`CanonicalAudioBoundary`) instead of
  maintaining spike-local resampling/down-mixing. Rationale: ADR-0010 requires every Gemini/STT
  consumer to receive canonical mono float32/16 kHz audio from `UniversalCaptions.Audio`; the
  spike is a consumer and must not re-implement conversion.

- `Core` must remain a pure contract layer: no NAudio, no WPF, no third-party packages.
- Audio capture implementations depend on NAudio **inside** `UniversalCaptions.Audio` only.
- No circular project references.

---

## 3. File Naming Conventions

### Documentation Files
- All doc files: `UPPER_SNAKE_CASE.md`
- ADR files: `ADR-NNNN.md` (zero-padded, e.g., `ADR-0001.md`)
- Exception: `README.md` and `CLAUDE.md` remain lowercase

### Source Files
- C# types and members: PascalCase
- Source file name matches the primary public type it declares
- Test classes: `{TypeUnderTest}Tests`
- One primary public type per file unless the types form an inseparable value cluster

### Directory Naming
- Project directories: PascalCase, matching the assembly name

---

## 4. Import Rules

- `using` directives follow the ordering enforced by the IDE (System, then third-party, then project)
- Test files may import from the module under test directly
- Internal modules must not be re-exported through public API surfaces

---

## 5. Dependency Boundaries

- `UniversalCaptions.Core` must not reference NAudio, WPF, or any third-party package
- `UniversalCaptions.Audio` must not reference `UniversalCaptions.Speech.Gemini` or `UniversalCaptions.App`
- No Gemini/vendor API may appear in Core interfaces (see ADR-0003, ADR-0006, ADR-0011)
- Third-party packages are only added with a `TECH_STACK.md` entry and, when consequential, an ADR

---

## 6. Generated File Locations

| File Type | Location |
|---|---|
| Build output | `src/*/bin/`, `src/*/obj/`, `tests/*/bin/`, `tests/*/obj/` |
| Test results | `TestResults/` |
| Coverage reports | `coverage/` |
| IDE configuration | `.vscode/`, `.idea/` |

Generated files are ignored via `.gitignore` and must never be committed.

---

## 7. Temporary File Policy

- Temporary files, scratch notes, debugging output, and draft documents must never be committed
- Use the system's temp directory for ephemeral files
- If a temporary file must exist in the working tree, add it to `.gitignore` and prefix the name with `_` (e.g., `_scratch.md`)
- Clean up all temporary files before committing

---

## 8. Configuration Files

Configuration lives at the project root:

```text
.editorconfig
.gitignore
Directory.Build.props
UniversalCaptions.slnx
global.json
```

Per-project overrides live in the project directory (`.csproj`).

---

## 9. Enforcement

- Code review must verify that files are placed in approved locations
- Violations of the top-level directory rule must be fixed before merge
- Violations of project dependency boundaries are blocking findings
- Creating files outside the approved structure without approval is a Level 3 violation under [AGENT_DECISION_POLICY.md](AGENT_DECISION_POLICY.md)
