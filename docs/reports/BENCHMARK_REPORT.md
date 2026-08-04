# Benchmark Report

Last updated: 2026-08-05

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

1. ~~Slice 6 end-to-end: latency/CPU tuning of window size, decode interval, and `StabilityWindow` against real WASAPI loopback audio~~ — the offline parameter sweep is done in [Slice 6 below](#slice-6--streaming-latencycpu-ofat-sweep) (2026-08-01); real WASAPI loopback + SAPI validation is Slice 6 Phase 1c/2 (App-level E2E, deferred per user).
2. Optional: benchmark `WithSplitOnWord` / `WithMaxSegmentLength` (currently opt-in, default off) for finer caption boundaries.
3. Streamed finals occasionally re-emit overlapping text across epoch boundaries (see S4); tracked as TD-006/TD-007.

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

---

# Slice 6 — Streaming Latency/CPU OFAT Sweep

Date: 2026-08-01

## Purpose

Close Slice 2 open follow-up #1 (offline part): establish a one-at-a-time (OFAT) baseline for the three streaming knobs — `WindowDuration`, `DecodeInterval`, `StabilityWindow` — plus the tiny-vs-base model choice, on the target hardware. The App default is **not** changed by this sweep (changing the default model/pair is a Level-4 Must-Ask).

## Environment

| Item | Value |
|---|---|
| OS | Windows 10 Pro (build 19045) |
| CPU | 12 logical cores (threads per decode: 6) |
| Runtime | .NET 8.0.29, whisper.cpp via Whisper.net 1.9.1 (CPU) |
| Samples | `jfk.wav` (11.00 s, canonical reference), `OSR_us_000_0010_8k.wav` (11 s used here; pseudo-reference from ggml-small full-file decode) |
| Models | `artifacts/models/ggml-base.bin`, `ggml-tiny.bin` |
| Knob defaults (App) | window 8 s, interval 1 s, stability 3, commit overlap 1.5 s, min-audio-before-first-decode 2 s |

## Harness

`src/UniversalCaptions.Benchmarks` (STT mode) was extended with per-run knobs and CSV output:

```bash
dotnet run --project src/UniversalCaptions.Benchmarks --no-build -- \
  --model artifacts/models/ggml-base.bin --sample jfk.wav \
  --feed realtime --window 8 --interval 1 --stability 2 \
  --csv artifacts/reports/ofat/base_jfk_st2.csv
```

New flags: `--window <s>`, `--interval <s>`, `--stability <n>`, `--feed <realtime|fast>`, `--sample <name-substr>` (repeatable), `--csv <path>`. The streamed pass also records **streamed-finals WER** (WER of the concatenated committed finals vs the reference) and streams CPU. Each row is a single run (streaming is timing-sensitive, so run-to-run variance of a few percent exists; see S2/S4).

`--feed fast` is ingest-only: feeding a whole clip faster than realtime gives the arrival-driven loop a single decode pass, so no finals are committed (streamed WER n/a). Realtime pacing is required to measure streaming behavior and is what the sweep used.

## Raw Results (single runs, threads 6)

### base + jfk (11 s, full-file WER 0.0%)

| Config w/i/st | strWER | first partial | first final | avg final lat | finals |
|---|---|---|---|---|---|
| **8 / 1 / 3** (App default) | 72.7% | 4.25 s | 8.60 s | 6279 ms | 2 |
| 6 / 1 / 3 | 77.3% | 4.34 s | 8.55 s | 6242 ms | 2 |
| 10 / 1 / 3 | 77.3% | 4.05 s | 8.20 s | 6237 ms | 2 |
| 8 / 0.5 / 3 | 77.3% | 4.21 s | 8.47 s | 6424 ms | 2 |
| 8 / 2 / 3 | 77.3% | 4.11 s | 8.29 s | 6282 ms | 2 |
| 8 / 1 / **2** | 72.7% | 4.03 s | **6.46 s** | **3794 ms** | 2 |
| 8 / 1 / **5** | 100% (0 finals) | 4.30 s | n/a | n/a | 0 |

### Confirmations

| Sample | Model | Config | full WER | strWER | first final | finals |
|---|---|---|---|---|---|---|
| OSR | base | 8/1/3 | 4.9% | 39.5% | 8.45 s | 5 |
| OSR | base | 8/1/2 | 4.9% | 45.7% | 6.18 s | 9 |
| OSR | base | 6/1/3 | 4.9% | 24.7% | 8.42 s | 5 |
| jfk | tiny | 8/1/3 | 0.0% | 50.0% | 5.11 s | 3 |
| jfk | tiny | 8/1/2 | 0.0% | 40.9% | **3.78 s** | 4 |
| OSR | tiny | 8/1/3 | 16.0% | 53.1% | 5.16 s | 11 |

CSV artifacts: `artifacts/reports/ofat/*.csv` (git-ignored; includes full + streamed transcripts).

## Findings

### S1 — StabilityWindow dominates first-final latency
Cutting `StabilityWindow` 3 → 2 reduces first-final by **~2.1–2.4 s** (base jfk 8.60→6.46 s; base OSR 8.45→6.18 s; tiny jfk 5.11→3.78 s) and roughly halves average final latency (6279→3794 ms on base jfk), with no full-file accuracy change. Raising it to **5 never commits a final on an 11 s clip** (five consecutive identical passes are needed before the window advances) — the worst possible UX. Stability is the knob to tune for perceived latency.

### S2 — Window size and decode interval are minor on short clips
On jfk (11 s) window 6/8/10 s and interval 0.5/1/2 s move first-final by ≤0.5 s and streamed WER by ≤5 points. On OSR, window 6 s graduated more text to finals (strWER 24.7% vs 39.5% at st3): a smaller window restarts epochs sooner, committing early text faster. Variance is high (the same config measured 72.7% vs 77.3% across runs), so these deltas are not decisive — treat window/interval as secondary tuning.

### S3 — tiny commits more text, but that is not accuracy
tiny emitted more finals than base (jfk st3: 3 vs 2; OSR: 11 vs 5) because its faster decodes allow more stability passes per unit of audio, so its streamed WER *looks* better. **Full-file WER is the accuracy signal** (config-independent) and still favors base: 4.9% vs 16.0% on OSR. Do not read streamed WER as accuracy.

### S4 — Streamed-finals WER is a commit-rate proxy, not accuracy
The concatenated finals reconstruct only the committed (stable) prefix. The trailing tail of an utterance is inherently never committed — it stays partial until the speaker stops — so streamed WER vs the full reference counts tail-deletions as errors. Example: base jfk st2 committed only *"And so my fellow Americans ask"* of the 22-word reference. Additionally, across epoch boundaries the committer occasionally **re-emits overlapping text** (OSR st2 finals show *"…round bowls."* and *"…park truck. The"* twice) — the committer's non-prefix fallback re-appends rather than diffing (TD-006/007). Use streamed finals only as a coarse "how fast does text graduate to history" signal.

### S5 — CPU headroom: streaming is ~5× a full-file pass
Streaming re-decodes the growing window every interval, so streamed CPU dwarfs a single full-file decode (base jfk: ~61–65 s CPU over ~12 s wall at threads 6 ≈ **5 cores busy** during active speech; full-file decode was ~13.8 s CPU). This is the dominant background cost of live captions on this machine, not model load or translation.

### S6 — Realtime margin holds
All configs streamed at ≤1.18× realtime wall (base and tiny, threads 6) on jfk/OSR, consistent with Slice 2.

## Shortlist (for Slice 6 Phase 1c real-app validation)

| Rank | Config | Rationale |
|---|---|---|
| 1 | **base / 8 s / 1 s / st2** | Accuracy-first (full WER 0–4.9%) with first-final ~6.2–6.5 s instead of ~8.6 s at the old st3 default; **promoted to the App default (Slice 6 baseline, 2026-08-01).** |
| 2 | **tiny / 8 s / 1 s / st2** | Latency-first (~3.8 s first final, ~2× faster per decode) for low-headroom machines; accepts 16% OSR WER. |
| 3 | **base / 8 s / 1 s / st3** | Previous App default; conservative control for comparison. |

This sweep itself changed no default. After **Phase 1c real-device confirmation** (WASAPI loopback + SAPI + Argos E2E, per the user-approved plan), the validated baseline **base/8/1/st2 was promoted to the App default on 2026-08-01** (`StabilityWindow` 3→2, model `ggml-base` unchanged — one authoritative configuration shared with the benchmark; see `PROJECT_STATUS.md` "Slice 6 Baseline Defaults"). Switching the model to tiny remains a Must-Ask if low-headroom latency becomes the priority after Phase 2.

### Phase 1c confirmation (App-level SAPI E2E, completed 2026-08-01)

The shortlist was validated end-to-end through the real App (loopback → Whisper → Argos en→tl → overlay, baseline + shortlist × 3 runs each); full protocol + evidence in [TEST_REPORT.md](TEST_REPORT.md) (Slice 6 Phase 1c section). Results matched the offline sweep: **tiny/8/1/st2** is the end-to-end latency winner (E2E final median 16.25 s incl. per-session Argos cold start; warm last-final 7.45 s; last STT 3.61 s; 18 translated finals), **base/8/1/st2** commits faster than the old default (16 vs 10 finals; STT 4.18 vs 6.49 s) at identical model accuracy, and **base/8/1/st3** remains a conservative control. The baseline **base/8/1/st2** is now the App default.

# Slice 8 — Tagalog Model-Selection Isolation (2026-08-04)

## Purpose

Classify the reported Tagalog live-caption defects (misrecognitions, fragmented finals,
hallucinated `1.`, missing words) as **STT (Whisper) vs committer** using the real App and the
streaming pipeline, then quantify the accuracy-vs-latency tradeoff across the three locally
available models (`ggml-tiny`/`ggml-base`/`ggml-small`) to decide the default.

## Result — defects are STT, not the committer

RAW Whisper full-file segments on real conversational Tagalog (audio.com "First Meeting —
Meeting Someone", user-supplied; 16 kHz mono; evidence `artifacts/samples/raw_vs_committed_tagalog.log`)
already contain every symptom class before the committer sees the text:

- **Recognition errors:** `Kung usta?`, `Ikao.`, `Salaman.`, `Syangapala.`, `Tagaman nila ako.`,
  `May name is Maria and you.`, `Nagagalaka ko makilalaka.` — present verbatim in RAW.
- **Hallucinated segments:** RAW segments that are literally `1.` (0.52 s, 0.60 s).
- **Fragment boundaries:** Whisper segments Tagalog into 0.5–1.6 s chunks (`Kung usta?` = 0.82 s);
  the committer **aggregates** RAW segments into larger FINALs (FINAL[3] = 7 merged RAW segments),
  it does not manufacture cuts.

The 90 s streamed-harness run reproduced the App's committed FINALs faithfully from the RAW
boundaries. Conclusion: **`siyinobahako`-class errors and fragment boundaries are Whisper model
quality on Tagalog, not `StreamingTranscriptCommitter` logic.** ADR-0007 (commit/boundary/trim)
is not implicated and remains **Proposed** (its acceptance gate is unchanged and separate).

## Model comparison — real App, same 90 s Tagalog slice, STT lang `tl`, frozen config
(StabilityWindow 2, window 8 s, interval 0.5 s, min-audio 0.5 s; UIA-driven Release App, full
`ProcessorCount` threads)

| Model | STT latency | First final | Committed finals | Tagalog accuracy | Hallucinated `1.` | Stop drain |
|---|---|---|---|---|---|---|
| **tiny** | ~1.75 s | ~2.7 s | ~23 (most fragmented) | ❌ `Komosita!`, `guan`, `Salaman`, `Masayang ang marilala ka?` | ❌ `My name is One` | ✅ |
| **base** | ~3.1 s | ~17.5 s | 10 | ❌ `Kung usta`, `Ikaw`, `Mabutirin`, `San ka nakatera` | ❌ `Ang pangalan ko ay 1.` | ✅ |
| **small** | 16.9–21.9 s | ~35.3 s | 4 | ✅ `Kumusta`, `Ikaw`, `Salamat`, `Juan`, `Masaya akong makilalaka` | ✅ none | ✅ |

Evidence: `artifacts/samples/realapp_tiny_tagalog.log`, `realapp_base_tagalog.log`,
`realapp_small_tagalog.log`.

## Findings

### T1 — `ggml-small` fixes recognition but cannot keep real-time pace
All target Tagalog words correct and the `1.` hallucination gone, but STT latency 16.9–21.9 s vs
3.1 s for base; the pipeline lags ~17 s behind live audio at the frozen 8 s/0.5 s config. This is
a real product hit (full `ProcessorCount` threads), not a harness artifact — the harness number
(27.9 s avg) merely overestimated slightly.

### T2 — `ggml-tiny` does not solve the accuracy problem
Fastest (1.75 s) but the same error class as base with different spellings (`Komosita!`, `guan`,
`Salaman`), the `1.` hallucination persists (`My name is One`), and fragmentation is the worst
(~23 finals). It is a latency option, not an accuracy option.

### T3 — no available local model gives both quality and responsiveness
`small` = accuracy, `base`/`tiny` = speed. There is no free lunch among tiny/base/small on this
hardware under the frozen configuration.

## Decision (recorded 2026-08-04, per user)

No production change. **`ggml-base` remains the frozen default** (ADR-0003) because it preserves
acceptable responsiveness. `ggml-small` provides substantially better Tagalog recognition but
incurs unacceptable real-time latency on this setup; `ggml-tiny` is fastest but does not solve
recognition quality. Model exploration is **deferred** — a better Whisper model or
hardware-appropriate quantization is the future investigation, not forcing `small` realtime by
re-tuning window/interval (which would re-confound compute latency with segmentation behavior).
ADR-0007 remains **Proposed/frozen**, decoupled from this model-selection result.

# Slice 9 - Faster-whisper as a Selectable STT Engine (2026-08-04)

## Purpose

Follow up on Slice 8's T3 finding (no available whisper.cpp model gives both Tagalog quality and
responsiveness) by evaluating **faster-whisper** (a CTranslate2-backed implementation of the Whisper
family with int8 quantization and a `small`-level accuracy/latency tradeoff) as a **parallel,
selectable** `ISpeechToTextEngine`. The goal was not to re-tune whisper.cpp, but to close the
"small accuracy + base/near-base responsiveness" gap via a model whose runtime cost per decode is
substantially lower than whisper.cpp's `small`.

## Approach (architecture-strict, no whisper.cpp change)

The whisper.cpp decode portion was extracted to the `ISTTDecoder` seam (`WhisperCppDecoder` owns
`WhisperFactory`/`WhisperProcessor`) with **zero behavior change** to the frozen `ggml-base` path;
the engine's windowing/trim/commit orchestration (`RunLoop`, `DecodeInterval`, `WindowDuration`,
`TrimToCommitted`, `CommittedUntilUtc`, `CommitOverlap`, `StreamingTranscriptCommitter`) is
untouched. `FasterWhisperDecoder` runs a **persistent, binary-framed Python worker**
(`Server/faster_whisper_worker.py`) that loads the faster-whisper model **once** per session
(`model.transcribe` with `language="tl"` when selected, `beam_size=5`,
`condition_on_previous_text=False`, word_timestamps off, float32-normalized int16 PCM).
`FasterWhisperSpeechToTextEngine` is an `ISpeechToTextEngine` wrapper over the shared streaming
engine, selected via `UC_STT_ENGINE=fasterwhisper`; the default `/empty` value keeps **whisper.cpp
`ggml-base`** unchanged.

