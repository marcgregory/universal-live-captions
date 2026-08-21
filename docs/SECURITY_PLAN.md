# Universal Live Captions Security Plan

Last updated: 2026-08-21

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Define security architecture, threat model, controls, privacy model, and verification requirements |
| Scope | All system components, data, and infrastructure |
| Audience | Engineering, Security, DevOps, Compliance |
| Owner | Engineering |
| Status | Active |
| Related Documents | [ARCHITECTURE.md](ARCHITECTURE.md), [DEPLOYMENT.md](DEPLOYMENT.md), [ADR-0011](adr/ADR-0011-gemini-only-pipeline.md) |

---

## Threat Model

| # | Threat | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| T-1 | **Silent audio capture**: user is unaware audio is being captured | Medium | High (privacy) | Explicit capture indicator; explicit start action; never auto-start capture |
| T-2 | **Audio persistence**: raw audio written to disk without consent | Low | High (privacy) | No raw audio persistence; audio exists only in memory and in the TLS stream to Gemini |
| T-3 | **Exfiltration**: audio/transcripts sent somewhere unexpected | Low | High (privacy) | The ONLY network destination is the configured Gemini websocket endpoint (`generativelanguage.googleapis.com`); no other outbound calls exist in the app |
| T-4 | **Microphone capture without consent** | Low | High (privacy) | Microphone capture is not implemented; WASAPI loopback renders system output only |
| T-5 | **Tricking the user** via overlay/input interception | Low | Medium | Overlay click-through is opt-in; overlay is a normal WPF window |
| T-6 | **Supply chain**: compromised NuGet binaries | Low | High | Dependency review per QUALITY_ASSURANCE; `dotnet list package --vulnerable` gate before release |
| T-7 | **Malicious transcript injection** feeding captions | Low | Low | Captions rendered as plain text in a non-interactive window |
| T-8 | **API key theft** from settings files or logs | Low | High | Key lives only in Windows Credential Manager; never written to settings.json, logs, or exceptions (see Secret Management) |

## Assets and Trust Boundaries

- **Asset: captured audio** (in-memory, transient). Trust boundary: within the process, then over TLS to the Gemini endpoint while a session runs. Never persisted.
- **Asset: transcripts** (in-memory caption state). Same boundary; cleared on stop.
- **Asset: user configuration** — per-user JSON at `%LocalAppData%\UniversalCaptions\settings.json` storing only the UI-preference categories (audio source device id, speech language, translation on/off + target, overlay opacity/font size/click-through, overlay placement, overlay view state). **Never contains raw audio, transcripts, or credentials.** Writes are atomic (`.tmp` + `File.Move(overwrite)`); a corrupt file yields safe defaults and never blocks startup.
- **Asset: Gemini API key** — Restricted; Windows Credential Manager only (see Secret Management).
- The process is trusted; external inputs are limited to the Gemini session output and audio device state.

## Authentication Risks

None — no accounts or authentication. The user's Gemini API key authenticates the cloud session and is managed by Google's quota/abuse systems.

## Authorization Matrix

Not applicable — single-user local application with no roles.

## Tenant Isolation Strategy

Not applicable — single-user local application.

## Data Classification

| Classification | Examples | Storage Rules | Transmission Rules | Retention |
|---|---|---|---|---|
| Restricted | Gemini API key | Windows Credential Manager (advapi32 `CredWriteW`); in-memory only while an active session references it | TLS to vendor endpoint (`generativelanguage.googleapis.com`) | Until the user explicitly removes it; never written to settings.json or logs |
| Confidential | Captured system audio (may contain conversations, passwords spoken aloud, personal data) | Never persisted; in-memory only | **Streamed over TLS to the Gemini Live endpoint while a caption session runs (ADR-0011)**; to no other destination | Not retained |
| Internal | Transcripts in caption state | In-memory caption history; cleared on stop | Transcription/translation results return over the same TLS session; never retransmitted elsewhere | Cleared on stop / session end |
| Public | App diagnostics without audio content (sample rates, device names) | Optional log output | n/a | Log lifespan |

## Privacy Model

1. **Capture is explicit and visible.** Audio capture only begins when the user clicks "Start Captions". While active, a capture indicator is always visible in the control window.
2. **No silent capture.** There is no scenario in which the app captures audio without an explicit user action in the same session.
3. **No raw audio persistence.** Raw PCM audio is held in memory transiently for processing and is never written to disk.
4. **Cloud processing disclosure (ADR-0011).** Speech recognition and translation run in a Gemini Live session: audio IS streamed to Google while captions run. The app communicates this in its documentation and landing page; there is no offline mode.
5. **Single destination.** The only network endpoint the app contacts is the configured Gemini websocket endpoint. No telemetry, no update checks, no other services.
6. **Microphone excluded.** Microphone capture is not implemented; capture is WASAPI loopback of system output only.
7. **Clear stop action.** "Stop Captions" stops capture immediately, ends the Gemini session, and clears transient state.

