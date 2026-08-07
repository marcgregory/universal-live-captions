import json, os, time, sys, traceback

from transformers import M2M100ForConditionalGeneration, M2M100Tokenizer

CORPUS = os.environ.get("M2M_CORPUS", r"%TEMP%\opencode\txbench\unseen_english_16.txt")
OUT = os.environ.get("M2M_OUT", r"%TEMP%\opencode\txbench\out\m2m-100-418m-unseen.txt")
MODEL_ID = "facebook/m2m100_418M"
SRC = "en"
TGT = "tl"

def load_corpus(path):
    with open(path, "r", encoding="utf-8") as f:
        lines = [ln.strip() for ln in f if ln.strip()]
    return lines

def main():
    lines = load_corpus(os.path.expandvars(CORPUS))
    t0 = time.perf_counter()
    model = M2M100ForConditionalGeneration.from_pretrained(MODEL_ID)
    tok = M2M100Tokenizer.from_pretrained(MODEL_ID)
    t_load = time.perf_counter() - t0

    # model revision from HF metadata via huggingface_hub snapshot info is not
    # guaranteed without hub; record the resolved model id + tokenizer vocab size.
    tok.src_lang = SRC
    tgt_lang_id = tok.get_lang_id(TGT)

    rows = []
    for i, text in enumerate(lines):
        enc = tok(text, return_tensors="pt")
        t0i = time.perf_counter()
        generated = model.generate(**enc, forced_bos_token_id=tgt_lang_id)
        t1i = time.perf_counter()
        out_text = tok.batch_decode(generated, skip_special_tokens=True)[0]
        rows.append({
            "model": MODEL_ID,
            "model_revision_sha": "resolved_at_runtime",
            "source_language": SRC,
            "target_language": TGT,
            "input_line": text,
            "output_line": out_text,
            "inference_time_s": round(t1i - t0i, 4),
        })
        print(f"[{i+1}] ({t1i - t0i:6.2f}s) {out_text}", flush=True)

    # Determine SHA of resolved revision if huggingface_hub available.
    sha = "n/a"
    try:
        from huggingface_hub import HfApi
        sha = HfApi().model_info(MODEL_ID).sha
    except Exception as e:
        sha = f"n/a ({e.__class__.__name__})"

    summary = {
        "model": MODEL_ID,
        "model_revision_sha": sha,
        "source_language": SRC,
        "target_language": TGT,
        "load_time_s": round(t_load, 3),
        "rows": rows,
    }
    with open(os.path.expandvars(OUT), "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)
    print(json.dumps(summary, ensure_ascii=False, indent=2))

if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc()
        sys.exit(1)