## Worker round-trip characterization (venv: `%TEMP%\fwv`, faster-whisper 1.2.1, CTranslate2 4.8.1)

| Model / config | Realtime factor (90 s Tagalog) | Segments | Notes |
|---|---|---|---|
| `small` int8, raw int16 PCM (bug) | 0.73× | garbage (`1.`/`2.`) | int16 not normalized → wrong scale + slow |
| `small` int8, float32-normalized PCM | 5.85× | 24 clean bilingual | 15.4 s wall / 90 s; `language="tl"`, `beam 5`, 8 threads, 1 worker |
| `base` int8, float32-normalized PCM | 9.84× | — | faster but poor Tagalog accuracy (consistent with whisper.cpp base) |

## Real-App validation (same 90 s Tagalog slice, STT `tl`, frozen config st2/8 s/0.5 s/0.5 s, full `ProcessorCount` threads, UIA-driven Release App)

| Metric | whisper base | whisper small | **faster-whisper `small` int8** |
|---|---|---|---|
| STT latency | ~3.1 s | 16.9–21.9 s | **10.7–11.7 s** |
| First final | ~17.5 s | ~35.3 s | **16.5–29.9 s** |
| Committed finals | ~10 | 4 | **3–4** |
| Hallucinated `1.`/`one` | yes (`1.`) | none | **none** |
| Tagalog accuracy | weak | best | **whisper-small-level (clean bilingual finals)** |

