---
name: gemini-v0.5.30-slice-scope
description: v0.5.30 = Gemini Live Translate as an optional second translation provider. New project (not a wholesale move of the benchmark client), fake WebSocket tests, one-time consent modal, API key in Windows Credential Manager. v0.5.29 stays offline-only.
metadata:
  type: project
---

**Scope:** v0.5.30 introduces Gemini Live Translate (`gemini-3.5-live-translate-preview`) as an *optional* second translation provider, alongside the existing Argos/OPUS-MT offline path. The production STT engine stays the same (Whisper → either Argos or Gemini → caption overlay).

**Status (2026-08-08, after user correction + real-wire spike):** A1–A6 implementation aligned with Google's current Live Translate contract. Model is now `models/gemini-3.5-live-translate-preview`, output modality is `AUDIO` with `outputAudioTranscription` side-channel carrying the translated text, `translationConfig.targetLanguageCode` is BCP-47 (`fil` for Filipino/Tagalog, mapped from ISO 639-1 `tl`), and `systemInstruction` is no longer sent (rejected by this surface). `DefaultModel` + protocol + tests + spike runner all updated. **464/464 tests pass**, build clean, format clean. The two earlier wire bugs (`systemInstruction.parts` array shape, receive-before-Open race) are still fixed. Real-wire spike validation is open — the next spike run with a fresh API key is the gate. Full notes in `docs/spikes/GEMINI_MODEL_DISCOVERY.md`. Do NOT mark v0.5.30 ready until the spike returns at least one `serverContent.outputTranscription.text` frame with real Tagalog.

**Frozen production baseline for v0.5.29 (do not reopen):**
`WASAPI → Whisper → Argos OPUS-MT en→tl → 13-rule deterministic naturalizer → Caption overlay`. No Gemini in v0.5.29, no Gemini Settings UI in v0.5.29, no Gemini in the App source for v0.5.29.

**v0.5.30 pipeline (new):**
`WASAPI → Whisper → { Arg os (offline) | Gemini Live Translate (cloud, opt-in) } → Caption overlay`. Provider selection is per-session, controlled by `UC_TRANSLATION_PROVIDER` env knob (`offline` default, `gemini-live` opt-in) and the WPF Settings provider radio.

**Why:** Constitution §11.10 (no work-without-evidence) + ADR-0003 (no-silent-switch). The benchmark client in `UniversalCaptions.Benchmarks/Translation/GeminiLiveTranslateClient.cs` is the validated protocol evidence. The App stays offline-only at v0.5.29 (already accepted 2026-08-06). Adding Gemini without a full new acceptance gate would invalidate that.

**How to apply:**
- **Project shape:** new `UniversalCaptions.Speech.Gemini` project. Do NOT move the benchmark client wholesale — extract the validated WebSocket/protocol behavior into a reusable layer, then build production `GeminiLiveTranslateEngine : ITranslationEngine` on top with proper lifecycle/error handling.
- **Testable seam:** production client must have an injectable WebSocket seam (precedent: TD-016 `LineProtocolFasterWhisperProcessProtocolTests`). Cover: setup success, malformed frames, translation output, disconnect, timeout, auth failure, quota/error responses, session shutdown, cancellation. Real Gemini API key enters only at acceptance time, never in unit tests or checked-in config.
- **API key storage:** P/Invoke `CredWriteW` / `CredReadW` (Windows Credential Manager). NOT in `UserSettings`/`SettingsStore` (TD-005 only persists the six user-facing categories). Tests use a fake credential store.
- **Consent (per `SECURITY_PLAN.md` Privacy Model §5):** one-time modal the first time the user picks `Gemini Live Translate` — "This mode sends your PC's audio to Google over the internet. Your Gemini API key may incur charges on your Google account." Cancel → revert to Offline. Continue → enable Gemini mode + persistent status line in Settings ("Gemini Live Translate is enabled. Audio is sent to Google."). User can always switch back to Offline (Argos).
- **No silent fallback (ADR-0003):** if Gemini is selected but the key is missing/invalid/expired, the App must NOT fall back to Argos silently. Show the failure in the status line; user picks Offline or fixes the key.
- **Acceptance gate:** full clean-VM install + real WebSocket session against the actual Gemini API, same harness as the v0.5.26 acceptance run. Evidence in `docs/reports/TEST_REPORT.md` v0.5.30 close-out + new `docs/reports/INSTALLER_DISCOVERY.md` v0.5.30 entries.
- **Documentation:** new ADR-0009 (Gemini Live Translate as optional production translation provider); CHANGELOG v0.5.30; `RELEASE_PLAN.md` §7 updated to reflect Gemini shipping in v0.5.30 (not "not in v0.5.29"). Landing page Gemini card remains a "FUTURE / PLANNED" item until v0.5.30 actually ships.

**Related:** [[no-future-features-on-shipping-surface]], [[user-marc-prefers-honest-future-framing]], [[artifact-registry]] (RELEASE_PLAN.md is the owner of release-readiness decisions).