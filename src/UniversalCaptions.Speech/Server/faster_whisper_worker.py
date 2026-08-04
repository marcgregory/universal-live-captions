"""Persistent faster-whisper worker for streaming caption windows.

The .NET streaming engine owns windowing, trimming, and commit orchestration; this worker only
decodes one audio window into segments. It reads one binary-framed request from stdin, transcribes
with faster-whisper (model loaded once at startup), and writes one binary-framed response to stdout.
stdout carries the protocol only; diagnostics go to stderr.

Request frame
    [4 bytes] magic          b"UCWF"
    [4 bytes] version        int32, little-endian (1)
    [4 bytes] sample_rate    int32
    [4 bytes] sample_count   int32
    [4 bytes] language_len   int32
    [N bytes] language       UTF-8 code ("" = auto-detect)
    [M bytes] pcm            sample_count * int16, little-endian

Response frame
    [4 bytes] magic          b"UCWF"
    [4 bytes] version        int32 (1)
    [4 bytes] status         int32 (0 = ok, 1 = error)
    [4 bytes] segment_count  int32 (0 when status != 0)
    repeated segment_count:
        [8 bytes] start      float64 seconds relative to window start
        [8 bytes] end        float64 seconds
        [4 bytes] text_len   int32 (UTF-8 byte length)
        [N bytes] text       UTF-8

A ping is a request with sample_count == 0 (no PCM bytes follow, but the language header is still
present). The worker confirms model readiness by responding with status 0 and zero segments.

Usage
    python faster_whisper_worker.py --model small --compute int8 --threads 4 --num-workers 1
"""

import argparse
import struct
import sys
import time

MAGIC = b"UCWF"
MAGIC_INT = MAGIC[0] | (MAGIC[1] << 8) | (MAGIC[2] << 16) | (MAGIC[3] << 24)
VERSION = 1
HEADER = struct.Struct("<IIIII")
RESPONSE_HEADER = struct.Struct("<IIII")
SEGMENT = struct.Struct("<ddI")

import numpy as np  # noqa: E402


def read_exact(stream, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = stream.read(n - len(buf))
        if not chunk:
            return None
        buf.extend(chunk)
    return bytes(buf)


def write_all(stream, data):
    stream.write(data)
    stream.flush()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="small")
    parser.add_argument("--compute", default="int8")
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--num-workers", type=int, default=1)
    parser.add_argument("--beam-size", type=int, default=5)
    args = parser.parse_args()

    stdin = sys.stdin.buffer
    stdout = sys.stdout.buffer

    try:
        from faster_whisper import WhisperModel
    except Exception as exc:  # pragma: no cover - import failure path
        print(f"[FW-DIAG] faster_whisper import failed: {exc}", file=sys.stderr, flush=True)
        sys.exit(1)

    t0 = time.time()
    print(f"[FW-DIAG] loading model={args.model} compute={args.compute} threads={args.threads}", file=sys.stderr, flush=True)
    try:
        model = WhisperModel(
            args.model,
            device="cpu",
            compute_type=args.compute,
            cpu_threads=args.threads,
            num_workers=args.num_workers,
        )
    except Exception as exc:  # pragma: no cover - load failure path
        print(f"[FW-DIAG] model load failed: {exc}", file=sys.stderr, flush=True)
        sys.exit(1)
    print(f"[FW-DIAG] model loaded in {time.time() - t0:.1f}s", file=sys.stderr, flush=True)

    while True:
        header = read_exact(stdin, HEADER.size)
        if header is None:
            break

        magic, version, sample_rate, sample_count, language_len = HEADER.unpack(header)
        if magic != MAGIC_INT or version != VERSION:
            print(f"[FW-DIAG] protocol mismatch magic={magic!r} version={version}", file=sys.stderr, flush=True)
            break

        language = b""
        if language_len > 0:
            lang_bytes = read_exact(stdin, language_len)
            if lang_bytes is None:
                break
            language = lang_bytes.decode("utf-8")

        pcm = None
        if sample_count > 0:
            pcm = read_exact(stdin, sample_count * 2)
            if pcm is None:
                break

        if sample_count == 0:
            # Ping: process is alive and model is loaded.
            write_all(stdout, RESPONSE_HEADER.pack(MAGIC_INT, VERSION, 0, 0))
            continue

        t1 = time.time()
        try:
            pcm_int16 = np.frombuffer(pcm, dtype=np.int16)
            # faster-whisper expects float32 audio normalized to [-1, 1]; passing raw int16
            # produces garbage (clipped/zero-scaled decoder input) and a pathologically slow decode.
            samples = pcm_int16.astype(np.float32) / 32768.0
            segments_iter, info = model.transcribe(
                samples,
                language=language or None,
                beam_size=args.beam_size,
                condition_on_previous_text=False,
                word_timestamps=False,
            )
            segments = list(segments_iter)
        except Exception as exc:  # decode failure path
            print(f"[FW-DIAG] transcribe failed: {exc}", file=sys.stderr, flush=True)
            write_all(stdout, RESPONSE_HEADER.pack(MAGIC_INT, VERSION, 1, 0))
            continue

        wall = time.time() - t1
        dur = info.duration if info.duration else 0.0
        print(f"[FW-DIAG] decoded {sample_count} samples in {wall:.3f}s realtime={dur / wall:.2f}x lang={info.language} p={info.language_probability:.3f} segments={len(segments)}", file=sys.stderr, flush=True)

        body = bytearray()
        for seg in segments:
            text = (seg.text or "").encode("utf-8")
            body.extend(SEGMENT.pack(seg.start, seg.end, len(text)))
            body.extend(text)

        header_bytes = RESPONSE_HEADER.pack(MAGIC_INT, VERSION, 0, len(segments))
        write_all(stdout, header_bytes + bytes(body))


if __name__ == "__main__":
    try:
        main()
    except (BrokenPipeError, KeyboardInterrupt):
        pass

