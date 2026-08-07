# Benchmark Report

Last updated: 2026-08-07

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

# Slice 10 — Faster-Whisper Native Streaming (segment-based, VAD-gated) — controlled benchmark + real-App validation (2026-08-05)

## Purpose

Answer the Slice 8/9 decision-gate follow-up: does replacing the sliding-window re-decode with
**segment-based streaming** (C#-side VAD; one FINAL per completed speech segment decoded once through the
existing worker protocol; `UC_STT_ENGINE=fasterwhisper-native`) preserve faster-whisper `small` int8's
accuracy advantage (~31% WER) while eliminating the stale ~40 s commit cadence (2 FINALs/120 s)?

## Method

- **Controlled benchmark** — new additive `sttnative` mode (`NativeStreamingBenchmark.cs`) drives the real
  `FasterWhisperNativeStreamingEngine` exactly as the App composes it (`EnergyVad(0.008, 1, 2)`,
  0.3 s / 0.7 s / 8 s segment knobs, `small` int8, `tl`, default `ProcessorCount` threads) from
  `uc_video_full_16k.wav` (288.79 s), realtime feed at 10 ms chunks.
- **Real-App run** — Release App with `UC_STT_ENGINE=fasterwhisper-native`, ffplay → WASAPI loopback,
  STT language `tl` (UIA-driven), the same video audio.
- **Reference** — `fil-orig` auto-captions (`uc_subs_json.fil-orig.json3`); WER via the shared
  `stt_compare.py` normalization. Baselines: ggml-base full-file 51.2%, faster-whisper full-file 31.1%.

## Results

| Metric | faster-whisper native streaming | ggml-base (default) | windowed faster-whisper |
|---|---|---|---|
| Committed WER vs fil-orig | **32.6%** (≈ full-file 31.1%) | 51.2% (full-file) | — |
| Partials | **0 (FINAL-only)** | many | many |
| Commit cadence | **13.3 FINALs/120 s** (one per ~8.2 s segment) | ~12 | **2 FINALs/120 s (~40 s)** |
| First real-App caption | **15.2 s** | 14.8–21.0 s | 27.1 s |
| STT latency (segment start → emit) | 11.6–12.9 s | 2.4–3.7 s | 13.7–15.8 s |
| Behind segment end (staleness at commit) | **~4 s** (decode-bound) | — | stale audio re-committed |
| Recurring `(Song)`/`(Subscribe)` hallucinations | none (music gaps produce no captions) | n/a | n/a |

Decode round-trips measured ~3.5–5.0 s per 8 s segment (≈0.5× realtime) — the worker keeps up at true
realtime, so the commit lag is bounded by segment duration + decode, with **no growing backlog**.

## Controlled-run note

The controlled run's absolute emit-lags (median ~100 s) and 1.59× wall are a **measurement artifact**:
the feed loop's `Thread.Sleep(10)` sleeps ≈15.6 ms on Windows (OS timer granularity), pacing audio at
~1.57× wall. It is not engine behavior; the real-App run (true WASAPI realtime) is the authoritative
latency and shows ~4 s staleness at commit with no backlog.

## Decision (recorded 2026-08-05)

**Slice 10 answered its research question:** segment-based native streaming preserves faster-whisper
`small`'s accuracy advantage (32.6% vs 51.2%) while eliminating the stale 20–40 s commit backlog — one
fresh FINAL per ~8.2 s segment, ~4 s behind segment end, FINAL-only. faster-whisper stays **opt-in**
(`UC_STT_ENGINE=fasterwhisper-native`); the **ggml-base production default is unchanged (frozen)**.
Documented tradeoff: the 8 s `MaxSegmentDuration` cap can split a sentence mid-word (tunable via
`UC_NATIVE_MAX_SEGMENT`). Promotion to default is out of scope (freeze) and would be a separate decision.
Evidence: `TEST_REPORT.md` (Slice 10), CHANGELOG v0.5.19; raw logs
`artifacts/samples/realapp_native_streaming.log`, `%TEMP%\opencode\sttnative_small_realtime.log` (+ `.csv`,
`hyp_sttnative_small.txt`).

# Slice 11 — Native-Streaming Segment-Boundary Tuning: max-segment 8/10/12 s sweep (2026-08-05)

## Purpose

Per user after the Slice 10 PASS: tune the opt-in `fasterwhisper-native` segment boundaries. Test
`MaxSegmentDuration` around 8/10/12 s; measure whether longer segments reduce mid-sentence splits;
confirm latency/backlog stays bounded; keep `SilenceHangover = 0.7 s` fixed; change no worker
protocol / ggml-base / windowed-engine path. Goal is **accurate + natural sentence boundaries + bounded
live latency**, the legitimate basis for any future default-selection decision — not WER alone.

## Method

- Additive `sttnative` benchmark improvements: `timeBeginPeriod(1)`/`timeEndPeriod(1)` around the
  realtime feed (fixes the Slice 10 `Thread.Sleep(10)` ≈ 15.6 ms timer-granularity pacing artifact →
  controlled pacing now ~1.1× realtime) and a mid-sentence-split metric (FINALs ending without
  terminal punctuation = forced boundary cuts; short fragments ≤2 words and unterminated).
- Three runs on `uc_video_full_16k.wav` (288.79 s) vs the `fil-orig` reference, small int8, `tl`,
  realtime feed, `--max-segment 8/10/12`, hangover fixed 0.7 s. WER via the shared `stt_compare.py`
  normalization. Logs: `%TEMP%\opencode\sttnative_max{8,10,12}.log`/`.csv`/`hyp_sttnative_max{8,10,12}.txt`.

## Results

| Metric | max 8 s | max 10 s | max 12 s |
|---|---|---|---|
| FINALs | 32 | 26 | 22 |
| Commit cadence | **13.3 FINALs/120 s** (~9 s/caption) | 10.8 (~11 s) | 9.1 (~13 s) |
| WER vs fil-orig (norm) | 32.6% | 33.2% | **30.0%** |
| Partials | 0 | 0 | 0 |
| Mid-sentence splits (unterminated FINALs) | **10/32 (31%)** | 11/26 (42%) | 10/22 (45%) |
| Short fragments (≤2 words, unterminated) | 0 | 1 | 1 |
| Stop flush | none (last speech seg committed before music tail) | 1 | 1 |
| End-of-audio cap behavior | clean | `Pag-pag-pag…` stutter on capped segment spanning the music tail | truncated `tunog` fragment |
| Realtime factor (wall/audio) | 1.13× | 1.11× | 1.14× |
| Emit lag behind segment end | ~5 s (steady, no growth) | ~5 s (steady) | ~5 s (steady) |

## Findings

1. **Longer segments do NOT reduce mid-sentence splits.** The split *fraction* worsens 31% → 42% →
   45%. The cap still force-closes mid-sentence during continuous speech; a longer cap just does it
   less often while each forced cut now discards more in-flight content (e.g. at 12 s: FINAL 14 ends
   mid-word "…pagpapahapag-", and the final sentence is cut across FINAL 21/22 into a bare `tunog`).
   The 8 s default has the lowest split fraction.
2. **Latency/backlog is bounded at all three caps.** Emit stays ~5 s behind each segment's speech end
   with no growth; worst decode ~8 s for a capped 12 s segment — still < segment length, so the worker
   keeps up at realtime (all runs 1.11–1.14× wall).
3. **Longer caps add end-of-audio cap risk.** A segment force-closed at the cap that spans into the
   music tail decoded as a `Pag-pag-pag…` stutter (10 s) and a truncated `tunog` (12 s); at 8 s the
   last speech segment commits before the tail (clean). This is the Entry 12 §8 flagged risk (longer
   segments bridging non-speech).
4. **12 s WER gain is a boundary artifact, not a decoding gain.** 30.0% vs 32.6% comes from fewer
   force-close boundaries; it costs ~46% responsiveness (9.1 vs 13.3 FINALs/120 s, captions every
   ~13 s instead of ~9 s).
5. **8 s reproducibility:** the 8 s run reproduces Slice 10's WER exactly (32.6%), confirming the
   timer fix did not alter accuracy and the controlled run is now a valid pacing baseline.

## Decision (recorded 2026-08-05)

**Keep `MaxSegmentDuration = 8 s`** as the native engine's default — no production or knob-default
change. The sweep shows the 8 s cap is the best balance of boundary naturalness (lowest split fraction),
responsiveness (highest cadence), and robustness (no end-of-audio cap hallucinations). The kept default's
real-App latency/backlog evidence is the Slice 10 real-App run
(`artifacts/samples/realapp_native_streaming.log`); no redundant re-run was needed. Worker protocol /
ggml-base / windowed-engine paths untouched. Evidence: `TEST_REPORT.md` (Slice 11), Entry 12,
  CHANGELOG v0.5.20; raw logs `%TEMP%\opencode\sttnative_max{8,10,12}.log` (+ `.csv`, `hyp_sttnative_max{8,10,12}.txt`).