Documented in [PROJECT_CONSTITUTION.md](PROJECT_CONSTITUTION.md) Section 10 as immutable policy
(amended 2026-08-21 by [ADR-0011](adr/ADR-0011-gemini-only-pipeline.md)).

## Secret Management

The Gemini API key is **Restricted** data per the Data Classification table. The App enforces this
policy through the seams documented in [ADR-0009](adr/ADR-0009.md):

- **Storage.** The Gemini API key is stored in **Windows Credential Manager** via advapi32
  `CredWriteW` under the target name `UniversalCaptions:GeminiApiKey` (type
  `CRED_TYPE_GENERIC`, persistence `CRED_PERSIST_LOCAL_MACHINE`, per-user DPAPI encryption).
  The key never appears in `settings.json`, source code, logs, exception messages, telemetry,
  or clipboard.
- **Read path.** The App reads the credential **once at the start of a Gemini session** through
  `ICredentialStore.TryGetCredential` (`LiveTranslationEngineFactory`); the engine receives the
  value as `GeminiLiveTranslateEngineOptions.ApiKey` and drops it from active memory on engine
  Dispose / Stop.
- **UI capture.** The key flow opens a modal containing a WPF `PasswordBox`. The local `string`
  reference is dropped immediately after `SetCredential` returns; the `PasswordBox.Password` is
  cleared before the modal closes. The raw value is never displayed back to the user — only a
  status is shown.
- **No env-var fallback in the App.** The legacy `UC_GEMINI_API_KEY` environment variable is
  **not** consulted by the production App (the developer spike runner keeps the env-var path
  for wire testing only).
- **Revocation.** The previously exposed `AQ.Ab8RN6…` key (fingerprint `AQ.Ab8RN`) must be
  revoked in Google AI Studio before any public release. Replacement keys live in the user's
  Credential Manager only; the value is never pasted into chat, source, `.env`, or `settings.json`.

## Input and File Upload Validation

Not applicable — no file inputs. The app reads only the credential store and its own settings file (schema-validated, tolerant loader).

## Rate Limiting

Google enforces per-key quotas on the Gemini API; session failures surface as classified
`LiveTranslationError` messages (quota → "Wait and retry").

## Session and Cookie Security

Not applicable — no web sessions. The Gemini websocket uses TLS with the API key as the auth token.

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

Not applicable — no persistent data beyond UI preferences.

## Incident Response

Operational failures (device loss, session failure) are handled at runtime with user-readable errors and recovery/retry, not a security incident process (MVP). Security incidents would be reported to the project owner.

## Security Test Cases

### MVP Security Tests

- [ ] **Capture indicator** — capture-active state is always reflected in the UI
- [ ] **No persistence** — running a capture session writes no audio or transcript files
- [ ] **Explicit start** — capture never begins without a user start action
- [ ] **Microphone isolation** — the app never opens a microphone capture endpoint
- [ ] **Single destination** — the only outbound connection is the Gemini websocket endpoint
- [ ] **Stop clears state** — stopping capture clears in-memory transcripts

### Credential / API-key Security Tests (ADR-0009)

- [x] **Production App does NOT read `UC_GEMINI_API_KEY` env var** — pinned by
      `LiveTranslationEngineFactoryTests.Create_Ignores_UC_GEMINI_API_KEY_EnvVar`.
- [x] **Production App never writes the Gemini key to `settings.json`** — the raw key value is not a
      `UserSettings` field (schema v3 has no provider concept at all).
- [x] **Key is dropped from active session after Stop** — the engine receives the key as
      `ApiKey` on construction and is disposed by the pipeline on Stop; no caching in
      `LiveTranslationEngineFactory` or `WindowsCredentialStore`.
- [x] **Settings UI never displays the raw key** — the key panel shows only a status; the modal
      capture uses a `PasswordBox` and clears its `Password` before closing.
- [x] **Key removal deletes the Credential Manager entry** — pinned by
      `WindowsCredentialStoreTests.Roundtrip_SetReadDelete` and `InMemoryCredentialStoreTests.Remove_Deletes_Value`.
- [x] **Factory never throws on a failing `ICredentialStore`** — pinned by
      `LiveTranslationEngineFactoryTests.Create_StoreException_DoesNotThrow`.

## Release Security Checklist

- [ ] Dependency scan passed with no critical or high findings
- [ ] Static analysis passed with no security-related findings
- [ ] Privacy model reviewed against the shipped behavior (cloud disclosure present)
- [ ] Security test cases executed
- [ ] No secrets in the repository
