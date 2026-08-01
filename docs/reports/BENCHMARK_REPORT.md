# Benchmark Report

Last updated: 2026-08-01

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Record model-selection benchmarks on target hardware (Slice 2 STT + Slice 3 translation) |
| Scope | Slices 1–3: ggml-tiny / ggml-base via whisper.cpp (Whisper.net 1.9.1, CPU); Argos Translate 1.11.0 (Python 3.11, CPU) |
| Audience | Engineering |
| Owner | Engineering |
| Status | Active |
| Related Documents | [ADR-0003](../adr/ADR-0003.md), [ADR-0006](../adr/ADR-0006.md), [BUILD_PLAN.md](../implementation/BUILD_PLAN.md), [TEST_REPORT.md](TEST_REPORT.md) |

---

# Slice 2 — Whisper Model Benchmark

Date: 2026-07-31

## Environment

| Item | Value |
|---|---|
| OS | Windows 10 Pro (build 19045) |
| CPU | 12 logical cores (threads per decode: 4) |
| Runtime | .NET 8.0.29 |
| Backend | whisper.cpp via Whisper.net 1.9.1 (`Whisper.net.Runtime`, CPU build) |
| Samples | `jfk.wav` (canonical, 11.00 s), `jfk_noisy.wav` (jfk + 10 dB SNR white noise, 11.00 s, generated), `jfk_long.wav` (jfk×2 with a 0.5 s pause, 22.50 s, generated), `OSR_us_000_0010_8k.wav` (conversational/continuous speech, 33.62 s, 8 kHz→16 kHz upsampled) |
| Models dir | `artifacts/models/` (git-ignored) |

## Harness

`dotnet run --project src/UniversalCaptions.Benchmarks -c Release -- --threads 4`

The harness reads 16-bit PCM WAV (mono/stereo, 8/16 kHz) into mono 16 kHz floats and, for each model + sample, measures:

- **Full-file decode**: model load time, working-set delta, decode time + realtime factor, segment count, WER vs reference.
- **Streaming pass** through `WhisperSpeechToTextEngine` fed at realtime pacing (0.5 s chunks / 0.5 s sleep): wall time + factor, first-partial and first-final latency, partial/final counts, average event latencies, CPU time. The engine runs the stability-based committer (`StabilityWindow` default 3, `WindowDuration` 8 s, `CommitOverlap` 1.5 s, decode every 1 s).

References: canonical transcript for `jfk*`; for OSR a pseudo-reference produced by a ggml-small full-file decode (no canonical transcript available).

## Raw Results

### Full-file decode

| Sample | Model | RAM Δ | load | decode | factor | segments | WER |
|---|---|---|---|---|---|---|---|
| jfk (11 s) | ggml-tiny | 84.5 MB | 0.49 s | 1.56 s | 0.14× | 1 | 0.0% |
| jfk (11 s) | ggml-base | 148.6 MB | 0.60 s | 3.06 s | 0.28× | 1 | 0.0% |
| jfk_noisy (11 s) | ggml-tiny | 81.3 MB | 0.43 s | 1.45 s | 0.13× | 1 | 0.0% |
| jfk_noisy (11 s) | ggml-base | 149.9 MB | 0.60 s | 2.90 s | 0.26× | 2 | 0.0% |
| jfk_long (22.5 s) | ggml-tiny | 81.7 MB | 0.40 s | 1.63 s | 0.07× | 2 | 0.0% |
| jfk_long (22.5 s) | ggml-base | 148.9 MB | 0.57 s | 3.15 s | 0.14× | 2 | 0.0% |
| OSR (33.62 s) | ggml-tiny | 80.9 MB | 0.30 s | 3.39 s | 0.10× | 10 | 16.0% |
| OSR (33.62 s) | ggml-base | 147.5 MB | 0.77 s | 5.95 s | 0.18× | 10 | 4.9% |

### Streaming (stability-based finals, realtime pacing)