# Slice 12 — Faster-Whisper Native-Streaming Live Partials: Chrome-Live-Caption-style on the opt-in engine (2026-08-05)

## Purpose

After the Slice 10/11 gate closed with the "one FINAL per completed segment, 0 live partials"
tradeoff, the goal is the Chrome-Live-Caption-style experience on `fasterwhisper-native` only:
incremental live partial text while the speaker is still talking, a stable FINAL at/near speech end,
no wire-protocol change, translation OFF, ggml-base untouched. The key measurement is **first
visible partial latency** (T4 = speech onset → first partial), not first FINAL.

## Method

Additive implementation: `SpeechSegmentDetector.TryGetPartial` (bounded trailing-window snapshot of
the in-progress segment; refused while idle/hangover/after close; capture time = window start),
`FasterWhisperEngineOptions.PartialDecodeInterval` (default 0 = disabled) + `PartialDecodeWindow`
(4 s), and `FasterWhisperNativeStreamingEngine` cadence dispatch with at most one partial decode in
flight/queued (ticks deferred, not queued). Benchmark `sttnative` gains `--partial-interval` /
`--partial-window`, a first-partial/first-caption-lag/partial-cadence/lag-distribution metric block,
and a CSV partial table + summary columns. FINAL-only behavior (Slice 10/11) is byte-for-byte
preserved when the interval knob is left at 0.

One controlled run, identical composition to Slice 10/11 (small int8, tl, hangover 0.7 s, max
segment 8 s, realtime feed) on `uc_video_full_16k.wav` (288.79 s) vs the `fil-orig` reference, with
`--partial-interval 1 --partial-window 4`. Translation OFF.

## Results (vs the Slice 11 FINAL-only 8 s baseline)

| Metric | Slice 11 (FINAL-only) | Slice 12 (partials ON) |
|---|---|---|
| First visible caption | 18.85 s (first FINAL) | **9.19 s (first partial)** |
| First caption lag (onset → caption) | n/a | **5.59 s (T4)** |
| Caption cadence | 13.3 FINALs/120 s | 19.5 partials/120 s (+ 13.3 FINALs/120 s) |
| Active-line updates while speaking | none (FINAL-only) | ~3 s apart during speech |
| FINALs | 32 | 32 (**text-identical**) |
| WER (in-harness) | 33.19% | 33.19% (identical stream; no regression) |
| Max FINAL emit-lag | 43.7 s | 56.7 s (one 17.5 s decode spike; plateau ~50 s) |
| Realtime factor | 1.13× | 1.18× (partial decodes add ~5 % wall) |
| Dropped/reordered captions | none | none |
| Stop flush | none | none |
| Hallucination/repetition | baseline | no new artifacts |

Live partial behavior (first segment): `PARTIAL "Magandang"` (9.19 s) → `"ang buhay, ako sigino
ang galbes, ang gurun nyo para sa asignot…"` (12.26 s) → `"pabun nyo para sasignoturong instruksyon
ng Wicang Filipino."` (15.26 s) → `FINAL[1]` full sentence (18.62 s). The active overlay line
replaces with each partial, then the FINAL.

## Findings

1. **First-visible-partial is the win:** the first partial appears 5.59 s after speech onset — ~13 s
   earlier than the FINAL-only first caption — and updates every ~3 s while the speaker continues.
   This is the Chrome-like behavior.
2. **No accuracy regression:** the FINAL stream is text-identical to the Slice 11 8 s run (32/32);
   in-harness WER 33.19% both (the report's 32.6% is the `stt_compare.py` normalization, not used
   here).
3. **Bounded but elevated latency:** partial decodes add ~5 % wall (1.13× → 1.18×) and push the tail
   FINAL emit-lag plateau to ~50 s (vs ~43 s FINAL-only); one 17.5 s decode spike (machine
   contention) produced the 56.7 s max. The plateau is flat — no growing backlog; nothing dropped;
   the last FINAL emitted before the feed completed.
4. **Rolling-window tradeoff (expected):** the 4 s partial window shows a rolling 4 s of the segment;
   the FINAL then reveals the earlier words not shown by the last partial. Chrome Live Caption shows a
   similar rolling window; a wider window would raise partial decode cost.
5. **Partials inherit the baseline's ASR quirks** (e.g. the "Paano kong?" repetition) — they are
   re-decodes of the same audio, not new artifacts.

## Decision (recorded 2026-08-05)

**PASS** — Slice 12 closes out. `ggml-base` remains the production default; faster-whisper stays
opt-in; this benchmark does not constitute promotion. The partial knobs default off
(`PartialDecodeInterval = 0`), so production behavior is unchanged unless a user opts in via
`UC_STT_ENGINE=fasterwhisper-native` + `UC_NATIVE_PARTIAL_INTERVAL=1`. Evidence:
`TEST_REPORT.md` (Slice 12), Entry 13, CHANGELOG v0.5.21; raw log
`%TEMP%\opencode\sttnative_partials_slice12.log` (+ `.csv`).

## Promotion (Entry 14, 2026-08-05 — ADR-0008)

**The Slice 12 decision above was superseded by the user-approved production promotion (Entry 14 /
ADR-0008):** `fasterwhisper-native` + live partials (interval 1 s, window 4 s) is now the production
STT default via `SpeechEngineFactory`; `ggml-base` is the explicit fallback
(`UC_STT_ENGINE=ggml-base`). This benchmark is the validation evidence behind the promotion (first
visible partial 5.59 s, FINAL stream text-identical to Slice 11, WER 33.19% in-harness, backlog
bounded). No new benchmark runs were required for the promotion — the selected configuration is
exactly the one measured above.

# Entry 16 — CPU-thread gate: `--threads 12` vs `--threads 4` (2026-08-06)

## Purpose

The promoted path sustained ~77% of the machine (STT worker, all 12 cores per decode). Decision:
cap decode threads at 4 (CPU optimization slice). Gate: prove the cap causes **no caption regression**
— decode wall, realtime factor, FINAL stream, and WER must be unchanged.

## Method

`sttnative` (now with `--threads`) run twice on `uc_video_full_16k.wav` (288.79 s) vs the `fil-orig`
reference, identical Slice 12 composition (small int8, tl, hangover 0.7 s, max segment 8 s, partials
interval 1 s / window 4 s, realtime feed): once at `--threads 12` (pre-fix baseline) and once at
`--threads 4` (the new production default).

## Results

| Metric | threads=12 | threads=4 |
|---|---|---|
| first FINAL | 17.98 s | 18.12 s |
| FINALs | 32 (32 feed / 0 flush) | 32 (31 feed / 1 flush) |
| **WER (committed, vs `fil-orig`)** | **33.2%** | **33.2%** |
| **wall vs audio (realtime)** | **1.18×** | **1.18×** |
| first partial | 13.27 s | 13.30 s |
| emit-lag vs segStart (min/med/max) | 14.38 / 39.95 / 58.64 s | 14.51 / 41.27 / 59.67 s |
| mid-sentence splits | 10/32 | 10/32 (same split points) |
| short fragments | 0 | 0 |

**FINAL transcript text is 100% identical between the two runs (0 textual diffs across all 32 FINALs).**

## Decision

**PASS — cap the production default at `Threads = 4`** (`UC_NATIVE_THREADS`, default 4, clamped).
Decode wall is thread-count-invariant for real speech (consistent with the Entry 16 decode sweep);
capping at 4 cuts sustained STT worker CPU from ~77% to ~32% of the machine (2.4×) with an identical
caption stream and no latency/backlog change. Real-App CPU evidence + full 382/382 test suite in
`TEST_REPORT.md` (Entry 16); raw logs `%TEMP%\opencode\cpu_gate_t12.log/.csv` and
`cpu_gate_t4.log/.csv`.

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

# Post-core — Tagalog model sweep: does a bigger faster-whisper model beat production? (2026-08-06)

## Purpose

Post-core product experiment (production frozen at v0.5.25). The remaining product gap is Tagalog
accuracy (~33% committed WER on the lecture corpus). Answer: is there a faster-whisper model that
materially beats the production `small` int8 without sacrificing the Chrome-like live-caption behavior
(first caption ~3 s, live partials, bounded backlog, realtime-safe)?

## Method

Identical production composition for every run: `sttnative` (NativeStreamingBenchmark) on
`uc_video_full_16k.wav` (288.79 s real Tagalog lecture) vs the `fil-orig` reference, `tl`, hangover
0.7 s, max segment 8 s, partials interval 1 s / window 4 s, realtime feed, 10 ms chunks, `--threads 4`,
int8. Candidates: `small` (production baseline), `base`, `tiny` (all cached locally), then `medium`
(downloaded, ~1.5 GB) per user decision. Decode-cost side-by-side via the Entry 16 worker round-trip
sweep (8 s speech slice, 4 threads, tl). Logs: `%TEMP%\opencode\tl_sweep_{small,base,tiny,medium}.log/.csv`.

