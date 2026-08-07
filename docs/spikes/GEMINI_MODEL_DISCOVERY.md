# Gemini Live Translate — Real-Wire Spike Status

**Date opened:** 2026-08-08
**Date closed (real-wire PASS):** 2026-08-08
**Status:** **A1–A6 real-wire integration PASSED.** Connection, setup, audio, output
transcription, turnComplete, and the 12/12 usable utterances gate are all green. The
`sessionResumptionUpdate` Live API control frame is recognized by A5 and treated as a no-op by
A6; the engine no longer kills the session on it. Production-path code (`ClientWebSocketGeminiChannel`,
`GeminiLiveTranslateProtocol`, `GeminiLiveTranslateEngine`, `GeminiLiveTranslateEngineOptions`)
remains the validated, frozen implementation. 476/476 deterministic tests pass.

## Spike evidence (2026-08-08, final run)

| Gate                          | Result                     |
|-------------------------------|----------------------------|
| Authentication                | ✅ PASS                    |
| `setupComplete`               | ✅ PASS (observed)         |
| `outputTranscription` frames  | ✅ PASS (observed)         |
| `turnComplete` frames         | ✅ PASS (observed)         |
| Usable utterances             | **12 / 12 ✅**             |
| API-key leakage               | **None ✅**                |
| WebSocket transport           | ✅ Working                 |
| Translation output            | ✅ Working (real Tagalog)  |

Evidence file: `artifacts/spike-result/spike-result.json`
(`AuthOk: true`, `SetupCompleteObserved: true`, `OutputTranscriptionObserved: true`,
`TurnCompleteObserved: true`, `UsableUtteranceCount: 12`, `ApiKeyLeakageDetected: false`).

Performance observed across the 12-utterance corpus (16 kHz / 22.05 kHz / 8 kHz WAV, 11–318 s):

- **First translated partial:** ~5.9–6.6 s after `StartAsync` (utterance 11 = 5,923 ms;
  utterance 12 = 6,313 ms; consistent with the third pre-fix run that reported 6,563 ms).
- **Final translated output:** ~15.4–16.0 s after `StartAsync` (utterance 11 = 15,449 ms;
  utterance 12 = 15,763 ms).
- **Partials per utterance:** 8 (one per ~1.5 s during the active window).
- **Finals per utterance:** 1.
- **Per-utterance errors:** 0.
- **Final texts (samples):** "Ano", "Ano", "lang malaman ang lalim", "kundi kung ano",
  "itanong ninyo" — all real Tagalog translations of the corresponding English WAV.

## What the spike told us

