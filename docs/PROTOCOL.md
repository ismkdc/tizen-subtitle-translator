# Protocol

Two HTTP interfaces: the TV helper (`127.0.0.1:18765`) and the optional LAN
translation server (`:18880` Argos / `:18881` NLLB).

## 1. Helper API (`127.0.0.1:18765`)

All responses are JSON; CORS is `*`; no authentication (localhost only).

### `GET /health`

```json
{ "ok": true, "service": "subtitle-helper", "version": 1 }
```

### `GET /state`

```json
{
  "ok": true,
  "running": true,
  "config": {
    "region": { "x": 0.18, "y": 0.72, "w": 0.64, "h": 0.26 },
    "ocr": { "threshold": 155, "minConfidence": 55 },
    "style": { "size": 28, "bottom": 220, "opacity": 0.92, "color": "#FFFFFF" },
    "translate": { "source": "EN", "target": "TR" }
  },
  "last": {
    "ocr": "Spider-Man: You guys were in bed with Fisk all along?",
    "tr": "Örümcek Adam: Baştan beri Fisk'le iş birliği içinde miydiniz?",
    "ocr_ms": 183,
    "tr_ms": 312,
    "engine": "tesseract",
    "conf": 91
  },
  "capture": { "w": 720, "h": 576 },
  "error": null,
  "key": true
}
```

The helper returns its own config model; fields it does not know (for example
`translate.endpoint`) are still read by the **runner** from the same file.

### `GET /config`

```json
{ "ok": true, "config": { "...": "..." } }
```

### `POST /config`

```http
POST /config
Content-Type: application/json

{ "config": { "region": { ... }, "ocr": { ... }, "style": { ... }, "translate": { ... } } }
```

Values are clamped server-side (region 0–1, confidence 20–99, overlay position
60–1040, etc.).

### `POST /start`

Stops an existing pipeline, writes the config, starts:

```text
unshare -m ... exec /usr/bin/bash -c "exec -a /usr/bin/tzcapturesample \
  /usr/bin/dotnet /opt/usr/subtitle-ocr/SubtitleRunner.dll 0"
```

Returns the state after ~1.2 s.

### `POST /stop`

Kills the runner and overlay, writes an empty scene, returns the state.

## 2. Translation API (LAN)

### `POST /translate`

```http
POST /translate
Content-Type: application/json

{ "text": "Aunt May: Okay, love you.", "source": "EN", "target": "TR" }
```

Response:

```json
{ "tr": "Aunt May: Tamam, seni seviyorum.", "ms": 100 }
```

- On error: `{ "tr": "", "error": "..." }`.
- `translate-server.py` uses Argos (`en→tr`); `nllb-server.py` uses
  NLLB-200 distilled 600M via CTranslate2.

### `GET /health`

```json
{ "ok": true, "model": "nllb-600m" }
```

## 3. Vetro bridge (`:8099`)

The TV app starts the helper through Vetro's unauthenticated `/exec`:

```http
POST /exec
Content-Type: application/json

{ "cmd": "systemctl is-active --quiet subtitle-helper || (systemctl reset-failed subtitle-helper 2>/dev/null; systemd-run --unit=subtitle-helper --collect /usr/bin/dotnet /opt/usr/subtitle-helper/SubtitleHelper.dll)" }
```

The request is sent with `mode: "no-cors"` from the WGT (the response is opaque).
Vetro runs it as root.

## 4. Status and scene files

- `/tmp/subtitle-probe/status.json` — written atomically by the runner; the helper
  reads it for `/state`.
- `/tmp/subtitle-probe/live-scene.json` — written atomically by the runner; the
  overlay reads it every 100 ms.

Scene example:

```json
{
  "objects": [
    {
      "type": "subtitle",
      "text": "Örümcek Adam: Üst katlara doğru ilerliyor…",
      "size": 28,
      "x": 260,
      "width": 1400,
      "bottom": 220,
      "padding_x": 24,
      "padding_y": 10,
      "align": "center",
      "color": "#FFFFFF",
      "background_color": "#000000EB"
    }
  ]
}
```

An empty file or `{}` hides the overlay.

## 5. Security

- The helper is localhost-only; no auth.
- Vetro `/exec` is root and unauthenticated; keep the TV on a trusted LAN.
- The translation servers bind `0.0.0.0`; expose them only on the LAN.
