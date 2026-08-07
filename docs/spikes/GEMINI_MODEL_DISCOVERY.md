# Gemini Live Translate — Real-Wire Spike Status

**Date opened:** 2026-08-08
**Status:** A1–A4 setup contract confirmed PASS against the live Google service. A5 protocol
parser is fail-soft with a structural diagnostic for unknown top-level shapes (so the next run
tells us what A5 needs to add rather than "Unrecognized top-level frame"). Real-wire spike
classified as **REAL-WIRE: PARTIAL PASS / PROTOCOL FAIL** — connection, setup, audio, and
translation output are all verified; an unknown-but-non-fatal server frame after the final
translation is the only remaining gap. 469/469 deterministic tests pass.

## What the spike told us

Two rounds of real-wire spike runs against `wss://generativelanguage.googleapis.com/ws/...v1beta
.GenerativeService.BidiGenerateContent` (with the user's API key, redacted in this repo):

| Round | Issue found | Root cause | Status |
|---|---|---|---|
| 1 | Receive loop threw `InvalidOperationException: The WebSocket is not connected` | `ClientWebSocketGeminiChannel.ReceiveTextAsync` did not wait for `OpenAsync` to complete before calling `_socket.ReceiveAsync`; the receive loop runs ahead of the handshake. | **Fixed** — `_openedTcs` gate added. |
| 1 | `GeminiLiveTranslateProtocol.BuildSetupFrame` wrote `systemInstruction.parts` as a STRING instead of an array of `{text}` objects. | `WriteString("parts", ...)` instead of `WriteStartArray("parts")` + nested `{text}` object. | **Fixed** — now writes `parts: [{text: "..."}]`. |
| 3 | Server returned `InvalidPayloadData — Unknown name "inputAudioTranscription" at 'setup.generation_config': Cannot find field.` | The Google docs WebSocket example includes `inputAudioTranscription: {}` in `generationConfig`, but the real server rejects that field. The App doesn't need the input transcript (Whisper STT owns the source), so we omit the field. | **Fixed** — `inputAudioTranscription` dropped from the setup frame. `outputAudioTranscription` retained (it carries the translated text). |
| 4 | Server returned `InvalidPayloadData — Unknown name "outputAudioTranscription" at 'setup.generation_config': Cannot find field.` | The docs WebSocket example puts `outputAudioTranscription` inside `generationConfig`, but the real server says that's the wrong path. The REST API reference at https://ai.google.dev/api/live places `outputAudioTranscription` as a TOP-LEVEL sibling of `model` and `generationConfig` on `BidiGenerateContentSetup` — not nested inside `generationConfig`. The `translationConfig` field stays inside `generationConfig` (different path). | **Fixed** — `outputAudioTranscription` moved to the `setup` top level. Apex docs/server discrepancy resolved. |
| 5 | `InvalidOperationException: Gemini Live Translate protocol does not use binary frames; the server sent one.` | `ClientWebSocketGeminiChannel.ReceiveTextAsync` only accepted `WebSocketMessageType.Text` and rejected `Binary` frames outright. The real server sends binary frames for the translated audio path; the side-channel text still arrives in JSON over those binary frames. | **Fixed** — channel now accepts Binary frames, decodes UTF-8 JSON when the payload starts with `{` or `[`, and falls back to a metadata-only diagnostic (`payloadLength` + first 16 bytes hex + UTF-8 attempt + JSON attempt) on failure. No payload bytes are logged. |
| 6 | A5 protocol parser reports `Unrecognized top-level frame (no serverContent, error, setupComplete, or goAway)` after a final translation has already arrived. | A5 only knows four top-level frame shapes (`serverContent`, `error`, `setupComplete`, `goAway`). The real server emits at least one additional top-level shape after the final translation (the runner saw translation → unknown frame → engine fatal). The unknown frame is NON-blocking — the translation was already delivered — but A5 treats it as fatal. | **Diagnostic-only fix** — `TryParseServerFrame` now emits a structural fingerprint of the unknown top-level frame (`topLevelKeys=[name:ValueKind, name:ValueKind, ...]`, no payload bytes) so the next run tells us exactly what A5 needs to add. Behavior is unchanged on the engine side (`TranslationFailed` still fires for malformed frames; the spike runner still records the diagnostic verbatim in the per-utterance error list). 3 new protocol tests pin the diagnostic shape. |

## The corrected setup frame

```json
{
  "setup": {
    "model": "models/gemini-3.5-live-translate-preview",
    "generationConfig": {
      "responseModalities": ["AUDIO"],
      "translationConfig": {
        "targetLanguageCode": "fil",
        "echoTargetLanguage": false
      }
    },
    "outputAudioTranscription": {}
  }
}
```

Notes:
- `outputAudioTranscription` is a TOP-LEVEL sibling of `model` and `generationConfig` on
  `BidiGenerateContentSetup`. The WebSocket example in the Live Translate docs shows the field
  inside `generationConfig`, but the real server rejects that placement. The REST API reference
  at https://ai.google.dev/api/live confirms the top-level placement.
- `inputAudioTranscription` is omitted entirely. The App pipeline doesn't need the input
  transcript (Whisper STT owns the source).
- `translationConfig` is a child of `generationConfig` (per the Live Translate docs). It is NOT
  a top-level field.

Source of truth: https://ai.google.dev/gemini-api/docs/live-api/live-translate (verified via direct
fetch on 2026-08-08).

