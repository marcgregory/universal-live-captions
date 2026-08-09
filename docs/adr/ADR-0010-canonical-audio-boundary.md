# ADR 0010: Canonical Audio Ingestion Boundary (Canonical 16 kHz Mono PCM)

Date: 2026-08-09

## Status

Proposed (for review — no code changes)

## Context

Every STT/Gemini consumer must receive audio in one form: the channels below all feed speech
analysis, but each derives its 16 kHz mono stream by its own bespoke conversion:

- **App production path** (`UniversalCaptions.Audio.Processing.AudioProcessor` +
  `SampleRateConverter`): windowed-sinc/Blackman resample, mono down-mix `(L+R)/2`, produces
  16 kHz mono float32 chunks. This is the quality target.
- **`LiveTranslationBenchmark.LoadWav`** (`translatelive` benchmark): accepts only 8k or 16k;
  uses a linear (factor-2) upsample for 8k and **rejects 22.05/44.1/48 kHz** files outright.
- **WavLoader** (Gemini spike, `tests/.../Spikes/GeminiDirectWireSpike.cs`): accepts any rate,
  down-mixes by averaging channels, then **nearest-neighbor** resamples to 16 kHz.

The result is three independent conversion decisions for the same conceptual input. A file is
accepted or rejected, resampled or not, at quality that depends on which harness is reading it.
The 2026-08-09 probe (Option 2) confirmed a specific instance: on the same 8 kHz / 22.05 kHz
WAVs, nearest-neighbor and linear resampling produced the same fluent translation output, so the
two resampling algorithms were found to be **not a differentiator** for those runs. That probe
isolated *one* variable; it did **not** attempt to prove or disprove that *any* prior observed
garbage output was caused by nearest-neighbor resampling. **Root-cause attribution of the earlier
bad technical-run output remains an open investigation** (the original source audio/EN transcript
was not retained, so no honest attribution to Whisper vs segmentation vs Argos vs ingestion is
possible).

Separate from attribution, the architecture gap is real: **there is no single, hardened,
deterministic audio-normalization boundary** in front of the Gemini/STT consumers. Each consumer
either re-implements conversion or uses a different quality, and non-16 kHz sources (48 kHz Zoom,
22.05 kHz podcasts, 8 kHz OSR) are handled inconsistently. Whatever the exact cause of any past
bad caption, a new video must be **input data**, not a new conversion investigation.

## Decision

Introduce **one canonical audio ingestion boundary** owned by `UniversalCaptions.Audio`, and make
every STT/Gemini/en preprocessor a consumer of it — no bespoke resampling in benchmarks or spikes.

### Canonical contract

- **Output format (the canonical frame):** mono, float32, **16 kHz**, normalized amplitude in
  `[-1.0, 1.0]`, no NaN/inf. This is the exact frame the App already produces.
- **Canonical PCM bytes:** a **16 kHz mono PCM16 (LE)** projection of that frame is the *wire
  bytes* used wherever a raw PCM16 stream is required (Gemini `realtimeInput.audio`), derived by a
  single, deterministic clamp → scale → little-endian conversion.
- **Supported input:** WAV 8, 11.025, 16, 22.05, 44.1, 48 kHz; mono or stereo; 16-bit PCM (the
  minimum table the normalization test must cover). Decoding of non-WAV containers (mp4/mp3/WebM
  — e.g. the TeraBox talk) is explicitly deferred/broader but feeds the same boundary once read.

### Ingestion pipeline and rules

Input bytes → WAV decode → de-interleave → channel down-mix → resample → canonical mono 16 kHz
float32 → (PCM16 projection when a wire stream needs it).