Real-wire spike runs against `wss://generativelanguage.googleapis.com/ws/...v1beta
.GenerativeService.BidiGenerateContent` (with the user's API key, redacted in this repo):

| Round | Issue found | Root cause | Status |
|---|---|---|---|
| 1 | Receive loop threw `InvalidOperationException: The WebSocket is not connected` | `ClientWebSocketGeminiChannel.ReceiveTextAsync` did not wait for `OpenAsync` to complete before calling `_socket.ReceiveAsync`; the receive loop runs ahead of the handshake. | **Fixed** — `_openedTcs` gate added. |
| 1 | `GeminiLiveTranslateProtocol.BuildSetupFrame` wrote `systemInstruction.parts` as a STRING instead of an array of `{text}` objects. | `WriteString("parts", ...)` instead of `WriteStartArray("parts")` + nested `{text}` object. | **Fixed** — now writes `parts: [{text: "..."}]`. |
| 3 | Server returned `InvalidPayloadData — Unknown name "inputAudioTranscription" at 'setup.generation_config': Cannot find field.` | The Google docs WebSocket example includes `inputAudioTranscription: {}` in `generationConfig`, but the real server rejects that field. The App doesn't need the input transcript (Whisper STT owns the source), so we omit the field. | **Fixed** — `inputAudioTranscription` dropped from the setup frame. `outputAudioTranscription` retained (it carries the translated text). |
| 4 | Server returned `InvalidPayloadData — Unknown name "outputAudioTranscription" at 'setup.generation_config': Cannot find field.` | The docs WebSocket example puts `outputAudioTranscription` inside `generationConfig`, but the real server says that's the wrong path. The REST API reference at https://ai.google.dev/api/live places `outputAudioTranscription` as a TOP-LEVEL sibling of `model` and `generationConfig` on `BidiGenerateContentSetup` — not nested inside `generationConfig`. The `translationConfig` field stays inside `generationConfig` (different path). | **Fixed** — `outputAudioTranscription` moved to the `setup` top level. Apex docs/server discrepancy resolved. |
| 5 | `InvalidOperationException: Gemini Live Translate protocol does not use binary frames; the server sent one.` | `ClientWebSocketGeminiChannel.ReceiveTextAsync` only accepted `WebSocketMessageType.Text` and rejected `Binary` frames outright. The real server sends binary frames for the translated audio path; the side-channel text still arrives in JSON over those binary frames. | **Fixed** — channel now accepts Binary frames, decodes UTF-8 JSON when the payload starts with `{` or `[`, and falls back to a metadata-only diagnostic (`payloadLength` + first 16 bytes hex + UTF-8 attempt + JSON attempt) on failure. No payload bytes are logged. |
| 6 | A5 protocol parser reports `Unrecognized top-level frame (no serverContent, error, setupComplete, or goAway)` after a final translation has already arrived. | A5 only knows four top-level frame shapes (`serverContent`, `error`, `setupComplete`, `goAway`). The real server emits at least one additional top-level shape after the final translation (the runner saw translation → unknown frame → engine fatal). The unknown frame is NON-blocking — the translation was already delivered — but A5 treats it as fatal. | **Diagnostic-only fix** — `TryParseServerFrame` now emits a structural fingerprint of the unknown top-level frame (`topLevelKeys=[name:ValueKind, name:ValueKind, ...]`, no payload bytes) so the next run tells us exactly what A5 needs to add. Behavior is unchanged on the engine side (`TranslationFailed` still fires for malformed frames; the spike runner still records the diagnostic verbatim in the per-utterance error list). 3 new protocol tests pin the diagnostic shape. |
| 7 | Real wire identified the unknown frame as `sessionResumptionUpdate` (Google-documented Live API control message, https://ai.google.dev/api/live#sessionresumptionupdate). | A5's union handling was incomplete — the server emits `{"sessionResumptionUpdate":{"resumable":true,"newHandle":"…"}}` on the Live Translate surface even though Live Translate doesn't accept `sessionResumption` configuration today. The frame is informational, not a fatal error. | **Fixed** — added `GeminiServerMessage.SessionResumptionUpdate(NewHandle, Resumable)` typed case + a no-op switch branch in the engine receive loop. 7 new tests pin the behavior (4 protocol: resumable=true / resumable=false / empty object / wrong type; 2 engine: no-op across a final-translation boundary and across a partial → update → turnComplete boundary; 1 tightened diagnostic test asserts the known shapes no longer leak into the diagnostic). 476/476 deterministic tests pass. |

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
- **`sessionResumptionUpdate`:** informational control frame Google emits on Live API sessions
  when resumption is configured. Live Translate does not accept resumption configuration today, so
  the frame is informational on our surface; A5 parses `resumable` (bool) + `newHandle` (string,
  optional) and A6 treats it as a no-op. Reference: https://ai.google.dev/api/live#sessionresumptionupdate.

## Server → client frame shape

Live Translate sends the translated transcript on `serverContent.outputTranscription.text`. Our
existing parser handles that path (`TryBuildServerContent` at lines 245-275 of
`GeminiLiveTranslateProtocol.cs`), plus the older `serverContent.modelTurn.parts[].text` shape
and the `partial` / `turnComplete` boolean flags. No additional parsing code was needed for the
content side; the Round 7 work was the missing **control-plane** union case.

## Code changes (rounds 1–7)

- `src/UniversalCaptions.Speech.Gemini/GeminiLiveTranslateProtocol.cs` — `BuildSetupFrame`
  rewritten to emit the corrected contract; `systemInstruction` parameter dropped;
  `sessionResumptionUpdate` recognized and parsed; structural-diagnostic for unknown
  top-level shapes (Round 6) so the next run tells us what A5 is missing.
- `src/UniversalCaptions.Speech.Gemini/GeminiLiveTranslateEngine.cs` — wires
  `ResolveTargetLanguageCode()` into the setup-frame call; `SessionResumptionUpdate` switch
  branch is a documented no-op.
- `src/UniversalCaptions.Speech.Gemini/GeminiServerMessage.cs` — added
  `SessionResumptionUpdate(string? NewHandle, bool Resumable)` record.
- `src/UniversalCaptions.Speech.Gemini/ClientWebSocketGeminiChannel.cs` — accepts Binary
  frames, decodes UTF-8 JSON, falls back to metadata-only diagnostic on failure (Round 5).
- `src/UniversalCaptions.Speech.Gemini/GeminiLiveTranslateEngineOptions.cs` — `DefaultModel`
  updated to `models/gemini-3.5-live-translate-preview`; new `ResolveTargetLanguageCode()` with
  ISO 639-1 → BCP-47 mapping (tl → fil, en → en, ja → ja, …); new `TargetLanguageCode` override.
- `tests/UniversalCaptions.Speech.Gemini.Tests/GeminiLiveTranslateProtocolTests.cs` — model +
  target fixtures updated; tests rewritten to assert `AUDIO` modality, `outputAudioTranscription`
  + `inputAudioTranscription` (in `generationConfig`), `translationConfig.targetLanguageCode`,
  `echoTargetLanguage` (default + true), and the absence of `systemInstruction`; new
  `SessionResumptionUpdate_*` and `UnrecognizedFrame_*` tests.
- `tests/UniversalCaptions.Speech.Gemini.Tests/GeminiLiveTranslateEngineTests.cs` — model
  fixture updated; `StartAsync_SendsSetupFrameAsFirstMessage` rewritten to assert every field
  of the corrected setup frame; new `SessionResumptionUpdateFrame_*` end-to-end tests.
- `tests/UniversalCaptions.Speech.Gemini.Tests/Spikes/GeminiDirectWireSpike.cs` — prints
  `resolved target language code` + the new setup frame; runs the full 12-utterance corpus;
  per-utterance error capture retains the full inner exception chain.
- `tools/GeminiDirectWireSpike/Program.cs` + `tools/GeminiDirectWireSpike/GeminiDirectWireSpike.csproj`
  — thin runner shim so the spike is `dotnet run --project tools/GeminiDirectWireSpike`, not
  `dotnet run --project tests/…` (which collides with the xUnit host).

## Verification (final state, 2026-08-08)

- **476/476 deterministic tests pass** (7 new in this work: 4 protocol sessionResumptionUpdate,
  1 tightened diagnostic, 2 engine end-to-end).
- **Build:** 0 warnings, 0 errors.
- **`dotnet format --verify-no-changes`:** exit 0.
- **Real-wire spike:** 12/12 usable utterances, 0 errors, real Tagalog translations,
  no API-key leakage (verified by `CheckForApiKeyLeakage` substring scan over every output
  field).

## Next step

The A1–A6 implementation is real-wire-validated and frozen. The remaining v0.5.30 acceptance
work is **NOT** wire-protocol work; it is the production-path / clean-VM acceptance:

1. Add a credential store for the user's Gemini API key (Windows Credential Manager) with a
   Settings flow that does not require pasting the key into the App UI.
2. Promote the `GeminiLiveTranslateEngine` from spike-only to a user-toggleable translation
   engine in the App (Settings → Translation → Provider = Argos | Gemini).
3. Clean-VM install the v0.5.30 installer, exercise Start/Stop + toggle + Settings, capture
   evidence, ship.