Evidence: `artifacts/samples/realapp_fasterwhisper_small_tagalog.log`,
`realapp_fasterwhisper_small_int1_5_tagalog.log`.

## Findings

### FW1 — faster-whisper `small` meets the Slice 8 target
On the frozen 0.5 s-interval config, faster-whisper `small` int8 committed clean bilingual Tagalog
finals (**no `1.`/`one` hallucination**) at **10.7–11.7 s** STT latency, versus whisper.cpp small's
16.9–21.9 s. Accuracy was whisper-small-level (comparable correct `Kumusta`/`Ikaw`/`Salamat`/
`Juan` family) while halving the STT latency and keeping the first final on par with base.

### FW2 — a 1.5 s decode interval trades final latency for cleaner boundaries
The 1.5 s-interval variant emitted the cleanest complete sentences (single multi-clause FINAL,
first final 16.5 s ≈ base 17.5 s) but raised the last-final STT latency to ~24 s because the
stability-window confirmation (2 passes at 1.5 s apart) plus the boundary-wait budget now span a
longer wall-clock window. Acceptable for a recorded/minimal-partial use, but not the streaming
default.

### FW3 — two wire-protocol bugs were surfaced only by the real App
The unit-test fake seam did not exercise the wire format. The live run exposed (a) a wrong
little-endian magic constant (`0x55435746` → corrected `0x46574355`, "UCWF") and (b) a 16-byte
segment-header read that should have been 20 bytes (the worker's `"<ddI"` struct is 8+8+4). After
both fixes the run produced transcripts (the pre-fix run committed only `Listening.`). This is a
lasting reason the faster-whisper worker should gain a direct protocol round-trip test (TD-013-style).

**Addressed 2026-08-04 (TD-016):** a deterministic protocol-contract suite now guards both wire bugs
without a Python/venv — `LineProtocolFasterWhisperProcessProtocolTests` (9 tests) drives the real
production reader against a fake-worker byte stream over an injectable `Stream` seam, including the
exact byte-order (`0x46574355`) and the 16-vs-20-byte segment-header regression cases. Full suite
302/302. See CHANGELOG v0.5.14 and TEST_REPORT (TD-016).

## Startup / responsiveness decision-gate measurement (2026-08-04)

Follow-on to the Slice 9 validation, gated on whether faster-whisper `small` warrants promotion to
the default. Measured on the real App (UIA-driven Release build, same 90 s Tagalog slice, STT `tl`,
full `ProcessorCount` threads) and via a direct worker probe.

**Worker cold-start decomposition (direct probe, `small` int8):** process spawn 0.006 s + Python
import + model load **2.6 s** + first 8 s-window decode 2.5 s = **~5.2 s total**. The model load
itself is small and rising early in the window is not the first-caption driver.

**Real-App first-caption & steady-state latency:**

| Metric | faster-whisper `small` int8 | ggml-base (default) |
|---|---|---|
| **First caption** | **16.5–17.4 s** | 25.0 s |
| **STT latency (last final)** | 13.7–15.8 s | **2.4–3.7 s** |
| Committed finals | 7 (composed sentences) | 10 (fragmented) |
| Hallucinated `1.`/`One.`/`May name is` | none | present |
| Tagalog accuracy | clean bilingual | weak |

Evidence: `artifacts/samples/firstcaption_fw_small.log`, `firstcaption_i1_fw_small.log`,
`firstcaption_base.log`, `firstcaption_w4_fw_small.log`.

**Window/interval tuning (faster-whisper `small`):**

| Config | STT latency | Result |
|---|---|---|
| 8 s / 0.5 s (frozen) | 11.7–15.8 s | best |
| 8 s / 1.0 s | 13.7 s | no change |
| 8 s / 1.5 s | 24.2 s | worse (fewer decode passes = slower stability confirmation) |
| 4 s / 0.5 s | no captions | dead end (window too small to commit with StabilityWindow 2) |

**Conclusion: the frozen 8 s / 0.5 s configuration is already close to the practical optimum for
the faster-whisper path; window/interval tuning does not close the steady-state gap.**

**Pre-warm assessment:** the worker model load is ~2.6 s; a pre-warm would move that off the Start
click and shave ~2.6 s off the first caption (≈16.5 s → ≈14 s), but it would not reduce the
steady-state final latency. It is a minor nicety, not a decision-changer.

## Decision (recorded 2026-08-04)

**`ggml-base` remains the frozen default** (ADR-0003). faster-whisper is **opt-in** via
`UC_STT_ENGINE=fasterwhisper` (with `UC_FW_PYTHON` for the interpreter; auto-discovery to
`%TEMP%\fwv`). **No default promotion happens without explicit user approval.** The faster-whisper
path is a validated solution to the Slice 8 T3 gap available on demand, not a replacement for the
frozen baseline.

**Decision-gate close-out (recorded 2026-08-04, per user) — not promoted.** Accuracy winner is
faster-whisper `small` int8; responsiveness winner is `ggml-base`. `ggml-base` stays the production
default because the faster-whisper path introduces a major responsiveness regression in a live-caption
application (steady-state STT latency 13.7–15.8 s vs ggml-base 2.4–3.7 s). The first-caption advantage
(16.5 s vs 25.0 s) and pre-warm (~2.6 s) do not compensate. **faster-whisper `small` remains opt-in
until its steady-state latency can be materially reduced.** No further window/interval tuning is
planned for this gate — it is already near-optimal. This is a clean close: no production change, no
forced promotion; the Tagalog accuracy gap on the ggml-base default is acknowledged as open.

# TD-001 — Resampler benchmark: windowed-sinc vs NAudio `WdlResampler` (2026-08-05)

## Purpose

TD-001 gate: does replacing the current windowed-sinc `<SampleRateConverter>` (Slice 1) with NAudio
`WdlResampler` improve enough real-time STT performance without degrading audio quality/recognition?
Benchmark-only first pass — no production replacement is made here (per TD-001 decision rule).

## Method

`dotnet run --project src/UniversalCaptions.Benchmarks -c Release -- resample --repeats 5`

Same representative speech (canonical `jfk.wav` — clean, and `jfk_noisy.wav` — +10 dB SNR) is the 11.00
s 16 kHz mono ground truth. The 44.1 kHz / 48 kHz sources are created from that same 16 kHz speech via
a reference band-limited upsample, so both candidates downsample **byte-identical input** (fair
head-to-head). Each conversion runs in 0.5 s input chunks (mirroring the `AudioProcessor` pipeline);
reported performance is the best of 5 runs (wall, realtime factor vs clip length, CPU time,
allocation via `GC.GetAllocatedBytesForCurrentThread`, output-frame count). STT impact = ggml-base
full-file decode WER vs the jfk canonical reference for the resampled 16 kHz output.

| impl | mode |
|---|---|
| control | no resampling (pass-through) — the pipeline's no-op behavior; establishes the STT baseline |
| sinc | current `SampleRateConverter` (windowed-sinc, Blackman, ~32 taps @ 44.1k/48k) |
| wdl | NAudio `WdlResampler` (`SetMode(true, 2, false)` interpolate + IIR, feed mode) |

## Raw results

### Resampler performance (best of 5, 0.5 s chunks, mono)

| impl | path | wall | realtime vs clip | cpu | alloc (11 s clip) | out frames |
|---|---|---|---|---|---|---|
| control | 16k->16k | 0 ms | 0.00x | 0 ms | 0 MB | 176000 |
| sinc | 44.1k->16k | 400 ms | 0.04x | 375 ms | 5.7 MB | 175984 |
| wdl | 44.1k->16k | 13 ms | 0.00x | 16 ms | 3.0 MB | 175992 |
| sinc | 48k->16k | 356 ms | 0.03x | 359 ms | 6.1 MB | 175984 |
| wdl | 48k->16k | 13 ms | 0.00x | 16 ms | 3.2 MB | 175992 |

Noisy clip was consistent: sinc 401–411 ms / 5.7–6.1 MB per 11 s; wdl 13–14 ms / 3.0–3.2 MB.

### Audio equivalence / STT impact (ggml-base full-file decode, lang en)

| path | clean WER | noisy WER |
|---|---|---|
| control 16k->16k | 0.0% | 0.0% |
| sinc 44.1k->16k | 0.0% | 0.0% |
| wdl 44.1k->16k | 0.0% | 0.0% |
| sinc 48k->16k | 0.0% | 0.0% |
| wdl 48k->16k | 0.0% | 0.0% |

Decode latency is indistinguishable across rows (≈2.0–2.2 s, 0.19–0.21x) — resampling adds no STT
timing or accuracy signal. Output keeps ~equal length (175984 vs 175992 frames; 0.5 ms delta).

## Findings

### F1 — WDL is ~30x faster and ~half the allocations
WDL converts both 44.1k->16k and 48k->16k in ~13 ms vs ~356–400 ms for the current sinc resampler
(≈28–31x faster, realtime factor 0.00x vs 0.03–0.04x), with ~3.0–3.2 MB vs 5.7–6.1 MB per 11 s clip.

### F2 — STT/audio quality is equivalent, not degraded
Clean and noisy rounds trips both transcribe to **0.0% WER** with either resampler, identical to the
no-resampling control, at equal decode latency and nearly identical output lengths. There is **no
measurable recognition or audio-quality difference** between the two — the WDL speedup costs nothing.

### F3 — the sinc resampler is NOT a live-latency driver
The current sinc resampler already runs at **0.03–0.04x realtime** (≈25-30x faster than live). The real
STT decode runs at ~0.2x — an order of magnitude above the resampler. Per-chunk, replacing sinc with
WDL saves ~0.4 ms per 0.5 s audio chunk (~0.03% of a live caption slice) — it cannot move end-to-end
live-caption latency. The 5.7 MB/11 s sinc allocation is ~0.5 MB/s GC churn, negligible.

## Decision (recorded 2026-08-05) — keep the current windowed-sinc resampler; TD-001 closed

Applying the TD-001 decision rule to the measured numbers:

- WDL is faster (≈30x) and STT-equivalent — a candidate on raw throughput.
- But whether to switch keys on the rule's operative question: **does the switch improve real-time
  STT performance?** No. The current resampler already runs 25-30x faster than realtime and does not
  meaningfully contribute to live-caption latency (decode dominates by >10x); the ~0.4 ms/chunk WDL
  saving is not observable in end-to-end latency, and the current sinc is deterministic and
  dependency-free.
- The last decision row ("current resampler materially contributes to live latency → optimization
  justified") is **false** — it does not.

**Therefore: keep `SampleRateConverter`; do not introduce `WdlResampler` into production. No code
change to the product subgraph.** The resample `resample` benchmark stays available in
`UniversalCaptions.Benchmarks` for a future reassessment if STT is ever offloaded (when resampling
could become a fraction of the pipeline, e.g. a hardware/accelerated STT path). If the sinc
resampler's list/array churn is ever a concern, allocations can be reduced independently of the
algorithm choice — not a reason to switch.

Execution evidence: resample runs on `jfk.wav` + `jfk_noisy.wav`, both listed in
`docs/reports/TEST_REPORT.md` (TD-001). Full suite 302/302, Release build 0 warnings/0 errors.
