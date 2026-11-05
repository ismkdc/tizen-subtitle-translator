#!/usr/bin/env python3
"""Local EN->TR translation server for the SubtitleTV runner.

Runs on the Mac (LAN) with Argos Translate / CTranslate2. The TV sends one
line at a time and gets the translation back in ~30-60 ms, so the cloud
latency disappears.
"""
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import argostranslate.translate as translate

# Warm up the model once so the first real request is fast.
translate.translate("warmup", "en", "tr")
_lock = threading.Lock()


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
            self._json({"ok": True})
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
        src = (data.get("source") or "en").lower()
        tgt = (data.get("target") or "tr").lower()
        if not text:
            self._json({"tr": "", "ms": 0})
            return
        started = time.time()
        try:
            with _lock:
                out = translate.translate(text, src, tgt)
        except Exception as e:  # keep the protocol simple and visible
            self._json({"tr": "", "error": str(e)})
            return
        self._json({"tr": out, "ms": int((time.time() - started) * 1000)})

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    print("translate-server on 0.0.0.0:18880", flush=True)
    ThreadingHTTPServer(("0.0.0.0", 18880), Handler).serve_forever()
