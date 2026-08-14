# Runtime Gemini-Toggle Latency Verification

Status: **PASS (2026-08-12, measurement only).** No code changes.

## One-paragraph result

Real WASAPI two-mode measurement (one-off harness, deleted after use; evidence `latency_mode_compare.log`
kept): Release app + loopback English audio, LEG1 Translate OFF then a runtime toggle to Gemini EN→TL
for LEG2 (no Stop/Start, per v0.5.33 CLI switches). Whisper STT FINAL latency (`LatencyText`,
capture→FINAL, n=17 each): Translate OFF mean **11.8 s** (6.3–17.0 s) vs Gemini ON mean **11.4 s**
(7.5–13.9 s) — **identical STT pipeline in both modes**.

## Key proof

The stderr trace proves **Gemini is fully detached when translation is OFF**: the first translation
request (`T5`) fired only at **52.1 s** — exactly the runtime toggle — and zero translation requests
occurred during the English-only leg.

## Conclusion

Gemini does not make English-only slower; it masks Whisper's committed-FINAL cadence (~8 s partials /
~12 s FINALs) by streaming partial translations (Gemini partial ≈11.5 s ≈ Whisper FINAL, no added
lag). The "frozen vs realtime" comparison is the same underlying timing.

## Follow-up

The next real UX/perf investigation (now complete) was **Gemini streaming caption segmentation** —
see `investigations/gemini-segmentation.md`.

## Evidence

- CHANGELOG v0.5.35.
- `latency_mode_compare.log` (untracked).
