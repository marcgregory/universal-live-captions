import json, os, time

ARGO_PYTHON = os.path.join(os.environ["TEMP"], "argosv", "Scripts", "python.exe")
ARGO_SERVER = r"C:\Users\TO GOD BE THE GLORY\Desktop\cwm_claude_code\audiototexttranslation\src\UniversalCaptions.Translation\Server\argos_translate_server.py"
CORPUS = os.path.join(os.environ["TEMP"], "opencode", "txbench", "unseen_english_16.txt")
lines = [ln.strip() for ln in open(CORPUS, encoding="utf-8") if ln.strip()]

proc = None
try:
    import subprocess
    proc = subprocess.Popen([ARGO_PYTHON, ARGO_SERVER], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)
    proc.stdin.write(json.dumps({"id": -2, "ping": True}) + "\n"); proc.stdin.flush(); proc.stdout.readline()
    rows = []
    for i, text in enumerate(lines):
        req = {"id": i, "text": text, "source": "en", "target": "tl"}
        t0 = time.perf_counter()
        proc.stdin.write(json.dumps(req, ensure_ascii=False) + "\n"); proc.stdin.flush()
        resp = proc.stdout.readline(); obj = json.loads(resp)
        dt = time.perf_counter() - t0
        rows.append({"input_line": text, "output_line": obj.get("text", ""), "inference_time_s": round(dt, 4)})
        print(f"[{i+1}] ({dt:6.2f}s) {obj.get('text','')}", flush=True)
finally:
    if proc:
        try:
            proc.stdin.close(); proc.wait(timeout=10)
        except Exception:
            proc.kill()

out_path = os.path.join(os.environ["TEMP"], "opencode", "txbench", "out", "argos-unseen.txt")
with open(out_path, "w", encoding="utf-8") as f:
    json.dump({"model": "Argos translate-en_tl-1_9 (OPUS-MT en-tl, bundled)", "source_language": "en",
               "target_language": "tl", "rows": rows}, f, ensure_ascii=False, indent=2)
print("written:", out_path)
