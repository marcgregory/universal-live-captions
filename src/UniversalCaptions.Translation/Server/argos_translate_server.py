"""Line-protocol server for Argos Translate.

Reads one JSON object per line from stdin and writes one JSON object per line to stdout.

Requests
    {"id": <int>, "ping": true}
    {"id": <int>, "text": <str>, "source": <str|None>, "target": <str>}

Responses (always carry the request id)
    {"id": <int>, "ok": true, "ready": true, "languages": [<code>, ...]}
    {"id": <int>, "ok": true, "text": <str>, "detectedSource": <str|None>, "usedPivot": <bool>, "pivotLanguage": <str|None>}
    {"id": <int>, "ok": false, "kind": <str>, "message": <str>}

Errors are written to stderr for diagnostics; stdout carries the protocol only.
"""

import json
import sys

try:
    import argostranslate.translate as translate
    from argostranslate.translate import CompositeTranslation, PackageTranslation
except Exception as exc:  # pragma: no cover - import failure path
    print(json.dumps({"id": -1, "ok": False, "kind": "EngineUnavailable", "message": f"argostranslate import failed: {exc}"}), flush=True)
    sys.exit(1)


def _installed_languages():
    try:
        return translate.get_installed_languages(), None
    except Exception as exc:
        return None, str(exc)


def _language_by_code(code, languages):
    if not code:
        return None, None
    for lang in languages:
        if lang.code == code:
            return lang, None
    return None, f"unknown language code: {code}"


def _detect_source(text):
    try:
        detector = getattr(translate, "detect_language", None)
        if detector is None:
            return None, None
        return detector(text), None
    except Exception as exc:
        return None, str(exc)


def _translate(text, source, target):
    languages, err = _installed_languages()
    if err:
        return {"ok": False, "kind": "EngineFailed", "message": err}

    from_lang, err = _language_by_code(source, languages)
    if err:
        return {"ok": False, "kind": "UnsupportedLanguage", "message": err}
    to_lang, err = _language_by_code(target, languages)
    if err:
        return {"ok": False, "kind": "UnsupportedLanguage", "message": err}

    detected_source = None
    if from_lang is None:
        detected_source, err = _detect_source(text)
        if err:
            return {"ok": False, "kind": "EngineFailed", "message": err}
        if detected_source is None:
            return {"ok": False, "kind": "EngineFailed", "message": "source auto-detection is not available"}
        from_lang = detected_source

    try:
        translation = from_lang.get_translation(to_lang)
    except Exception as exc:
        return {
            "ok": False,
            "kind": "LanguagePairNotSupported",
            "message": f"no translation path from {from_lang.code} to {target}: {exc}",
        }
    if translation is None:
        return {
            "ok": False,
            "kind": "LanguagePairNotSupported",
            "message": f"no translation path from {from_lang.code} to {target}",
        }

    try:
        out = translation.translate(text)
    except Exception as exc:
        return {"ok": False, "kind": "EngineFailed", "message": str(exc)}

    used_pivot = isinstance(translation, CompositeTranslation)
    pivot_language = None
    if used_pivot:
        intermediate = getattr(translation, "t1", None)
        pivot_language = getattr(getattr(intermediate, "to_lang", None), "code", None)

    return {
        "ok": True,
        "text": out,
        "detectedSource": getattr(detected_source, "code", None),
        "usedPivot": used_pivot,
        "pivotLanguage": pivot_language,
    }


def main():
    languages, err = _installed_languages()
    if err:
        print(json.dumps({"id": -1, "ok": False, "kind": "EngineFailed", "message": err}), flush=True)
        return

    for line in sys.stdin:
        line = line.strip()
        if line and line[0] == "\ufeff":
            line = line[1:].strip()
        if not line:
            continue
        try:
            request = json.loads(line)
        except json.JSONDecodeError as exc:
            print(json.dumps({"id": -1, "ok": False, "kind": "EngineFailed", "message": f"invalid request: {exc}"}), flush=True)
            continue

        rid = request.get("id")
        if not isinstance(rid, int):
            print(json.dumps({"id": -1, "ok": False, "kind": "EngineFailed", "message": "request id must be an integer"}), flush=True)
            continue

        if request.get("ping"):
            print(json.dumps({"id": rid, "ok": True, "ready": True, "languages": [l.code for l in languages]}), flush=True)
            continue

        text = request.get("text")
        if not isinstance(text, str) or not text.strip():
            print(json.dumps({"id": rid, "ok": False, "kind": "EmptyInput", "message": "text is empty"}), flush=True)
            continue

        target = request.get("target")
        if not isinstance(target, str) or not target.strip():
            print(json.dumps({"id": rid, "ok": False, "kind": "UnsupportedLanguage", "message": "target language is required"}), flush=True)
            continue

        result = _translate(text, request.get("source"), target)
        result["id"] = rid
        print(json.dumps(result), flush=True)


if __name__ == "__main__":
    try:
        main()
    except (BrokenPipeError, KeyboardInterrupt):
        pass
