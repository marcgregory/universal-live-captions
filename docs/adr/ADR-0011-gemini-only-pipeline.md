# ADR 0011: Gemini Live Is the Single STT + Translation Engine (Local Whisper and Argos Removed)

Date: 2026-08-21

## Status

Approved (user decision, 2026-08-21: "tanggalin ang Argos… wala na ang local-first, Gemini na lang lahat" — confirmed via structured questions: Gemini also replaces local Whisper STT; provider dropdown removed; full removal of dead machinery).

## Context

The production pipeline has been: WASAPI loopback → local faster-whisper STT (`fasterwhisper-native`, validated default) → captions, with an optional translation leg: local Argos (caption-line text translation) or Gemini Live Translate (live audio translation, real-wire validated 2026-08-08/09, `docs/spikes/GEMINI_MODEL_DISCOVERY.md`).

Problems with the dual/local architecture:

1. **Argos is slow.** Measured repeatedly (Slice 6, `captionwire` benchmark, latency studies): multi-second per-line service time, serialized single-gate process, warm-up cost, and a heavy bundled Python venv + package closure. The user experience regression is the primary motivator for this decision.
2. **Two parallel caption paths** (local STT source lines vs live-engine translation lines) carry permanent complexity: provider policy, epoch guards across provider switches, display-mode reconciliation, and a large test matrix.
3. **Local Whisper costs real CPU** (worker ~32–34% of the machine even after Entry 16) and adds its own latency profile.

Decisive facts:

- The Gemini Live Translate surface is real-wire-proven end to end (authentication, setup, audio in, `outputTranscription` out, turnComplete/commit-idle finals, provenance PASS with Argos pinned at zero calls).
- The setup frame's top-level `inputAudioTranscription` field was **proven accepted by the real server** (2026-08-09 A/B run: variant B with the field produced byte-identical translation streams; both variants provenance-clean). The protocol source explicitly anticipated this product change ("If a future product change drops Whisper from the pipeline and needs Gemini to also transcribe the input audio, this is the top-level field to add").
- **Open verification gate:** whether `serverContent.inputTranscription.text` frames actually stream back on `gemini-3.5-live-translate-preview`. The A/B run proved acceptance and translation-stream equivalence; it did not assert observation of input-transcription texts. This must be verified on the real wire (spike) before a release claims Gemini-sourced source-language captions.

## Decision

1. **Gemini Live is the only speech-to-text and translation engine.** The pipeline becomes:
   `WASAPI loopback → audio processor → Gemini Live session → Captions → Overlay`.
2. **Remove the local engines wholesale:** delete `UniversalCaptions.Speech` (Whisper engines, faster-whisper worker, committer) and `UniversalCaptions.Translation` (Argos engine, TagalogNaturalizer, argos server script) with their test projects. Delete `UniversalCaptions.Benchmarks` (all of its modes measured the removed local engines; the Gemini spike tool remains the measurement path for the cloud engine).
3. **One session, two surfaces.** `GeminiLiveTranslateEngine` always requests top-level `inputAudioTranscription` and raises new source-transcription events alongside the existing translation events. Source captions come from `inputTranscription`; translated captions from `outputTranscription`.
4. **The Gemini session runs whenever capture runs.** Because the Live Translate service is translate-only (no transcribe-only mode), the session streams audio to Google for the whole capture session even when the user's translation toggle is OFF; with the toggle OFF the pipeline suppresses translation-origin lines and shows source-transcript captions only. This is an explicit, disclosed privacy trade-off (see Constitution amendment).
5. **CaptionPipeline loses the `ISpeechToTextEngine` leg entirely.** Gemini transcription events feed `ProcessPartial`/`ProcessFinal` (source captions) and `LatencyUpdated` (remapped: final transcription commit latency); translation events keep feeding `ProcessPartialTranslation`/`ProcessFinalTranslation`; `EndToEndLatencyUpdated` unchanged.
6. **UI simplification:** the translation-provider dropdown is removed. Translation ON = Gemini into the selected target language; all source/target languages are enabled unconditionally (Gemini auto-detects source). The Gemini API-key panel becomes the primary setup surface; a definitively unusable key blocks enabling translation with an actionable message.
7. **Settings schema v3:** the `TranslationProvider` enum and `Provider` field are removed (tolerant loader ignores the stale field in existing files).
8. **Packaging shrinks to the .NET publish output:** no Python runtime merge, no model staging, no argos-packages, no `UC_FW_*`/`UC_ARGOS_*`/HF env vars in the launcher.
9. **Core contract cleanup:** `ITranslationEngine` (caption-line text translation) and `ISpeechToTextEngine` are deleted with their implementations; `ILiveAudioTranslationEngine` gains the transcription events; `SpeechTranscript` remains the source-caption ingress type; `TranslationGuard` (source ≠ target) is retained as a pure validation rule.
10. **Constitution amendment (privacy):** the immutable rules that remain are: no silent capture, no raw-audio persistence, no microphone capture, keys only in Windows Credential Manager (ADR-0009). The "local-first STT and translation" clause is superseded by this ADR: captured system audio is streamed to Google's Gemini API whenever capture runs. This must remain visible in the README/security documentation.

## Consequences

- **Offline operation is no longer possible.** Without a stored, valid Gemini API key the app produces no captions. Startup surfaces an actionable error instead of silently degrading.
- **Latency profile changes.** First caption is bounded by Gemini's first token (spike: ~5.9–6.6 s first partial) instead of local Whisper first-final (~3.2 s measured). Accepted by the user as the price of removing Argos slowness and local CPU cost.
- **Quality and availability become Google's.** Accuracy, language coverage, rate limits, quota, and session lifetime (goAway) are externalized. Failure surfacing (already built for the Gemini path) becomes the primary reliability story.
- **Large test-suite reduction:** Speech.Tests (109) and Translation.Tests (27) die with their projects; App.Tests/Captions.Tests lose the caption-line translation and provider-switch matrices; Gemini.Tests gain input-transcription protocol/engine pins.
- **Verification gate before release:** real-wire proof that `inputTranscription` frames arrive (spike run); until then, Gemini-sourced source captions are implemented-but-unverified (marked as such in TEST_REPORT).

## References

- `docs/spikes/GEMINI_MODEL_DISCOVERY.md` (real-wire validation + A/B `inputAudioTranscription` result)
- ADR-0006 (Argos language pairs — superseded), ADR-0009 (credential store — unchanged), ADR-0010 (audio boundary — unchanged)
- `docs/implementation/investigations/latency-study.md`, `docs/reports/BENCHMARK_REPORT.md` (Argos/Whisper measurements)