## Results

| Metric | **small (PROD)** | base | tiny | medium |
|---|---|---|---|---|
| **WER (committed, vs `fil-orig`)** | **33.2%** | 54.1% | 76.2% | **29.4%** |
| first FINAL | 16.5 s | 14.1 s | 13.1 s | 28.3 s |
| first partial | 8.2 s | 6.9 s | 6.5 s | 20.2 s |
| partials per 120 s | 28.3 | 72.3 | 64.8 | **3.7** |
| FINALs per 120 s | 13.3 | 13.3 | 9.6 | 12.1 |
| wall vs audio | 1.12× | 1.10× | 1.09× | 1.15× |
| emit-lag vs segStart (med / max) | 27.7 / 39.9 s | 23.4 / 37.2 s | 22.3 / 34.4 s | **59.9 / 85.6 s** |
| mid-sentence splits | 10/32 | 4/32 | 6/23 | 6/29 |
| short fragments | 0 | 0 | 1 | 0 |
| decode cost (8 s slice, 4 thr) | 2.86 s wall, **1.43 cpu-s/s** | — | — | 8.87 s wall, **4.43 cpu-s/s** |
| single-slice realtime | 2.79× | — | — | **0.90×** (below realtime) |

## Findings

- **Within the cached family, accuracy scales with model size and `small` is best.** base (+21 pts WER)
  and tiny (+43 pts) are decisively worse; their only advantages are faster first captions and fewer
  splits (sentence coherence), which do not compensate.
- **`medium` is an accuracy-only candidate, not a production candidate.** It improves WER ~4 pts
  (33.2% → 29.4%) but the live-caption behavior degrades sharply: decode cost is **3.1×** (1.43 → 4.43
  CPU-seconds per audio-second), single-slice decode drops **below realtime (0.90×)** — a continuous
  lecture therefore backlogs: emit-lag median roughly doubles (27.7 → 59.9 s, max 39.9 → 85.6 s), first
  partial slips 8.2 → 20.2 s, and partial cadence collapses 28.3 → 3.7 per 120 s (partials barely flow).
  2 FINALs were still in the queue at Stop (flushed). This fails the user's promotion rule ("must not
  sacrifice the Chrome-like live-caption behavior").
- **Hallucination/repetition is input-driven, not model-size-driven.** All models stutter the same
  `Paano kong?`/`Paano kung?` ×6 on the same segment (FINAL 2) — a corpus/audio artifact, present in
  small, base, and medium identically. `tiny` adds 1 short fragment. No model produces extra
  end-of-audio cap stutter at the 8 s cap (Slice 11 finding unchanged).
- **CPU-only is the fundamental blocker for Tagalog accuracy.** The accuracy lever is model size, and
  the only sizes that beat `small` (medium +~4 pts, large-v3 ~+10–15 pts expected) decode too slowly on
  this 12-core CPU machine to stay realtime for continuous speech. A materially better Tagalog WER
  requires a GPU/accelerated STT path — out of scope for the frozen core.

## Decision

**No promotion. Production default stays `fasterwhisper-native` + `small` int8 (v0.5.25 frozen).**
`base`/`tiny` rejected on accuracy; `medium` recorded as an **accuracy-only candidate** (WER 29.4%)
with documented live-caption cost (below-realtime decode, ~2× backlog, collapsed partial cadence) — a
candidate only for a future hardware-accelerated or offline-batch path. `large-v3` was **not
benchmarked** (per user; ~3 GB download, and CPU decode is projected to be ~2–3× realtime, i.e. it
would fail the same live-caption gate by a wider margin). Tagalog accuracy improvement is closed as a
CPU-feasibility dead end for the frozen core; it remains an open product item pending a hardware path
or a user verdict that the accuracy gap is unacceptable.

# Translation provider benchmark: Argos vs OPUS-MT vs NLLB (2026-08-06)

## Purpose

Post-core product experiment (production frozen at v0.5.26). The Phase 2 diagnosis (Chrome vs
UniversalCaptions divergence) isolated the remaining product gap to **translation quality** — the STT,
capture, committer, and overlay boundaries were all proven clean (A/B/E in the diagnosis). The App
ships Argos Translate (`translate-en_tl-1_9`) for en→tl. Answer: is there a local, offline, fast
enough translation provider that produces materially more natural Filipino than the shipped Argos
engine, while staying within the live-caption CPU budget?

## Method

- **Input corpus is fixed and identical for every candidate**: the exact 11-line English STT transcript
  (`english_corpus.txt`, repo root, untracked) extracted from the real practice-group audio
  (`english_sustained_90s.wav` looped in the Phase 2 VLC leg). Translation-only — no STT contamination.
- **Argos (baseline)**: drives the real bundled `argos_translate_server.py` over line protocol (same
  process + model the App uses), `source=en`, `target=tl`, exactly as `ArgosTranslationEngine` does.
- **OPUS-MT**: the official `Helsinki-NLP/opus-mt-en-tl` converted to CTranslate2 int8 via
  `ct2-transformers-converter` (the `gaudi/opus-mt-en-tl-ctranslate2` HF repo is **broken** — it has no
  `model.bin`; `manancode/opus-mt-en-tl-ctranslate2-android` failed HF download with a 500). Runs
  CTranslate2 + sentencepiece in the installed bundle python.
- **NLLB-200-distilled-600M**: `JustFrederik/nllb-200-distilled-600M-ct2-int8` (pre-converted CT2,
  622 MB int8) in the bundle python. Tokenized with the `tokenizers` lib only (`tokenizer.json`, no
  transformers dependency), `eng_Latn` source prefix + `tgl_Latn` target prefix.
- Decode params kept equal across the CT2 candidates (`beam_size=4`, `max_decoding_length=256`,
  `repetition_penalty=1.3`). Per-line wall-clock latency, model load time, first-translation latency,
  peak working set (CT2 in-process via psapi; Argos via CIM command-line match).
- Harness: `%TEMP%\opencode\txbench\harness_txbench.py`; outputs in
  `%TEMP%\opencode\txbench\out\{argos,opus-mt,nllb}.txt`.

## Results

| Metric | **Argos (PROD)** | OPUS-MT | NLLB |
|---|---|---|---|
| **model load** | 3.7 s (process+model) | 275 ms | 1.8 s |
| **first translation** | 2.5 s | 128 ms | 782 ms |
| **avg / line** | 311 ms | 344 ms | 577 ms |
| **max / line** | 2.5 s | 2.8 s | 930 ms |
| **peak RSS** | 500 MB | 316 MB | 1.14 GB |
| **same-model lineage?** | **yes — this IS OPUS-MT** | (baseline) | no |
| **repetition loops** | 0/11 | **2/11** (lines 3, 4) | 0/11 |
| **license** | MIT (Argos) + OPUS-MT | **Apache-2.0** | **CC-BY-NC-4.0** |

Line-by-line output (Argos → OPUS-MT → NLLB), selected lines:

| # | EN source | Argos (PROD) | OPUS-MT | **NLLB** |
|---|---|---|---|---|
| 1 | Hello and welcome to the first meeting of our Conversational Tagalog Practice Group. | `...malugod na tanggapin sa unang pulong ng aming Conversional Tagalog Practice Group.` | `...tanggapin ... ating Conversional Tagalog Tagalog Practice Group.` | `Hello at welcome sa unang pagpupulong ng aming Talking Tagalog Practice Group.` |
| 2 | My name is Maria. | `Ang pangalan ko ay Maria.` | `Ang pangalan ko po ay Maria.` | `Si Maria ang pangalan ko.` |
| 3 | What is your name? | `Ano ang pangalan mo?` | **loop** (`at ano ang pangalan mo?` ×40+) | `Ano ang pangalan mo?` |
| 4 | Good morning everyone. | `Magandang umaga lahat.` | **loop** (`Magandang umaga bawa't isa, maganda kayong lahat ng lahat…`) | `Maligayang umaga sa lahat.` |
| 9 | …speak slowly and clearly, and to listen carefully to each other. | `…magsalita nang mabagal at malinaw, at makinig na mabuti sa isa't isa.` | `…makinig na mabuti.` (drops the tail) | `…magsalita nang mabagal at malinaw, at pakinggan ang isa't-isa.` |
| 11 | Great work everyone, that is the end of today's practice session. | `Dakilang gawa ang lahat, iyan ang wakas ng kasalukuyang sesyon ng pagsasanay.` | `Malaking gawain ang lahat, iyan ang katapusan ng sesyon ng pagsasanay sa ngayon.` | `Maganda na trabaho lahat, iyon ang pagtatapos ng sesyon sa pagsasanay ngayon.` |

## Findings