| Sample | Model | wall | factor | CPU | first partial | first final | partials | finals | avg partial lat | avg final lat |
|---|---|---|---|---|---|---|---|---|---|---|
| jfk | ggml-tiny | 12.25 s | 1.11× | 41.0 s | 3.614 s | 6.612 s | 6 | **2** | 1404 ms | 4002 ms |
| jfk | ggml-base | 12.47 s | 1.13× | 42.4 s | 4.509 s | 9.876 s | 3 | **1** | 2303 ms | 6974 ms |
| jfk_noisy | ggml-tiny | 12.08 s | 1.10× | 41.9 s | 2.979 s | 8.208 s | 7 | **1** | 1070 ms | 3964 ms |
| jfk_noisy | ggml-base | 12.79 s | 1.16× | 43.4 s | 4.628 s | 10.317 s | 3 | **1** | 2429 ms | 8090 ms |
| jfk_long | ggml-tiny | 23.01 s | 1.02× | 85.4 s | 2.886 s | 5.267 s | 16 | **5** | 1127 ms | 794 ms |
| jfk_long | ggml-base | 25.24 s | 1.12× | 93.7 s | 4.460 s | 9.549 s | 8 | **3** | 2374 ms | 8309 ms |
| OSR | ggml-tiny | 35.32 s | 1.05× | 134.1 s | 3.273 s | 5.752 s | 22 | **11** | 1450 ms | 6184 ms |
| OSR | ggml-base | 35.66 s | 1.06× | 134.3 s | 4.590 s | 10.045 s | 11 | **4** | 2798 ms | 9037 ms |

## Findings

### F1 — Streaming finals are resolved
The previous no-finals behavior (whisper.cpp yields single whole-window segments) is fixed by the stability-based committer. Every sample now produces committed finals at realtime pacing (stream wall ≤ 1.16× clip time): jfk 1–2 finals, jfk_long 3–5 finals (segment boundaries split across the pause), OSR 4–11 finals. The commit advances across epochs: in OSR the stream continued committing new finals after the initial one (11 finals for tiny).

### F2 — Realtime capability
- **tiny** decodes 0.07–0.14× realtime and streams at 1.02–1.11× wall; the strongest realtime margin.
- **base** decodes 0.14–0.28× realtime and streams at 1.06–1.16× wall; still realtime-safe on this machine.
- Both keep up with live pacing; streaming CPU is high because the full growing window is re-decoded every second (acceptable for the spike; a Slice 6 latency/CPU pass may tune window/decode-interval).

### F3 — Quality discrimination (new)
- `jfk*` is clean single-speaker speech; both models hit WER 0.0% even with 10 dB SNR noise added.
- The **OSR conversational sample discriminates**: **ggml-tiny 16.0% WER vs ggml-base 4.9% WER**. On continuous/conversational speech, base is meaningfully more accurate.
- Latency cost of base: first partial ~1 s later, first final ~2.8 s later (OSR: 10.0 s vs 5.8 s) and ~30% fewer partials. tiny emits more, faster partials.

## Recommendation (ADR-0003 — user-approved 2026-07-31)

**Default model: ggml-base** — both models are realtime-safe on this machine; on continuous/conversational speech base is clearly more accurate (OSR WER **4.9% vs tiny 16.0%**). Accuracy wins where realtime is still met. Costs: ~2× RAM (~149 MB) and ~2–4 s higher first-final latency (~10.0 s vs 5.8 s on OSR).

**Fallback/performance mode: ggml-tiny** — keep available as a low-resource option for machines where base cannot sustain realtime; first final 5.3–8.2 s, WER 0.0% on clean/noisy single-speaker, 16.0% on the conversational sample.

**ggml-small rejected** as the live-caption default (0.88× decode, not realtime-safe); used only as the OSR pseudo-reference.

Model selection is configurable (`WhisperEngineOptions.ModelPath`) and not coupled to `WhisperSpeechToTextEngine`; the runtime must not silently switch models.

## Open Follow-Ups

1. Slice 6 end-to-end: latency/CPU tuning of window size, decode interval, and `StabilityWindow` against real WASAPI loopback audio; confirm base sustains realtime on real device input.
2. Optional: benchmark `WithSplitOnWord` / `WithMaxSegmentLength` (currently opt-in, default off) for finer caption boundaries.

---

# Slice 3 — Argos Translation Benchmark

Date: 2026-08-01

## Metadata

| Attribute | Value |
|---|---|
| Purpose | Record translation latency/quality of Argos Translate on target hardware for MVP language pairs |
| Scope | Argos 1.11.0, Python 3.11 (dedicated venv under `artifacts/argos/`, git-ignored), CPU; direct pairs `en→tl`, `ja→en`, `en→ja` and pivot `ja→tl` via `en` |
| Audience | Engineering |
| Owner | Engineering |
| Status | Active |
| Related Documents | [ADR-0006](../adr/ADR-0006.md), [TECH_STACK.md](../TECH_STACK.md), [BUILD_PLAN.md](../implementation/BUILD_PLAN.md) |