- **Down-mix rule:** stereo→mono = `(L + R) / 2` per frame, with per-sample clamp into `[-1, 1]`
  before scaling so stereo overflow cannot exceed unity. (Matches the App's `MixChannels`.)
- **Resampler:** reuse the production **`SampleRateConverter`** (windowed-sinc, Blackman kernel)
  exactly as `AudioProcessor` uses it. One resampler implementation; benchmarks and spikes must not
  define their own linear/nn-adaptive upsample or reject non-16k rates.
- **Tail / end-of-stream (EOS) padding:** `SampleRateConverter` is a streaming filter with internal
  history. On EOF the converter must be **flushed** (drain to the filter's steady state) so the
  last output frame is not truncated or amplified; a defined number of near-zero/zero tail frames
  is emitted so the final segment carries full context. The exact tail length is owned by the
  normalization layer and must be **constant and deterministic** (not audio-dependent), so
  identical input always produces identical bytes. Rationale: a truncated tail is a known EOS
  caption-clipping hazard (the end-of-audio `Pag-pag-pag…` stutter observed at segment-cap edges was
  one such cap effect); defining the pad spec prevents that class of artifact from returning as a
  regression.

### Determinism / invariants (enforced by tests)

For any supported input the boundary guarantees:

- output is **16 kHz, mono, float32**, no NaN, no inf, all samples finite and within `[-1.0, 1.0]`
  (with tolerance); the PCM16 projection is byte-deterministic;
- **identical input → identical canonical PCM bytes** (pure function of the input; no
  RNG/noise/no wall-clock/time dependence);
- running the boundary twice yields byte-identical results.

### Test matrix (canonical normalization)

A **`CanonicalAudioNormalizationTests`** suite (unit, fake-boundary — WAV bytes in, canonical Frame
out, no hardware) must cover **all six sample rates × two channel layouts = 12 input combinations**
(6 rates × {mono, stereo}):

| Sample rate | Channels | 16-bit? | Assertions |
|---|---|---|---|
| 8 kHz | mono, stereo | yes | format invariants; L/R/center waveform sane |
| 11.025 kHz | mono, stereo | yes | format invariants |
| 16 kHz | mono, stereo | yes | pass-through identity where rate unchanged |
| 22.05 kHz | mono, stereo | yes | format invariants |
| 44.1 kHz | mono, stereo | yes | format invariants |
| 48 kHz | mono, stereo | yes | format invariants |

Each of the **12 combinations** is an independent test case; stereo cases additionally assert the
down-mix rule (`(L+R)/2` with clamp) at both rates, and the 16 kHz cases assert the 
pass-through identity path (no resampler invoked for already-canonical input).

Across the matrix:

- every output is 16 kHz mono, bounded `[-1,1]`, finite, no NaN;
- PCM16 projection is byte-deterministic across two identical inputs;
- EOS-pad consistency: two runs on the same input produce identical trailing samples (no
  wall-clock dependency);
- **quality smoke:** a clean single tone at every input rate reconstructs at 16 kHz with
  SNR above a floor (windowed-sinc is not a brick-wall; floor set to catch blatant kernel breakage,
  relaxed enough for Blackman taps) — a regression tripwire, not an accuracy gate.

### Migration

- `LiveTranslationBenchmark.LoadWav` and the spike `WavLoader` become **callers of the canonical
  boundary** instead of maintaining independent resample/reject logic. The spike's
  `ToMono16kFloat` nearest-neighbor path and the benchmark's narrow-rate rejection are retired.
- The App `AudioProcessor` already uses the canonical resampler/mix; it becomes the reference,
  unchanged in behavior. If it already provides the whole input, the canonical boundary is a thin
  wrapper / shared code path, not a second implementation.
- The frozen Option-2 **retention fix** (EN `source` column in `translatelive` CSV) stays as-is;
  this ADR does not change it.
- No changes to the frozen Gemini wire/downstream, the Argos engine, or the existing spike
  harness beyond the WAV-loader consumer swap.

## Consequences

- Any rate/channel/bit input (8k..48k, mono/stereo) becomes deterministic canonical 16 kHz mono
  before any STT/Gemini consumer — usable as generic input data.
- No random sample rate rejection; no "nearest vs linear" investigation per file.
- The EOS tail pad is explicitly defined and tested → no silent loss of final segment context.
- Cost: sinc is ~30× slower than a trivial resampler, but TD-001 established it is not a latency
  driver; the new public surface and test matrix must be maintained.

## Alternatives Considered

- **Keep per-harness conversion; fix only if proven wrong.** Rejected: one observed class of
  failure (unattributed bad outputs) is exactly the situation where a non‑canonical boundary
  makes the next investigation impossible; without a canonical gate the pipeline stays
  non‑deterministic-by-design.
- **Add a proper third resampler (e.g. libsoxr/windowed-sinc v2).** Rejected: production sinc
  resampler already exists and is validated (TD-001); a second resampler would re‑introduce the
  multi‑implementation problem this ADR eliminates.
- **Just re-run the existing linear probe and decide on nearest-vs-linear.** Rejected as the sole
  action: the probe correctly showed "not the differentiator" for those two files, but does not
  build a durable guarantee across sample rates/channels; the decision is orthogonal.

## References

- ADR-0007 (overlay display), ADR-0008 (production STT default), TD-001 (resampler benchmark:
  keep sinc), CHANGELOG v0.5.24.
- Task log: Option 1 (Gemini inputAudioTranscription) — not the failure axis for the technical
  runs; Option 2 (nearest vs linear resample probe on 8k/22k) — resampling algorithm shown to be a
  non-differentiator for those runs; attribution of the earlier bad output remains pending.