- **Argos en→tl IS Helsinki-NLP OPUS-MT en→tl.** The shipped `translate-en_tl-1_9` package contains
  `sentencepiece.model` (826,681 B) and `model/shared_vocabulary.json` (1,106,323 B) — **byte-identical
  to the official OPUS-MT files** (verified by SHA-256); `model.bin` differs only by Argos's export
  format (`add_source_eos=true`, `decoder_start_token=<s>`). So "Argos vs OPUS-MT" is **the same
  translation model**; switching to a raw OPUS-MT CTranslate2 model is not a quality change.
- **Raw OPUS-MT alone is worse, not better**: without Argos's stanza SBD + post-processing it
  degrades into long repetition loops (2/11 lines) and drops tails (line 9). This confirms Argos's
  wrapper adds real value on top of the model — and that the ceiling of the whole OPUS-MT family is
  the register/quality ceiling of the same model.
- **The real differentiator is NLLB, and it is materially more natural.** `pagpupulong` (meeting) vs
  `pulong`; `Maligayang umaga sa lahat` vs `Magandang umaga lahat`; `Si Maria ang pangalan ko` vs
  `Ang pangalan ko ay Maria`; `Maganda na trabaho` vs `Dakilang gawa`; `pakinggan` vs `makinig na
  mabuti`; no `Conversional` false-friend. NLLB reads like spoken Tagalog, not model-ese, and has
  **zero repetition loops**. This is the direction the Chrome-comparison gap points to.
- **Argos has a rare first-call hallucination**: under some process states it prepends a spurious
  `Eksistensiyal` and shifts to `tinanggap/ating` on line 1 (observed repeatedly in the corpus run,
  absent when line 1 is translated first in a fresh process). A reliability note, not the main driver.
- **NLLB costs are real but live-caption-compatible**: avg 577 ms/line (vs 311 ms Argos) — both far
  under the caption cadence budget (FINALs flow every few seconds, partials every ~1–3 s); peak RSS
  1.14 GB vs 500 MB (fits the machine easily); model load 1.8 s on cold start. No offline-violating
  dependency (tokenizers-only, no transformers).
- **License is the blocking decision (Must-Not-Decide, AGENT_DECISION_POLICY):** OPUS-MT is
  Apache-2.0 (commercial OK); NLLB-200 is **CC-BY-NC-4.0 (non-commercial)** as published by Facebook
  — using it in a distributed commercial product requires a license verdict from the user (or a
  commercially-licensed NLLB variant, e.g. Meta's `nllb-200-distilled-600M` under commercial terms or
  an Apache/MIT conversion).

## Decision

**No provider change made — docs-only benchmark.** The evidence shows the product gap is translation
register, and NLLB is the strongest local, offline, CPU-feasible candidate (natural Filipino, no
loops, fits the live budget) with one open blocker: **CC-BY-NC-4.0 licensing is a user decision, not
an agent decision.** OPUS-MT is dismissed as a separate candidate because Argos already ships it
(same model, better wrapper). Presentation to the user with license facts is the next step; any
provider swap would be a user-approved product change (ADR + implementation), not an agent default.

## User verdict (2026-08-06) — NLLB NOT promoted

User decision (directive, not agent-decided): **do not promote NLLB.** The CC-BY-NC-4.0
(non-commercial) license makes it **unsuitable as the production provider** if the app may be
distributed commercially. Standing decisions:

1. **Argos en→tl stays the production baseline** — no core changes.
2. **NLLB is an experimental/reference result only** — documented for its better naturalness but
   explicitly **not production-eligible because of CC-BY-NC-4.0**.
3. **Continue searching for a commercially usable local EN→TL model/provider**, prioritizing in
   order: permissive commercial license (Apache/MIT/BSD or equivalent) → offline operation →
   reasonable CPU latency → good Filipino naturalness → small enough for the existing installer.
4. **Benchmark every next candidate against the exact same transcript** (`english_corpus.txt`) that
   exposed the Argos problem.
5. **Promotion rule (user-specified):**

   > Better Filipino than Argos + commercially usable license + live-caption latency acceptable on
   > this machine.

6. **Do not sacrifice the licensing requirement just because NLLB sounds substantially better.** If
   no qualifying replacement is found, **Argos remains the production provider** and NLLB stays an
   experimental benchmark result.

## Candidate 3 — MADLAD-400-3B-MT (Apache-2.0): DISQUALIFIED (2026-08-06)

Follow-up per the user's directive to keep searching for a commercially usable local EN→TL model.
**`google/madlad400-3b-mt`** (Google, **Apache-2.0** — commercial OK, offline-capable) is the strongest
permissively-licensed multilingual MT (covers `tgl` as `<2fil>`). Tested via the pre-converted CT2
int8 (`Heng666/madlad400-3b-mt-ct2-int8`, 2.8 GB) in the bundle python, T5-style
(`<2eng>` source prefix → `<2fil>` target prefix).

**Results on the same corpus:**

| Metric | **Argos (PROD)** | MADLAD-400-3B-MT |
|---|---|---|
| **avg / line** | 311 ms | **2.8 s** (beam4+no-rep2) to **22.7 s** (beam4+rep-penalty) |
| **max / line** | 2.5 s | 22.7 s |
| **quality** | stilted but on-topic | **verbose/hallucinating** (`My name is Maria.` → `narito ka na, Maria…`; line 1 → a rambling paragraph) |
| **model size** | 500 MB process | **2.8 GB int8** (~3× the STT model) |
| **license** | MIT (Argos) + OPUS-MT | Apache-2.0 ✓ |

**Verdict: fails the user's promotion rule on both latency and quality.** The 3B-parameter T5 decoder
is far too slow on this 12-core CPU for live captions (8.5–73× Argos per line), its output on short
caption lines is verbose and off-target, and 2.8 GB blows the installer budget. Even at 12 threads
and aggressive decoding the floor is ~2.8 s/line. **Not a production candidate on this machine.**

# Tagalog Naturalizer — Human-Rated Quality Pass, Argos en→tl (2026-08-07)

Human evaluation of the deterministic `TagalogNaturalizer` (13-rule phrase table) on the same
practice-session corpus as the translatelive run, scored by a Filipino listener. Purpose: decide
whether the naturalizer materially improves naturalness before any bigger architectural move
(Argos replacement, rule expansion, or production wiring).

## Method

- Sample: `artifacts/samples/english_sustained_90s_16k.wav` (88.15 s, en→tl). The corpus loops the
  same practice-session content twice, so the 22-row Argos run contains 12 **distinct** captions
  (rows 13–22 are verbatim repeats of rows 2–11). The 12 distinct lines are the independent
  evaluation units.
- Evidence: `artifacts/reports/translatelive/argos_naturalized_2026-08-07.csv` (per-row original +
  naturalized) and `argos_vs_gemini_english90s_2026-08-07.csv` (Gemini reference, assembled into
  complete sentences).
- Scoring per line, **Naturalized output only** (original Argos and Gemini were compared pairwise
  via the Preference column, not scored in isolation):
  - **Naturalness** 1 = very unnatural · 3 = understandable but awkward · 5 = sounds like a
    Filipino would naturally say it.
  - **Meaning** 1 = changed · 3 = partially preserved · 5 = exact meaning preserved.
  - **Preference** Original Argos / Naturalized / Gemini / Tie — "which would you show a Filipino
    viewer?", the crucial test, not similarity to Gemini.

## Results