## Environment

| Item | Value |
|---|---|
| OS | Windows 10 Pro (build 19045) |
| CPU | 12 logical cores |
| Runtime | .NET 8.0.29 |
| Engine | Argos Translate 1.11.0 (Python 3.11 venv) via `ArgosTranslationEngine` (child process, line protocol) |
| Language packages | `en↔tl` 1.9, `ja→en` 1.1, `en→ja` 1.1 (direct; `ja→tl` pivots via `en`) |
| Sizes | Argos child process working set grows ~468 MB → ~864 MB as models load |

## Harness

`src/UniversalCaptions.Benchmarks` — `translate` mode (`UniversalCaptions.Benchmarks.exe translate --python <venv-python>`). Feeds distinct live-caption-length sentences through `ArgosTranslationEngine` and measures:

- **First call**: process spawn + model load + first translation (one-time).
- **Steady-state**: per-call latency on distinct texts (Argos caches identical input, which would otherwise read ~0.3 ms).
- **Cached-repeat**: repeated identical text (documents the internal cache, not real latency).
- **Throughput**: characters/second at steady state.
- **Finals stream**: a simulated caption stream of 5 final segments — ordering and per-segment latency.
- **Quality**: character-similarity vs a reference translation per pair.
- **Working set**: Argos child RAM via WMI (Windows only).

## Raw Results

| Pair | First call (load+first) | Steady-state (distinct text) | Throughput | Quality (char-sim) | Cached-repeat |
|---|---|---|---|---|---|
| `en→tl` | **12.8–13.9 s** | ~184 ms | ~368 ch/s | **51.8%** | ~0.3 ms |
| `ja→en` | ~1.0 s | ~56–58 ms | — | **91.7%** | ~0.3 ms |
| `en→ja` | ~1.0 s | ~310 ms | — | **19.6%** | ~0.3 ms |
| `ja→tl` (pivot via `en`) | ~0.7 s | ~1050 ms | — | — | — |

Finals stream: **ordered=True**, per-segment latency **70–220 ms** (all within the ~1 s target; dominated by engine latency, not the child process).

## Findings

### F1 — Model load dominates one-time cost
`en→tl` first call is **12.8–13.9 s** (Argos loads the full `en→tl` model + stanza pipeline). `ja→en`/`en→ja` first call is ~1 s. All subsequent calls are fast (56–1050 ms), so the first-call cost is a process/model-load one-time hit — acceptable for a spike; Slice 4 should warm the engine at startup (or accept a cold-start delay on first captions).

### F2 — Steady-state latency is comfortable for finals
At ~56–310 ms per call, `ArgosTranslationEngine` is far below the per-final caption budget; even the pivot `ja→tl` at ~1050 ms is on the edge but acceptable at MVP scale. No backpressure needed at these rates.

### F3 — Pivoting works but costs
`ja→tl` (no direct model) correctly pivots through `en` (`usedPivot=true`, `pivotLanguage=en`) at ~1050 ms — ~3–5× a direct pair. Keep pivoting available as a fallback; consider direct models later if `ja→tl` becomes an MVP pair.

### F4 — Quality caveats
- `en→tl` 51.8% and `en→ja` 19.6% char-similarity are **low — but these pairs are low-resource and the reference is a machine translation, not a human one**; similarity is a rough proxy, not accuracy. `ja→en` 91.7% shows clean output.
- Japanese output renders correctly when the console is UTF-8 (`Console.OutputEncoding = UTF8`); the benchmark sets this.
- `tl` as a **source** is unsupported by Argos's sentence-boundary detection (see ADR-0006); all MVP pairs use `tl` as a target only.

## Decision (ADR-0006 — recorded 2026-08-01)

- **Protocol**: child process + newline-delimited JSON line protocol (no HTTP/server port; simplest local IPC for a one-shot engine).
- **Pairs**: direct `en→tl`, `ja→en`, `en→ja` installed; `ja→tl` available via pivot through `en` as a fallback.
- **Source auto-detection**: not available in Argos 1.11 (`translate.detect_language` absent); MVP takes the source language from the user's selection.

## Open Follow-Ups

1. Warm-start the engine during Slice 4 startup to avoid the 12.8–13.9 s cold `en→tl` first-call cost in live captions.
2. If `tl`-as-source or `ja→tl` becomes an MVP requirement, source-side SBD and a direct `ja→tl` model are needed (TD-010).
3. Quality measurement should later use human-reference BLEU, not char-similarity.