### Key contract details

- **Model id format:** `models/{model_id}` (the `models/` prefix is required in the setup frame on
  the WebSocket surface; bare ids are rejected).
- **Output modality:** `AUDIO` only. The translated text arrives on the
  `serverContent.outputTranscription.text` side-channel, NOT as a TEXT response.
- **Target language code:** BCP-47 (for example `fil` for Filipino / Tagalog, NOT ISO 639-1 `tl`).
  The App-side ISO 639-1 code is mapped to a BCP-47 code via
  `GeminiLiveTranslateEngineOptions.ResolveTargetLanguageCode()`.
- **`systemInstruction`:** rejected. Live Translate is "audio restricted" and offers
  "translation only. Pure low-latency translation; no support for tools or instructions."
- **`echoTargetLanguage`:** optional boolean (default false). When true, the server attaches the
  target language tag to the output audio frames. Disabled by default — the caption pipeline carries
  the language itself; the audio side-channel is ignored.

## Server → client frame shape

Live Translate sends the translated transcript on `serverContent.outputTranscription.text`. Our
existing parser handles that path (`TryBuildServerContent` at lines 239-249 of
`GeminiLiveTranslateProtocol.cs`), plus the older `serverContent.modelTurn.parts[].text` shape
and the `partial` / `turnComplete` boolean flags. No additional parsing code was needed.

## Code changes (this round)

- `src/UniversalCaptions.Speech.Gemini/GeminiLiveTranslateProtocol.cs` — `BuildSetupFrame`
  rewritten to emit the corrected contract; `systemInstruction` parameter dropped.
- `src/UniversalCaptions.Speech.Gemini/GeminiLiveTranslateEngineOptions.cs` — `DefaultModel`
  updated to `models/gemini-3.5-live-translate-preview`; new `ResolveTargetLanguageCode()` with
  ISO 639-1 → BCP-47 mapping (tl → fil, en → en, ja → ja, …); new `TargetLanguageCode` override.
- `src/UniversalCaptions.Speech.Gemini/GeminiLiveTranslateEngine.cs` — wires
  `ResolveTargetLanguageCode()` into the setup-frame call.
- `tests/UniversalCaptions.Speech.Gemini.Tests/GeminiLiveTranslateProtocolTests.cs` — model +
  target fixtures updated; tests rewritten to assert `AUDIO` modality, `outputAudioTranscription`
  + `inputAudioTranscription` (in `generationConfig`), `translationConfig.targetLanguageCode`,
  `echoTargetLanguage` (default + true), and the absence of `systemInstruction`.
- `tests/UniversalCaptions.Speech.Gemini.Tests/GeminiLiveTranslateEngineTests.cs` — model fixture
  updated; `StartAsync_SendsSetupFrameAsFirstMessage` rewritten to assert every field of the
  corrected setup frame (AUDIO, outputAudioTranscription, inputAudioTranscription,
  translationConfig, targetLanguageCode, no systemInstruction).
- `tests/UniversalCaptions.Speech.Gemini.Tests/Spikes/GeminiDirectWireSpike.cs` — prints
  `resolved target language code` + the new setup frame.

## Verification

- **464/464 deterministic tests pass** (added one new fact for `SetupFrame_EchoTargetLanguage_TrueIsForwarded`).
- **Build:** 0 warnings, 0 errors.
- **`dotnet format --verify-no-changes`:** exit 0.

## Next step

Re-run the spike with a valid (revoked-and-regenerated) API key to confirm:

1. Server accepts `models/gemini-3.5-live-translate-preview` and returns `setupComplete`.
2. Server emits at least one `serverContent.outputTranscription.text` frame per utterance.
3. The text in that frame is a real Tagalog translation of the input audio.

If the spike passes, A1–A6 implementation is real-wire-validated and v0.5.30 can move toward the
acceptance gate.

If the spike still fails, capture the new server error verbatim — the model + modality are now
documented so any remaining failure is a different class of bug (endpoint path, audio MIME, etc.).