| # | English (STT source) | Original Argos | Naturalized | Gemini reference | Nat | Meaning | Pref |
|---|---|---|---|---|---|---:|---|
| 1 | Hello and welcome to the first meeting of our Conversational Tagalog Practice Group. | Hello at malugod na tanggapin sa unang pulong ng aming Conversional Tagalog Practice Group. | Kamusta at maligayang pagdating sa unang pulong ng aming Conversational Tagalog Practice Group. | Kamusta at maligayang pagdating sa unang pulong ng ating conversational Tagalog practice group. | 5 | 5 | N |
| 2 | My name is Maria. | Ang pangalan ko ay Maria. | (unchanged) | Ang pangalan ko ay Maria. | 5 | 5 | Tie |
| 3 | What is your name? | Ano ang pangalan mo? | (unchanged) | Ano ang pangalan mo? | 5 | 5 | Tie |
| 4 | Good morning everyone. | Magandang umaga lahat. | (unchanged) | Magandang umaga sa inyong lahat. | 4 | 5 | G |
| 5 | Today we will practice greetings and introductions. | Sa ngayon ay magsasanay tayo ng mga pagbati at pambungad. | Ngayon ay mag-eensayo tayo ng mga pagbati at pagpapakilala. | Ngayon ay mag-eensayo tayo ng pagbati at pagpapakilala. | 5 | 5 | N |
| 6 | Please open your notebooks to the first page. | Pakisuyong buksan ang inyong mga kuwaderno sa unang pahina. | Pakibuksan ang inyong mga kuwaderno sa unang pahina. | Pakibuksan ang inyong mga notebook sa unang pahina. | 5 | 5 | Tie |
| 7 | Thank you for listening. | Salamat sa inyong pakikinig. | (unchanged) | Salamat sa pakikinig. | 4 | 5 | G |
| 8 | We will see you next week. | Makikita ka namin sa susunod na linggo. | Magkikita tayo ulit sa susunod na linggo. | Magpapatuloy tayo sa susunod na linggo. | 5 | 5 | N |
| 9 | Remember to speak slowly and clearly, and to listen carefully to each other. | Tandaan na magsalita nang mabagal at malinaw, at makinig na mabuti sa isa't isa. | (unchanged) | Tandaan na magsalita nang dahan-dahan at malinaw. At makinig nang mabuti sa isa't isa. | 5 | 5 | N |
| 10 | We will start with the numbers 1 through 10, then move on to the days of the week. | Magsisimula tayo sa numerong 1 hanggang 10, pagkatapos ay lilipat tayo sa mga araw ng sanlinggo. | (unchanged) | Magsisimula tayo sa mga numero 1 hanggang 10, pagkatapos ay lilipat sa mga araw ng linggo. | 4 | 5 | G |
| 11 | Great work everyone, that is the end of today's practice session. | Dakilang gawa ang lahat, iyan ang wakas ng kasalukuyang sesyon ng pagsasanay. | Magandang trabaho sa inyong lahat, iyan ang katapusan ng ating sesyon sa pagsasanay. | Magaling. Magandang trabaho sa inyong lahat. Iyan ang katapusan ng ating sesyon sa pagsasanay. | 4 | 5 | G |
| 12 | Hello and welcome to the first meeting of our conversational tagalog practice group. | Hello at malugod na tanggapin sa unang pulong ng aming grupong nag - uusap - usap na tangalog. | Kamusta at maligayang pagdating sa unang pulong ng aming grupong nakikipag-usap-usap na tagalog. | Kamusta at maligayang pagdating sa unang pulong ng ating conversational Tagalog practice group. | 3 | 4 | G |

**Aggregate (12 distinct lines):**

| Candidate | Naturalness (mean) | Meaning (mean) | Preference (12-line) | Preference (22-line weighted) |
|---|---|---:|---:|---:|
| Original Argos | — (not isolated-scored) | — | **0** | **0** |
| Naturalized Argos | **4.5** | **4.9** | **4** | **7** |
| Gemini | — (not isolated-scored) | — | **5** | **9** |
| Tie | — | — | **3** | **6** |

User-stated aggregate for Naturalized output: **Naturalness 4.5/5, Meaning 4.8/5** (the arithmetic mean
of the per-line Meaning scores is 4.9; the 4.8 is the conservative reading). Rows 13–22 repeat 2–11
verbatim and double the tally without adding independent evidence.

## Findings

- **F1 — Line 8 validates the human-pass premise: Gemini is a strong reference, not ground truth.**
  The English is "We will see you next week." Naturalized `Magkikita tayo ulit` was scored **N (5/5)**
  and preserves the "see you" emphasis; Gemini's `Magpapatuloy tayo` ("we will continue") is natural
  but shifts the semantic emphasis. Do not chase Gemini's wording for its own sake.
- **F2 — Line 12 exposes a different class of problem: proper-name preservation.** The naturalizer
  correctly repairs the STT artifacts (`nag - uusap - usap`, `tangalog`) but its output
  `grupong nakikipag-usap-usap na tagalog` is still grammatically awkward (3/5 naturalness, 4/5
  meaning). The desired output is closer to keeping the proper group name
  "Conversational Tagalog Practice Group" (as Gemini does). This is **not** phrase naturalization;
  it needs a preserve-proper-name strategy. **Recorded as a quality finding; the rule is NOT changed**
  per the user's direction to evaluate the current 13-rule set as-is.
- **F3 — Line 11 is understandable but slightly literal.** `Magandang trabaho sa inyong lahat` works;
  a Filipino speaker might say `Magaling kayong lahat` depending on emphasis. The added Gemini
  `Magaling.` reads more idiomatic. English-influenced praise phrasing is a recurring style issue.
- **F4 — The Gemini wins are consistency, not accuracy.** Lines 4, 7, 10 are all cases where Argos
  (and Naturalized, which left them unchanged) is **correct but slightly more literal/formal**:
  `Magandang umaga lahat` vs `sa inyong lahat`, `Salamat sa inyong pakikinig` vs `Salamat sa
  pakikinig`, `mga araw ng sanlinggo`/`numerong` vs `mga numero`/`mga araw ng linggo`. These are
  candidates for future rules after the larger-set evaluation — not changes now.

## Overall judgment (user)

> Naturalized Argos is already **substantially more conversational than raw Argos** (Original was
> never preferred on any of the 12 lines) and **close to Gemini on most sentences** (4 Naturalized
> wins, 5 Gemini wins, 3 ties). The remaining gap does not mean Argos needs replacing; it tells us
> the naturalizer must be evaluated against a **larger, unseen conversational Tagalog set** —
> group/class names, idiomatic expressions, formal-vs-conversational vocabulary, literal-but-foreign
> phrasing, and preserving English proper names.

## Decision (recorded 2026-08-07)

1. **Keep the 13-rule table exactly as committed** (`fb223ec`). No rule expansion from these 12
   lines — they are now scored, not tweaked against.
2. The quantitative move toward the Gemini reference (full-stream char similarity **0.670 → 0.777,
   +0.107**) is corroborated by the human pass but is **not** the optimization target.
3. Next phase: evaluate the naturalizer on a larger unseen conversational-Tagalog set per F2–F4
   before any rule expansion or production wiring.

# Tagalog Naturalizer — Unseen-Set Generalization Test, Argos vs Gemini (2026-08-07)

Follow-up to the human-rated quality pass (seen practice-session corpus). Purpose: test whether the
**frozen 13-rule naturalizer generalizes to unseen conversational content**, and to get a decoded
(per-system) Argos vs Gemini quality reading on that content. The new script was deliberately written
to **avoid all 13 rule triggers**.

## Method

- New unseen sample: `artifacts/samples/english_unseen_90s_16k.wav` (92.85 s, 16 scripted lines),
  synthesized with `gen_english_unseen_wav.ps1` (SAPI female, Rate −2) — same pipeline as the seen
  corpus, fresh conversational script (greetings/intros with names, classroom Q&A, time/date/
  numbers, requests with pronouns, casual exchanges, idioms, group/proper name, closing).
- Same-audio legs on that sample: Argos offline (faster-whisper-native `small` → Argos en→tl,
  23 FINAL captions, first caption 14.3–15.6 s, no backlog) and Gemini Live (88 stream fragments,
  target `fil`).
- **Naturalizer on the unseen set: 0/23 captions rewritten; full-stream char similarity vs Gemini
  0.586 → 0.586 (+0.000)** (vs +0.107 on the seen set). The "Naturalized Argos" candidate is
  therefore byte-identical to raw Argos here — the A/B comparison below is effectively
  **Argos vs Gemini**.
- Blinded scoring: one Filipino evaluator scored the 16 lines as A/B with labels **shuffled per
  line** (key held in `artifacts/reports/translatelive/unseen_ab_key_2026-08-07.md`, closed during
  scoring). Naturalness 1–5, Meaning 1–5, Preference A/B/Tie.

## Raw (blinded) scores

| # | A Nat | B Nat | A Mean | B Mean | Pref |
|--:|--:|--:|--:|--:|:--:|
| 1 | 4 | 5 | 5 | 5 | B |
| 2 | 4 | 2 | 5 | 2 | A |
| 3 | 5 | 3 | 5 | 5 | A |
| 4 | 4 | 4 | 4 | 5 | B |
| 5 | 5 | 2 | 5 | 3 | A |
| 6 | 4 | 5 | 5 | 5 | B |
| 7 | 2 | 2 | 5 | 2 | A |
| 8 | 5 | 5 | 5 | 5 | Tie |
| 9 | 3 | 3 | 3 | 5 | B |
| 10 | 5 | 2 | 5 | 4 | A |
| 11 | 2 | 4 | 3 | 5 | B |
| 12 | 5 | 3 | 5 | 5 | A |
| 13 | 5 | 1 | 5 | 2 | A |
| 14 | 2 | 4 | 2 | 4 | B |
| 15 | 3 | 5 | 3 | 5 | B |
| 16 | 5 | 1 | 5 | 2 | A |

Submitted column tally: A 7 / B 8 / Tie 1. Direct recount of the preference column: **A 8 / B 7 /
Tie 1**. Neither column number is a per-system measure — both blind columns mix systems (column A =
7 Argos + 9 Gemini; column B = 9 Argos + 7 Gemini). The decoded table below is authoritative.

## Decoded per-system (key applied)

