import json
import os
import re
import time

CORPUS = os.path.join(os.environ["TEMP"], "opencode", "txbench", "unseen_english_16.txt")
ARGO_JSON = os.path.join(os.environ["TEMP"], "opencode", "txbench", "out", "argos-unseen.txt")
OUT_PATH = os.path.join(os.environ["TEMP"], "opencode", "txbench", "out", "naturalizer-qwen2.5-1.5b-instruct.txt")

MODEL_ID = "Qwen/Qwen2.5-1.5B-Instruct"

# ---------------------------------------------------------------------------
# Column 2: the frozen deterministic 13-rule naturalizer, ported faithfully
# from src/UniversalCaptions.Translation/TagalogNaturalizer.cs (same rules,
# same word-boundary + case-preserving semantics).
# ---------------------------------------------------------------------------
FROZEN_RULES = [
    ("wakas ng kasalukuyang sesyon ng pagsasanay", "katapusan ng ating sesyon sa pagsasanay"),
    ("dakilang gawa ang lahat", "magandang trabaho sa inyong lahat"),
    ("dakilang gawa", "magandang trabaho"),
    ("malugod na tanggapin", "maligayang pagdating"),
    ("pakisuyong buksan", "pakibuksan"),
    ("makikita ka namin", "magkikita tayo ulit"),
    ("hello at", "kamusta at"),
    ("sa ngayon ay", "ngayon ay"),
    ("magsasanay tayo", "mag-eensayo tayo"),
    ("pambungad", "pagpapakilala"),
    ("nag - uusap - usap", "nakikipag-usap-usap"),
    ("conversional", "conversational"),
    ("tangalog", "tagalog"),
]


def frozen_naturalize(text):
    result = text
    for from_phrase, to_phrase in FROZEN_RULES:
        result = apply_rule(result, from_phrase, to_phrase)
    return result


def apply_rule(text, from_phrase, to_phrase):
    if len(from_phrase) == 0 or len(text) < len(from_phrase):
        return text
    out = []
    i = 0
    while True:
        idx = text.lower().find(from_phrase, i)
        if idx < 0:
            break
        before_ok = (idx == 0) or (not text[idx - 1].isalpha())
        end = idx + len(from_phrase)
        after_ok = (end == len(text)) or (not text[end].isalpha())
        if not before_ok or not after_ok:
            out.append(text[i:idx + 1])
            i = idx + 1
            continue
        out.append(text[i:idx])
        out.append(adjust_case(text[idx:end], to_phrase))
        i = end
    out.append(text[i:])
    return "".join(out)


def adjust_case(source, replacement):
    has_letter = any(c.isalpha() for c in source)
    all_upper = has_letter and all((not c.isalpha()) or c.isupper() for c in source)
    if all_upper:
        return replacement.upper()
    if source and source[0].isupper():
        return replacement[0].upper() + replacement[1:]
    return replacement


# ---------------------------------------------------------------------------
# Small-model naturalizer (Qwen2.5-1.5B-Instruct, Apache-2.0, ungated).
# Greedy deterministic decode. No post-processing, no tuning.
# ---------------------------------------------------------------------------
SYSTEM_PROMPT = (
    "You are a Tagalog caption naturalizer. You receive an existing Tagalog translation of an "
    "English caption. Rewrite it into more natural, conversational Tagalog while preserving its "
    "exact meaning. Rules: never change names; never change numbers, dates, or times; never "
    "invent or omit information; do not translate English proper names; do not turn one sentence "
    "into unrelated content; do not add explanations; output ONLY the corrected Tagalog caption "
    "and nothing else."
)


def load_model():
    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer
    t0 = time.perf_counter()
    tok = AutoTokenizer.from_pretrained(MODEL_ID)
    model = AutoModelForCausalLM.from_pretrained(
        MODEL_ID, torch_dtype=torch.float32, low_cpu_mem_usage=True
    )
    model.eval()
    return tok, model, time.perf_counter() - t0


def run_naturalizer(tok, model, tagalog_line):
    messages = [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user", "content": tagalog_line},
    ]
    prompt = tok.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
    inputs = tok(prompt, return_tensors="pt")
    t0 = time.perf_counter()
    import torch
    with torch.no_grad():
        outputs = model.generate(
            **inputs,
            max_new_tokens=96,
            do_sample=False,
            num_beams=1,
            pad_token_id=tok.pad_token_id if tok.pad_token_id is not None else tok.eos_token_id,
        )
    dt = time.perf_counter() - t0
    generated = outputs[0][inputs["input_ids"].shape[-1]:]
    return tok.decode(generated, skip_special_tokens=True).strip(), dt


def main():
    with open(CORPUS, encoding="utf-8") as f:
        english_lines = [ln.strip() for ln in f if ln.strip()]
    with open(ARGO_JSON, encoding="utf-8") as f:
        argos_rows = json.load(f)["rows"]

    from huggingface_hub import HfApi
    revision = HfApi().model_info(MODEL_ID).sha

    tok, model, load_time = load_model()
    rows = []
    for i, (en, ar) in enumerate(zip(english_lines, argos_rows)):
        argos_tl = ar["output_line"]
        frozen = frozen_naturalize(argos_tl)
        nat, dt = run_naturalizer(tok, model, argos_tl)
        rows.append({
            "index": i + 1,
            "english": en,
            "argos": argos_tl,
            "argos_frozen_13": frozen,
            "small_model_naturalized": nat,
            "inference_time_s": round(dt, 4),
        })
        print(f"[{i+1}] ({dt:6.2f}s)", flush=True)
        print(f"  argos: {argos_tl}", flush=True)
        print(f"  frozen:{frozen}", flush=True)
        print(f"  model: {nat}", flush=True)

    with open(OUT_PATH, "w", encoding="utf-8") as f:
        json.dump({
            "model": MODEL_ID,
            "model_revision_sha": revision,
            "task": "Tagalog caption naturalizer over Argos en->tl output",
            "decode": "greedy, do_sample=False, num_beams=1, max_new_tokens=96",
            "load_time_s": round(load_time, 4),
            "rows": rows,
        }, f, ensure_ascii=False, indent=2)
    print("written:", OUT_PATH)
    print("revision:", revision)


if __name__ == "__main__":
    main()
