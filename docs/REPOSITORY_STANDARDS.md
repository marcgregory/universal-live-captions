# Universal Live Captions Repository Standards

Last updated: 2026-08-01

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
  UniversalCaptions.Speech/        # Speech engines (WhisperSpeechToTextEngine); contracts live in Core
  UniversalCaptions.Translation/   # Translation engines (ArgosTranslationEngine); contracts live in Core
  UniversalCaptions.Captions/      # Caption state and service (created in Slice 4)
  UniversalCaptions.App/           # WPF control window + caption overlay (created in Slice 5)
  UniversalCaptions.Diagnostics/   # Diagnostic console apps (audio meter, etc.)
  UniversalCaptions.Benchmarks/    # Whisper + Argos benchmark harness (Slices 2 and 3 deliverables)

tests/
  UniversalCaptions.Audio.Tests/   # xUnit tests for the Audio project
  UniversalCaptions.Speech.Tests/  # xUnit tests for the Speech project (Slice 2)
  UniversalCaptions.Translation.Tests/ # xUnit tests for the Translation project (Slice 3)
  UniversalCaptions.Captions.Tests/ # xUnit tests for the Captions project (Slice 4)
  UniversalCaptions.App.Tests/     # xUnit tests for the App project (Slice 5)
```

### Project Dependency Rules

| Project | May Reference | Must NOT Reference |
|---|---|---|
| `UniversalCaptions.Core` | Nothing in the repo | Everything else |
| `UniversalCaptions.Audio` | Core | Speech, Translation, Captions, App |
| `UniversalCaptions.Speech` | Core | Audio, Translation, Captions, App |
| `UniversalCaptions.Translation` | Core | Audio, Speech, Captions, App |
| `UniversalCaptions.Captions` | Core | Audio, Speech, Translation, App |
| `UniversalCaptions.App` | Core, Audio, Speech, Translation, Captions | — |
| `UniversalCaptions.Diagnostics` | Core, Audio | App |
| `UniversalCaptions.Benchmarks` | Core, Speech, Translation | Audio, Captions, App |
| `tests/*` | Their target project (+ Core) | Projects they do not test |

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
- `UniversalCaptions.Audio` must not reference `UniversalCaptions.Speech`, `UniversalCaptions.Translation`, or `UniversalCaptions.App`
- No audio/STT/translation vendor API may appear in Core interfaces (see ADR-0003 and ADR-0006)
- Third-party packages are only added with a `TECH_STACK.md` entry and, when consequential, an ADR

---

## 6. Generated File Locations

| File Type | Location |
|---|---|
| Build output | `src/*/bin/`, `src/*/obj/`, `tests/*/bin/`, `tests/*/obj/` |
| Test results | `TestResults/` |
| Coverage reports | `coverage/` |
| IDE configuration | `.vscode/`, `.idea/` |
| Whisper model binaries (dev) | `artifacts/models/` (git-ignored) |
| Argos venv + translation packages (dev) | `artifacts/argos/` (git-ignored) |

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