| Metric | Argos (offline) | Gemini Live |
|---|---:|---:|
| Naturalness (mean /16) | **2.69** | **4.44** |
| Meaning (mean /16) | **3.56** | **4.81** |
| Preferences (winning lines) | **1** (line 9) | **14** |
| Ties | 1 (line 8) | 1 (line 8) |

Decoded line map (winner): 1 G, 2 G, 3 G, 4 G, 5 G, 6 G, 7 G, 8 Tie, 9 A, 10 G, 11 G, 12 G, 13 G,
14 G, 15 G, 16 G.

## Findings

- **F1 — The blind columns masked the result; decoded, this is a decisive Gemini victory.**
  Gemini 14/16, Argos 1/16, 1 tie. The near-even column tally (8/7) hid a lopsided per-system
  result because both columns contained a mix of the two systems.
- **F2 — The evaluator's own qualitative lists, decoded, attribute the failures to Argos.**
  The "more conversational" wins they listed — #1 `Hi sa lahat`, #6 `notes`/`meeting`, #11
  `nakakatawa`, #14 `Mag-usap tayo sa tanghalian`, #15 `lakasan nang kaunti` — are all **Gemini**.
  The "obvious translation failures" — #2 `dulo ng sanlinggo`, #7 `Totoo, narito ka`, #13
  `makita ka agad`, #16 `malaking linggo` — are all **Argos**.
