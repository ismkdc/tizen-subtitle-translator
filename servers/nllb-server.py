#!/usr/bin/env python3
"""Local EN->TR translation server using NLLB-200-distilled-600M (CTranslate2 int8).

Better quality than the small Argos/OPUS model, still ~100-200 ms per line on
an M4 Pro. Speaker prefixes ("Aunt May:", "Fisk Thug:") are preserved when the
model drops them.
"""
import json
import re
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import ctranslate2
from transformers import AutoTokenizer

MODEL_DIR = "nllb600-ct2"
TOKENIZER = "facebook/nllb-200-distilled-600M"
TARGET_LANG = "tur_Latn"

_tokenizer = AutoTokenizer.from_pretrained(TOKENIZER, src_lang="eng_Latn")
_translator = ctranslate2.Translator(MODEL_DIR, device="cpu", compute_type="int8")
_lock = threading.Lock()
_SPEAKER = re.compile(r"^([A-Za-z0-9][A-Za-z0-9 .'\-]{1,39}):\s*(.+)$")


def _translate(text: str) -> str:
    ids = _tokenizer(text).input_ids
    tokens = _tokenizer.convert_ids_to_tokens(ids)
    result = _translator.translate_batch(
        [tokens], target_prefix=[[TARGET_LANG]], beam_size=1, max_decoding_length=160
    )
    out_ids = _tokenizer.convert_tokens_to_ids(result[0].hypotheses[0][1:])
    return _tokenizer.decode(out_ids, skip_special_tokens=True).strip()


def translate(text: str) -> str:
    m = _SPEAKER.match(text)
    if m:
        body = _translate(m.group(2))
        return f"{m.group(1)}: {body}" if body else text
    return _translate(text)


# Warm up so the first real request is not penalised by lazy init.
translate("Hello, how are you?")


class Handler(BaseHTTPRequestHandler):
    def _json(self, obj):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("content-type", "application/json; charset=utf-8")
        self.send_header("content-length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.split("?")[0] in ("/health", "/"):
            self._json({"ok": True, "model": "nllb-600m"})
        else:
            self.send_error(404)

    def do_POST(self):
        if self.path.split("?")[0] != "/translate":
            self.send_error(404)
            return
        try:
            n = int(self.headers.get("Content-Length", "0"))
            data = json.loads(self.rfile.read(n) or b"{}")
        except Exception:
            self.send_error(400)
            return
        text = (data.get("text") or "").strip()
        if not text:
            self._json({"tr": "", "ms": 0})
            return
        started = time.time()
        try:
            with _lock:
                out = translate(text)
        except Exception as e:
            self._json({"tr": "", "error": str(e)})
            return
        self._json({"tr": out, "ms": int((time.time() - started) * 1000)})

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    print("nllb translate-server on 0.0.0.0:18881", flush=True)
    ThreadingHTTPServer(("0.0.0.0", 18881), Handler).serve_forever()
