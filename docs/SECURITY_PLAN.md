# Universal Live Captions Security Plan

Last updated: 2026-07-31

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define security architecture, threat model, controls, privacy model, and verification requirements |
| Scope | All system components, data, and infrastructure |
| Audience | Engineering, Security, DevOps, Compliance |
| Owner | Engineering |
| Status | Active |
| Related Documents | [ARCHITECTURE.md](ARCHITECTURE.md), [DEPLOYMENT.md](DEPLOYMENT.md) |

---

## Threat Model

| # | Threat | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| T-1 | **Silent audio capture**: user is unaware audio is being captured | Medium | High (privacy) | Explicit capture indicator; explicit start action; never auto-start capture |
| T-2 | **Audio persistence**: raw audio written to disk without consent | Low | High (privacy) | No raw audio persistence by default (see Privacy Model) |
| T-3 | **Exfiltration**: audio/transcripts leave the machine unexpectedly | Low | High (privacy) | Local-only pipeline in MVP; cloud STT requires explicit config and on-screen disclosure |
| T-4 | **Microphone capture without consent** | Low | High (privacy) | Microphone capture is not implemented in the MVP and requires explicit enablement in any future release |
| T-5 | **Tricking the user** via overlay overlay/input interception | Low | Medium | Overlay click-through is opt-in; overlay is a normal WPF window |
| T-6 | **Supply chain**: compromised NuGet/whisper binaries | Low | High | Dependency review per QUALITY_ASSURANCE; models from official sources; hash verification in Slice 2 |
| T-7 | **Malicious transcript injection** feeding captions | Low | Low | Captions rendered as plain text in a non-interactive window |

## Assets and Trust Boundaries

- **Asset: captured audio** (in-memory, transient). Trust boundary: within the process; never persisted by default.
- **Asset: transcripts** (in-memory). Same boundary.
- **Asset: user configuration** — now documented and persisted (TD-005, 2026-08-05): per-user JSON at `%LocalAppData%\UniversalCaptions\settings.json` storing only the six UI-preference categories (audio source device id, speech language, translation on/off + target, overlay opacity/font size/click-through, overlay placement, overlay view state). **Never contains raw audio, transcripts, or engine/environment paths.** Writes are atomic (`.tmp` + `File.Move(overwrite)`); a corrupt file yields safe defaults and never blocks startup.
- The process is trusted; external inputs are limited to the STT engine output and audio device state.

## Authentication Risks

None — no accounts or authentication.

## Authorization Matrix

Not applicable — single-user local application with no roles.

## Tenant Isolation Strategy

Not applicable — single-user local application.

## Data Classification

| Classification | Examples | Storage Rules | Transmission Rules | Retention |
|---|---|---|---|---|
| Confidential | Captured system audio (may contain conversations, passwords spoken aloud, personal data) | Never persisted by default; in-memory only | Never transmitted in MVP (local STT only) | Not retained |
| Internal | Transcripts in caption state | In-memory caption history; cleared on stop | Never transmitted in MVP | Cleared on stop / session end |
| Public | App diagnostics without audio content (sample rates, device names) | Optional log output | n/a | Log lifespan |

## Privacy Model

1. **Capture is explicit and visible.** Audio capture only begins when the user clicks "Start Captions". While active, a capture indicator is always visible in the control window.
2. **No silent capture.** There is no scenario in which the app captures audio without an explicit user action in the same session.
3. **No raw audio persistence.** Raw PCM audio is held in memory transiently for processing and is never written to disk by default.
4. **Local STT preferred.** The MVP processes speech with a local Whisper engine; audio never leaves the machine.
5. **Cloud STT disclosure.** If a cloud engine is later enabled, the UI must explicitly communicate that audio leaves the machine, and it must be opt-in.
6. **Microphone excluded.** Microphone capture is not implemented in the MVP and requires explicit enablement in any future release.
7. **Clear stop action.** "Stop Captions" stops capture immediately and clears transient state.

Documented in [PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md) Section 10 as immutable policy.

## Secret Management

The MVP has no secrets. Cloud STT API keys (future) must be stored via the OS credential manager, never in source control.

## Input and File Upload Validation

Not applicable in the MVP — no file inputs. Whisper model files (Slice 2) must be validated for expected size/hash and path safety.

## Rate Limiting

Not applicable — no network endpoints in the MVP.

## Session and Cookie Security

Not applicable — no web sessions.

## CSRF Strategy

Not applicable.

## Security Headers

Not applicable.

## Dependency Scanning

| Tool | Schedule | Action on Finding |
|---|---|---|
| `dotnet list package --vulnerable` | On dependency changes and before release | Review; upgrade or document accepted risk |

## Static Analysis

| Tool | Scope | Gate |
|---|---|---|
| .NET analyzers (latest, warnings as errors) | All projects | Build |
| `dotnet format --verify-no-changes` | All projects | CI / pre-commit |

## Audit Logging

MVP: no persistent audit log. In-memory event log (capture start/stop, device errors) surfaced in the control window and diagnostic console.

## Backup and Recovery

Not applicable — no persistent data.

## Incident Response

Operational failures (device loss, engine failure) are handled at runtime with user-readable errors and recovery/retry, not a security incident process (MVP). Security incidents would be reported to the project owner.

## Security Test Cases

### MVP Security Tests

- [ ] **Capture indicator** — capture-active state is always reflected in the UI
- [ ] **No persistence** — running a capture session writes no audio or transcript files
- [ ] **Explicit start** — capture never begins without a user start action
- [ ] **Microphone isolation** — the app never opens a microphone capture endpoint
- [ ] **No network** — no outbound network calls in the MVP pipeline
- [ ] **Stop clears state** — stopping capture clears in-memory transcripts

## Release Security Checklist

- [ ] Dependency scan passed with no critical or high findings
- [ ] Static analysis passed with no security-related findings
- [ ] Privacy model reviewed against the shipped behavior
- [ ] Security test cases executed
- [ ] No secrets in the repository
