# Translation

The runner sends the cleaned English text to one of three backends. Selection is
by configuration, not code changes.

## 1. Selection

```json
"translate": {
  "source": "EN",
  "target": "TR",
  "endpoint": ""
}
```

- `endpoint` empty → **DeepL** (`https://api-free.deepl.com`) using
  `/opt/usr/subtitle-ocr/deepl-key`.
- `endpoint` set → **local server** first; on failure the runner falls back to
  DeepL if a key exists.

The local server contract:

```http
POST /translate
Content-Type: application/json

{ "text": "Aunt May: Okay, love you.", "source": "EN", "target": "TR" }
```

```json
{ "tr": "Aunt May: Tamam, seni seviyorum.", "ms": 100 }
```

## 2. Backends

### DeepL (cloud, best quality)

- Latency ~300–900 ms; quality is the reference.
- Requires an API key. The free tier has a monthly character quota and rate
  limits (`translation_http_429` / `456` in the log).
- The runner caches up to 300 translated lines.

### NLLB-200-distilled-600M (local, balanced)

- ~100–165 ms on an M4 Pro with CTranslate2 int8.
- Better than Argos/OPUS-MT, close to DeepL on game dialogue.
- **Licence: CC-BY-NC 4.0 (non-commercial).**

Convert once:

```bash
python3 -m venv translate-env
./translate-env/bin/pip install ctranslate2 transformers sentencepiece huggingface_hub

./translate-env/bin/ct2-transformers-converter \
  --model facebook/nllb-200-distilled-600M \
  --output_dir nllb600-ct2 \
  --quantization int8 --force
```

Run the server:

```bash
./translate-env/bin/python servers/nllb-server.py     # 0.0.0.0:18881
```

The server:

- loads the tokenizer (`AutoTokenizer`, `src_lang=eng_Latn`);
- translates with `target_prefix=[['tur_Latn']]`, `beam_size=1`;
- preserves the speaker prefix (`Aunt May:`) when the model drops it.

### Argos / OPUS-MT (local, fastest)

- ~30–50 ms; the quality is noticeably lower and literal.
- Install:

```bash
bash scripts/install-translation.sh
./translate-env/bin/python servers/translate-server.py   # 0.0.0.0:18880
```

The script creates the venv, installs `argostranslate`, and downloads the
`en→tr` package.

## 3. Measured quality

| Source | Argos | NLLB-600M | DeepL |
|---|---|---|---|
| Even the rats are staying away | sıçanlar bile uzak kalıyor | Fareler bile uzakta kalıyor . | Fareler bile uzak duruyor. |
| Aunt May: Okay, love you. | — | Aunt May: Tamam, seni seviyorum. | May Teyze: Tamam, seni seviyorum. |
| Fisk Thug: Mr. Fisk says hi. | — | Fisk Thug: Bay Fisk selam söylüyor. | Fisk Thug: Bay Fisk selam söylüyor. |

NLLB preserves speaker labels; DeepL also translates titles (`Aunt May` → `May
Teyze`).

## 4. Latency budget

| Step | Argos | NLLB | DeepL |
|---|---|---|---|
| Capture | 40–80 ms | 40–80 ms | 40–80 ms |
| Crop + OCR | 100–270 ms | 100–270 ms | 100–270 ms |
| Translation | 30–50 ms | 100–165 ms | 300–900 ms |
| Overlay | ~10 ms | ~10 ms | ~10 ms |
| **Total** | **~0.2–0.3 s** | **~0.3–0.5 s** | **~0.6–1.2 s** |

These are measured on a Q70A + Mac (M4 Pro) LAN. Neural OCR (when it is used as a
fallback) adds 500–700 ms.

## 5. Caching and stale lines

- The runner caches translations by `target|text` (up to 300 entries).
- If a line changes while a request is in flight, the result is still displayed
  briefly (stale display), then replaced by the next line's translation.
- The overlay keeps the last translation for ~8 s after the line leaves the
  screen, so short gaps do not blank the band.

## 6. Troubleshooting

| Log | Meaning |
|---|---|
| `local_IOException` | local server unreachable; DeepL fallback |
| `translation_http_429` | DeepL rate limit; retry/backoff or use local |
| `translation_http_456` | DeepL quota exhausted |
| `deepl_key_missing` | no key and no endpoint |
| `translation_TimeoutException` | network/API timeout (5 s) |

See [`TROUBLESHOOTING.md`](TROUBLESHOOTING.md).
