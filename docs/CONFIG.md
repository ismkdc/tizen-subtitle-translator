# Configuration

Config file: `/opt/usr/subtitle-ocr/config.json`.

The helper and the runner both read it. The runner re-reads it on every loop, so
most changes apply without a restart. `POST /config` writes it atomically; manual
edits are also picked up (prefer atomic writes: write `.new`, then rename).

Example: [`config.example.json`](../config.example.json)

## Schema

```json
{
  "region":    { "x": 0.18, "y": 0.72, "w": 0.64, "h": 0.26 },
  "ocr":       { "threshold": 155, "minConfidence": 55, "neuralMinConfidence": 70, "fallback": true },
  "style":     { "size": 28, "bottom": 220, "opacity": 0.92, "color": "#FFFFFF" },
  "translate": { "source": "EN", "target": "TR", "endpoint": "" }
}
```

### `region`

| Field | Range | Meaning |
|---|---|---|
| `x` | 0–1 | left edge, relative to capture width |
| `y` | 0–1 | top edge |
| `w` | 0.05–1 | width |
| `h` | 0.03–0.8 | height |

The runner clamps: `x + w <= 1`, `y + h <= 1`. The region should cover the
subtitle strip, e.g. bottom-centre `{ "x": 0.18, "y": 0.72, "w": 0.64, "h": 0.26 }`.
If the bottom of the line is cut, increase `h` (or `y` + `h`).

The app's **Source region** screen edits these values with presets.

### `ocr`

| Field | Default | Meaning |
|---|---|---|
| `threshold` | 155 | bright-pixel threshold for band detection |
| `minConfidence` | 55 | Tesseract word confidence filter |
| `neuralMinConfidence` | 70 | neural OCR confidence filter |
| `fallback` | true | try the other engine when one fails |

### `style`

| Field | Default | Meaning |
|---|---|---|
| `size` | 28 | font size, 1080p canvas |
| `bottom` | 220 | bottom edge y of the band (1080p); **small = top** |
| `opacity` | 0.92 | background opacity 0.2–1.0 |
| `color` | `#FFFFFF` | text colour `#RRGGBB` |

`bottom` examples:

- `90–250` → near the top (below the TV's status area).
- `560` → middle.
- `1000` → bottom.

The app's **Subtitle style** screen has **Top / Middle / Bottom** presets.

### `translate`

| Field | Default | Meaning |
|---|---|---|
| `source` | `EN` | source language code |
| `target` | `TR` | target language code |
| `endpoint` | `""` | empty = DeepL; set = local `/translate` URL |

Example local endpoint:

```json
"endpoint": "http://192.168.1.20:18881/translate"
```

If the endpoint fails, the runner falls back to DeepL when `deepl-key` exists.

## Hot reload

- The runner reads `region`, `ocr`, `style`, `translate` every loop (≈250–600 ms).
- The helper reads config on `/state`, `/config`, `/start`.
- Changing the region while running changes the crop on the next frame.
- Changing `endpoint` applies to the next translation request.

## Validation and clamping

The helper clamps values on `POST /config`:

- region `w` 0.05–1, `h` 0.03–0.8, `x/y` inside;
- `threshold` 80–240;
- `minConfidence` 20–99;
- `size` 20–64;
- `bottom` 60–1040;
- `opacity` 0.2–1.0;
- `color` must match `^#[0-9A-Fa-f]{6}$`.

The runner also clamps while reading, so a malformed file cannot crash the loop.

## Common recipes

**Top subtitles, larger font:**

```json
{
  "region": { "x": 0.18, "y": 0.72, "w": 0.64, "h": 0.26 },
  "style": { "size": 32, "bottom": 260, "opacity": 0.95, "color": "#FFFFFF" }
}
```

**Bottom subtitles:**

```json
{ "style": { "size": 28, "bottom": 1000, "opacity": 0.85, "color": "#FFFFFF" } }
```

**Full-width OCR:**

```json
{ "region": { "x": 0.0, "y": 0.60, "w": 1.0, "h": 0.40 } }
```

**Neural OCR only (slower, more accurate):**

```json
{ "ocr": { "minConfidence": 70, "neuralMinConfidence": 70, "fallback": false } }
```

> With `fallback: false`, if the neural engine returns nothing the frame is
> skipped. Tesseract-first is the fast default.