- **F3 — The frozen naturalizer gives zero coverage on unseen content (0/23), so it cannot close
  the gap.** This reverses the seen-set impression (4N/5G/3Tie). Argos's remaining problems are not
  cosmetic: literal translation (#16 `malaking linggo`), semantic mis-interpretation (#7 `Totoo,
  narito ka`), over-formal vocabulary (#9 `subalit`), code-switching choices (#6 `mga nota`/`pulong`
  vs `notes`/`meeting`), and broken phrase rendering (#13 `makita ka agad`). Correcting these needs
  **translation-level changes, not output phrase patches** — exactly the user's next research
  question: improve Argos's translation itself while staying offline (cf. the NLLB / MADLAD-400-3B
  offline-candidate evaluations above).
- **F4 — Single-evaluator caveat (stated by the evaluator).** These are one Filipino listener's
  scores; they should not be the sole benchmark evidence. The worksheet
  (`artifacts/reports/translatelive/unseen_ab_worksheet_2026-08-07.md`) is kept blank and ready for
  a **second independent blind scoring**; if a second evaluator reproduces the same pattern, the
  conclusion "a frozen deterministic phrase-rewrite layer does not generalize to broad
  conversational translation" is much stronger.

## Decision (recorded 2026-08-07)

1. **Keep the 13-rule table frozen.** The unseen test shows the phrase-table approach does not
   generalize to open-ended conversation; line-by-line rule expansion would chase an unbounded
   surface.
2. **Do not promote the naturalizer to production** on the strength of the seen set alone.
3. Next investigation (user's question): **improve the offline translation itself** — evaluate
   candidates (offline MT models / pipeline changes, not output patching) against this unseen
   sample, with a second evaluator scoring the same blinded worksheet first.

---

## M2M-100-418M — Offline en→tl Quality Probe (2026-08-07)

**Context.** Following the unseen-set result, the user's next question is whether an **offline
translation model** can produce more natural English→Tagalog than the Argos+naturalizer baseline
without destroying realtime viability. Per the user's decision gate, the first step is **quality
only** — a native, untuned translation probe — and the investigation **stops** if the candidate is
not clearly more natural than Argos. Prior candidates: NLLB-200-distilled-600M (best quality but
CC-BY-NC → not production-eligible), MADLAD-400-3B-MT (Apache-2.0 but disqualified: 2.8 s avg /
22.7 s max / verbose / 2.8 GB). First permissive-license candidate: **facebook/m2m100_418M** (MIT,
covers `tl`; larger sibling m2m100-1.2B also MIT).

**Method.** Translation-only (no STT variable): the **exact 16 unseen English lines** from
`gen_english_unseen_wav.ps1` were translated by (a) M2M-100-418M via `transformers` in the argos
venv (`m2m_probe_unseen.py`) and (b) the bundled Argos `argos_translate_server.py`
(`argos_corpus_unseen.py`) — identical input, both en→tl. **No post-processing, no naturalizer, no
prompt/decoding tuning** on either side, per the user's measurement rule (the naturalizer applies
0/23 rewrites on unseen content anyway, so it cannot affect this comparison). M2M decode: greedy,
`forced_bos_token_id=tl`, sentencepiece tokenizer, CPU, argos venv torch. Model revision recorded:
`55c2e61bbf05dfb8d7abccdc3fae6fc8512fd636`; load 3.841 s.

**Result — M2M-100-418M loses all 16/16 lines. Decision gate: STOP (do not benchmark streaming).**

| # | English (exact unseen line) | Argos (baseline) | M2M-100-418M (native) |
|---|---|---|---|
| 1 | Hi everyone, I'm Alex, and this is my friend Maya. | Ako si Alex, at ito ang kaibigan kong si Maya. | Hi lahat, ako ay Alex, at ito ay ang aking kaibigan Maya. |
| 2 | Welcome back to class. Did you all have a good weekend? | Maligayang pagdating sa klase. Maganda ba ang dulo ng sanlinggo ninyo? | Welcome back to class.Hindi ka ba ang lahat ng weekend? |
| 3 | Today let's talk about everyday plans and simple requests. | Pag - usapan natin ngayon ang tungkol sa pang - araw - araw na mga plano at simpleng mga kahilingan. | Sa ngayon, makipag-ugnay sa mga araw-araw na mga plano at simpleng mga pagsasanay. |
| 4 | First, who can tell me the time? It's almost nine thirty. | Una, sino ang makapagsasabi sa akin ng oras? Halos siyam na putbol na ito. | Alam mo na ba kung ano ang nakaraang oras? ang nakaraang taon. |
| 5 | My birthday falls on the twenty first of December, and I just turned thirty. | Ang aking kaarawan ay pumapatak sa dalawampung unang bahagi ng Disyembre, at ako'y naging tatlumpu. | Ang aking birthday ay nakakaalam sa 21st ng Disyembre, at ako ay nakakaalam ng 30. |
| 6 | Could you please pass me the notes from yesterday's meeting? | Puwede mo bang ipasa sa akin ang mga nota mula sa pulong kahapon? | Gusto mo bang mag-iisip sa akin ang mga notes mula sa meeting ng araw? |
| 7 | Sure, here you go. Thanks a lot, I really appreciate it. | Totoo, narito ka. Maraming salamat, talagang pinahahalagahan ko ito. | Siguro, dito ang iyong pag-ibig. salamat sa maraming, gusto ko na ito. |
| 8 | The Green Valley Cooking Club meets every Saturday at the community center. | Ang Green Valley Cooking Club ay nagpupulong tuwing Sabado sa sentro ng komunidad. | Ang Green Valley Cooking Club ay matatagpuan sa komunidad center sa araw-araw. |
| 9 | She said he would bring his guitar, but we never saw it arrive. | Sinabi niya na dadalhin ng lalaki ang gitara nito, subalit hindi namin nakita ang pagdating nito. | Nagsimula niya na siya ay nagsisimula ang kanyang guitar, ngunit hindi kami nakikita na ito nagsisimula. |
| 10 | I'm feeling a bit under the weather today, so I'll take it easy. | Medyo masama ang pakiramdam ko ngayon, kaya madali lang. | I feel a little under the weather today, so I’ll take it easy. – I feel a little under the weather today, so I’ll take it easy. |
| 11 | That joke you told earlier was hilarious. I couldn't stop laughing. | Nakagigitla ang birong sinabi mo noon. Hindi ko mapigil ang pagtawa. | Ito ang jokes na sinabi mo sa loob ay hilarious. hindi ko mag-stay laughing. |
| 12 | We're running a little behind schedule, so please bear with us. | Medyo nahuhuli na tayo, kaya pakisuyong pagtiisan mo kami. | Kami ay nag-aalok sa loob ng isang oras, so please bear with us. |
| 13 | Take care on your way home, and see you around soon. | Mag - ingat sa iyong pag - uwi, at makita ka agad. | Tingnan mo sa iyong bahay, at makikita mo sa loob ng isang araw. |
| 14 | Let's catch up over lunch sometime. It's been a while since we talked. | Paminsan - minsan, kumain tayo. Mula nang kami'y mag - usap. | Nagsimula kami ng lunch sa loob ng isang taon na ang nakalipas. |
| 15 | Could you speak up a little? The projector is a bit noisy today. | Puwede ka bang magsalita nang kaunti? Ang projector ay medyo maingay ngayon. | Ang proyektong ito ay isang maliit na rumor ngayon. |
| 16 | Alright, that's all for today. Thanks for coming, and have a great week. | Alam mo, iyan ay para sa ngayon. Salamat sa inyong pagdating, at mayroon po kayong isang malaking linggo. | Salamat, totoo ang lahat para sa ngayon. thanks for coming, and have a great week. |

**Findings (2026-08-07).**

- **F1 — M2M-100-418M is decisively worse than Argos on every unseen line (0/16).** The baseline
  wins even on lines where Argos has known faults (#13 `makita ka agad`, #16 `malaking linggo`);
  M2M adds whole-utterance semantic breaks.
- **F2 — Complete untranslated passthrough (line 10):** `I feel a little under the weather today, so
  I'll take it easy. – I feel a little under the weather today, so I'll take it easy.` — the entire
  caption is returned verbatim in English (doubled). This is a hard failure for a caption product.
- **F3 — Word-sense catastrophes:** "here you go" → `pag-ibig` (love, #7); "nine thirty" → `nakaraang
  taon` (last year, #4); "falls on" → `nakakaalam` (knows, #5); "pass me" → `mag-iisip` (think, #6);
  "meets every Saturday" → `araw-araw` (daily, #8); "bring" → `nagsisimula` (starts, #9); "noisy" →
  `rumor` (#15); "catch up over lunch" → `nagsimula kami ng lunch` (#14).
- **F4 — Code-switching / English leakage throughout:** #2 `Welcome back to class`, #5 `21st`, #6
  `notes`/`meeting`, #11 `jokes`/`hilarious`/`mag-stay`, #12 `so please bear with us`, #14 `lunch`,
  #16 `thanks for coming`.
- **F5 — Inference ~20–40× slower than Argos and already beyond caption cadence:** M2M mean 2.76 s /
  line (range 1.71–4.21 s) vs Argos mean ~0.11 s / line (range 0.08–0.15 s after warmup; the 2.44 s
  first-call in the probe is Argos model load, not translation). Even ignoring quality, M2M-100-418M
  cannot meet the live-caption cadence, so the streaming/performance phase is moot.

**Decision (recorded 2026-08-07).** Per the user's gate ("if M2M is not clearly more natural than
Argos: stop, no need to benchmark streaming"), the M2M-100 family is **rejected at the quality
probe** — the larger m2m100-1.2B is not probed because it shares the same architecture/training and
would not fix the observed word-sense and passthrough error class (same rationale as MADLAD
disqualification). **Argos+naturalizer remains the offline production baseline**; the permissive-
license offline MT space (OPUS-MT/Argos, M2M-100, MADLAD-400) is now evaluated and none beats Argos
on en→tl. Remaining options for "more natural Tagalog" are the license-blocked NLLB family
(CC-BY-NC, quality ceiling reference) or a general-LLM lane (Gemma 3 permissive, but weak on
low-resource translation pairs per research). **Open question for the user** before any further
candidate work: whether to accept Argos+naturalizer as final, or pursue a different approach to the
Tagalog naturalness gap (e.g., evaluating the small permissive LLM lane despite expected weakness).

**Evidence:** `artifacts/reports/translatelive/m2m_100_418m_unseen_2026-08-07.json` (M2M raw rows +
revision `55c2e61bbf05dfb8d7abccdc3fae6fc8512fd636`, load 3.841 s, per-row inference times);
`artifacts/reports/translatelive/argos_baseline_unseen_2026-08-07.json` (Argos raw rows, identical
input); probe scripts `m2m_probe_unseen.py` + `argos_corpus_unseen.py` (repo root). Raw corpora/
outputs outside the repo under `%TEMP%\opencode\txbench\`.

---

## Final Decision — Offline Model-Selection Investigation CLOSED (2026-08-07)

**User decision (2026-08-07).** Stop searching for another offline MT model. The offline-candidate
evidence chain is now complete and conclusive for en→tl on this product's constraints:

| Candidate | License | Quality vs Argos | Realtime | Verdict |
|---|---|---|---|---|
| **Argos / OPUS-MT en→tl** (+ frozen 13-rule naturalizer) | Apache-2.0 (shipped) | baseline — stilted but correct | fast (~0.11 s/line) | **Production offline baseline (frozen)** |
| **NLLB-200-distilled-600M** | CC-BY-NC-4.0 | materially better | ~577 ms/line, 1.14 GB | Quality ceiling; **not production-eligible** |
| **MADLAD-400-3B-MT** | Apache-2.0 | hallucination-prone/verbose | 2.8 s avg / 22.7 s max, 2.8 GB | Rejected (2026-08-06) |
| **M2M-100-418M** | MIT | worse on 0/16 unseen lines | ~2.76 s/line mean | Rejected (2026-08-07) |
| **Gemini Live Translate** | cloud | clearly better naturalness | realtime | Experimental quality/realtime **reference** (cloud/privacy/cost tradeoff) |
| **13-rule naturalizer** | — | improves known Argos artifacts | free | Keep frozen; ~0 unseen-set recall |

**Three-track conclusion (user-approved):**

1. **Keep Argos as the production offline baseline** (frozen): `Whisper → Argos/OPUS-MT en→tl →
   frozen 13-rule naturalizer → Caption`. The naturalizer stays because it demonstrably fixes known
   recurring artifacts even though it is not a general translator.
2. **Keep Gemini as the experimental quality/realtime reference.** Established tradeoff: Gemini wins
   naturalness + realtime tracking; Argos wins offline, privacy, cost. This is a product choice, not a
   winner/loser. Optional future **hybrid mode** (offline: Whisper→Argos→Naturalizer; online enhanced:
   Whisper/audio→Gemini Live Translate) would let the user choose privacy/reliability vs
   naturalness/realtime rather than forcing one technology.
3. **Stop the offline-model hunt** unless a new candidate materially changes the constraints.
   Repeatedly downloading MT models is no longer productive.

**Next experiment (user direction):** not another MT sweep, but **small-model Tagalog
naturalization** — whether a *small, permissively-licensed, instruction-following/text-rewriting
model* can act as a **Tagalog naturalization/correction layer** over Argos (a different experiment
from M2M/NLLB/MADLAD). The second blind human scorer of the unseen worksheet remains **supporting
evidence only and no longer blocks the technical direction** (the M2M failure + unseen-set
naturalizer result already provide enough evidence to move forward).

---

## Small-Model Tagalog Naturalizer — Quality Probe (2026-08-07)

**Context.** The naturalizer's contract (user-specified): *"Given an existing Tagalog translation,
improve its naturalness while preserving its meaning."* The experiment is **quality only** on the
same unseen 16-line corpus — no streaming optimization, and the experiment **stops** if the small
model does not produce visibly better human-rated Tagalog than Argos + the frozen 13 rules.
Guardrails (user-specified, enforced in the prompt): no name changes, no number/date/time changes,
no invented or omitted information, no unnecessary translation of English proper names, no
unrelated content, no added explanations, output only the corrected Tagalog caption.

**Method.** Translation-only, identical 16 unseen English lines. Four columns:
1. **Argos** (bundled OPUS-MT en→tl, `argos_translate_server.py`);
2. **Argos + frozen 13-rule naturalizer** (the deterministic `TagalogNaturalizer`, applied to the
   Argos output — ported 1:1 to Python and **parity-verified against all 13 C# unit-test vectors**);
3. **Argos + small-model naturalizer** = `Qwen/Qwen2.5-1.5B-Instruct` (Apache-2.0, ungated, 1.5B —
   a small permissive instruction-following model) given the **Argos Tagalog line only** (it is a
   naturalizer, not a re-translator — no English input) under the guardrail system prompt, greedy
   deterministic decode (`do_sample=False`, `num_beams=1`, `max_new_tokens=96`, fp32, CPU);
4. **Gemini reference** (the per-line Gemini Live translations already blind-scored in the unseen
   worksheet — naturalness/meaning reference, cloud).

Model recorded: revision `989aa7980e4cf806f80c7fef2b1adb7bc71aa306`, load ~1 s (cached), per-line
inference 6.6–17.2 s (mean ~11 s — noted, but the quality gate fails before performance matters).

**Result — DECISIVE FAIL. Small-model naturalizer is far worse than Argos on 15/16 lines.**
The experiment **stops at the quality gate** (no performance phase, no production change).

| # | English (source) | Argos (baseline) | Argos + frozen 13 rules | Argos + Qwen2.5-1.5B-Instruct | Gemini (reference) |
|---|---|---|---|---|---|
| 1 | Hi everyone, I'm Alex, and this is my friend Maya. | Ako si Alex, at ito ang kaibigan kong si Maya. | (unchanged) | Kaya ako Alex, at ito ang kaibigan ko na si Maya. | Hi sa lahat. Ako si Alex, at ito ang kaibigan kong si Maya. |
| 2 | Welcome back to class. Did you all have a good weekend? | Maligayang pagdating sa klase. Maganda ba ang dulo ng sanlinggo ninyo? | (unchanged) | Ang malaking pangyayari ay maaaring maipagdadalaw sa klasa. Kaya, mayroon ka pa bang dulo na siyang sanlanggian? | Maligayang pagbabalik sa klase. Nagkaroon ba kayo ng magandang weekend? |
| 3 | Today let's talk about everyday plans and simple requests. | Pag - usapan natin ngayon ang tungkol sa pang - araw - araw na mga plano at simpleng mga kahilingan. | (unchanged) | Ang nag-usap namin ay maaaring magkakaiba-saklaw sa pagtuturo ng mga planong at mga kahilingang simpleng mga tao. | Ngayon pag-usapan natin ang mga pang-araw-araw na plano at simpleng kahilingan. |
| 4 | First, who can tell me the time? It's almost nine thirty. | Una, sino ang makapagsasabi sa akin ng oras? Halos siyam na putbol na ito. | (unchanged) | Kaya kong mabuti ka pa rin magtanda sa akin? Hindi ko maayos nang matatagpuan. | Una, sino ang makapagsasabi sa akin ng oras? Ito ay halos 9:30. |
| 5 | My birthday falls on the twenty first of December, and I just turned thirty. | Ang aking kaarawan ay pumapatak sa dalawampung unang bahagi ng Disyembre, at ako'y naging tatlumpu. | (unchanged) | Ang kanyang taon ay tumutukoy sa iba't ibang araw na nagpapakita sa Disyembre, at siya'y maaaring matatagpuan. | Ang kaarawan ko ay sa ika-21 ng Disyembre. At nag-30 na ako. |
| 6 | Could you please pass me the notes from yesterday's meeting? | Puwede mo bang ipasa sa akin ang mga nota mula sa pulong kahapon? | (unchanged) | Kaya ka ba na magpasa sa akin ang mga note natin ngayon? | Pwede mo bang ipasa ang mga notes mula sa meeting kahapon? |
| 7 | Sure, here you go. Thanks a lot, I really appreciate it. | Totoo, narito ka. Maraming salamat, talagang pinahahalagahan ko ito. | (unchanged) | **Okay, here's the revised caption in more natural Tagalog:** Sure thing. Thanks for your kind words, really appreciated this. (English + added explanation + meaning lost) | Sige. Heto. Maraming salamat. Talagang naa-appreciate ko. |
| 8 | The Green Valley Cooking Club meets every Saturday at the community center. | Ang Green Valley Cooking Club ay nagpupulong tuwing Sabado sa komunidad center. | (unchanged) | Ang Green Valley Cooking Club ay nagpupulong sa komunidad tungkol sa sabado, sa sentrong lugar. | Ang Green Valley Cooking Club ay nagpupulong tuwing Sabado sa Community Center. |
| 9 | She said he would bring his guitar, but we never saw it arrive. | Sinabi niya na dadalhin ng lalaki ang gitara nito, subalit hindi namin nakita ang pagdating nito. | (unchanged) | Nagpapakita niyang dadalhin ng gitara natin, pero hindi kami nagkakaiba sa pagtanda nito. | Siya ay nagsabing dadalhin niya ang kanyang gitara. Pero hindi namin nakita na dumating. |
| 10 | I'm feeling a bit under the weather today, so I'll take it easy. | Medyo masama ang pakiramdam ko ngayon, kaya madali lang. | (unchanged) | Kahit malaki pa ang problema ko, maaaring maipakiramdam. | Medyo masama ang pakiramdam ko ngayon, kaya magpapahinga na lang ako. |
| 11 | That joke you told earlier was hilarious. I couldn't stop laughing. | Nakagigitla ang birong sinabi mo noon. Hindi ko mapigil ang pagtawa. | (unchanged) | Ang tawa namin ay nagkagigitla sa iyong mga pinuno. Kaya kung paano ka mabuti na magpapahayag? | Ang biro na sinabi mo kanina ay nakakatawa. Hindi ko mapigilan ang pagtawa. |
| 12 | We're running a little behind schedule, so please bear with us. | Medyo nahuhuli na tayo, kaya pakisuyong pagtiisan mo kami. | (unchanged) | Nakakalimutan natin ang mas maayos na magkakaiba ka sa akin. | Medyo nahuhuli na tayo sa iskedyul. Kaya pasensya na muna kayo sa amin. |
| 13 | Take care on your way home, and see you around soon. | Mag - ingat sa iyong pag - uwi, at makita ka agad. | (unchanged) | Kaya kung may problema o nag - maganda ang iyo, nais ka bang ma - solve? | Mag-ingat ka sa pag-uwi. At magkita tayo ulit sa lalong madaling panahon. |
| 14 | Let's catch up over lunch sometime. It's been a while since we talked. | Paminsan - minsan, kumain tayo. Mula nang kami'y mag - usap. | (unchanged) | Kahit pa rin, mayroon ka na. Kaya kami ay nag-usap. | Mag-usap tayo sa tanghalian minsan. Matagal na simula nung nag-usap tayo. |
| 15 | Could you speak up a little? The projector is a bit noisy today. | Puwede ka bang magsalita nang kaunti? Ang projector ay medyo maingay ngayon. | (unchanged) | Maaari ko ba talaga na siyang magtutulong sa pagkakataong salita kahit pa rin ang projector? | Pwede mo bang lakasan nang kaunti? Ang projector ay medyo maingay ngayon. |
| 16 | Alright, that's all for today. Thanks for coming, and have a great week. | Alam mo, iyan ay para sa ngayon. Salamat sa inyong pagdating, at mayroon po kayong isang malaking linggo. | (unchanged) | Kaya kini ang lugar na naisipin ko. Salamat rin sa inyo na nagpapakita, at mahalaga ka lang talaga ang iyong pagsasama-sama. | Sige, yun lang para sa araw na ito. Salamat sa pagpunta, at magandang linggo sa inyo. |

**Findings (2026-08-07).**

- **F1 — The small model is catastrophically worse than Argos on 15/16 lines (only #1 is arguably
  close).** Qwen2.5-1.5B-Instruct's outputs are largely **not valid Tagalog** (e.g. #4, #10, #12,
  #15, #16), and even where grammatical they destroy the meaning (#5 birthday/December/thirty all
  gone; #6 "yesterday's meeting" → "natin ngayon"; #8 "every Saturday at the community center" →
  "tungkol sa sabado, sa sentrong lugar"; #9 guitar-arrival lost; #11 "joke you told" → "iyong mga
  pinuno").
- **F2 — Contract violation on #7:** the model answered in **English** with an added meta-note
  ("Okay, here's the revised caption in more natural Tagalog:") instead of outputting only the
  corrected Tagalog caption. This breaks the guardrail contract even beyond the meaning loss.
- **F3 — No hallucination-protection capability at 1.5B:** invented pronouns/possessives (#2
  "sanlanggian", #16 "kini"), dropped the actual content, and introduced unrelated phrasing. The
  model cannot both preserve meaning and naturalize at this size; it simply re-generates.
- **F4 — Inference ~2 orders of magnitude above caption cadence:** mean ~11 s/line (6.6–17.2 s) at
  fp32 on this 12-core machine vs Argos ~0.11 s/line. Even if quality passed, the streaming
  gate would fail; the quality gate fails first, so no performance phase is run.
- **F5 — Frozen 13 rules confirmed faithful & inert on unseen content:** the ported rule table
  reproduced **all 13 C# unit-test vectors exactly** (PARITY OK) and rewrote **0/16** of these
  unseen lines — consistent with the 0/23 finding from the audio-pipeline unseen test.

**Decision (recorded 2026-08-07, per the user's quality gate).** The small instruction-following
model tested (**Qwen2.5-1.5B-Instruct**, greedy/deterministic) does **not** produce better
human-rated Tagalog than Argos + the frozen 13 rules — it produces invalid Tagalog and destroys
meaning. **The experiment stops at the quality gate.** No performance/streaming phase. **No
production change** (baseline remains `Whisper → Argos → frozen 13-rule naturalizer → Caption`).
The naturalization-gap conclusion now stands on three independent lines of evidence: the
deterministic rules (0/23 unseen recall), a small instruction-following model (this probe, 15/16
worse + contract violation), and the M2M family (0/16 translation). Remaining un-tested naturalizer
options would be a materially larger permissive LLM (contra the user's "very small model"
preference) or a dedicated Tagalog-rewrite fine-tune of a small model (a new training experiment,
out of scope for a probe).

**Evidence:** `artifacts/reports/translatelive/naturalizer_qwen2.5-1.5b_instruct_2026-08-07.json`
(raw model rows + revision `989aa7980e4cf806f80c7fef2b1adb7bc71aa306`, per-line inference times,
decode settings); `naturalizer_probe_qwen.py` (probe script incl. the parity-checked frozen-rule
port); Argos column from `artifacts/reports/translatelive/argos_baseline_unseen_2026-08-07.json`;
Gemini reference from the already-scored `unseen_ab_worksheet_2026-08-07.md` + key. Raw outputs
outside the repo under `%TEMP%\opencode\txbench\out\`.
