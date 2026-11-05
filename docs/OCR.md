# OCR pipeline

The runner turns one captured frame into one text line (or a short block), then
hands the text to a translation backend.

## 1. Region

Config `region` (0–1 relative to the captured frame):

```json
{ "x": 0.18, "y": 0.72, "w": 0.64, "h": 0.26 }
```

This is the bottom-centre rectangle where the game draws subtitles/prompts.
The app exposes it as **Source region** with presets.

## 2. Text-band detection

Inside the region:

1. For every row, count pixels brighter than `max(190, ocr.threshold)`.
2. Find contiguous bands of rows whose count ≥ `max(8, maxCount/3)` and height ≥ 8.
3. Reject solid lines/structures (`maxRow > 0.70 * width`) — these are railings,
   window frames, etc.
4. Score each remaining band by `xExtent × height` and keep the largest. This
   selects the subtitle line and skips small UI icons/HUD.
5. If a second band starts within 16 rows, the line is **multi-line**; the crop is
   extended by up to 40 px and the engine uses `--psm 6`.

Crop padding: `xpad = max(32, width/3)`, `ypad = max(12, height/2)`, then clamp.

## 3. Pre-processing

The crop is:

- converted to raw grayscale (Y plane),
- **inverted** (white text → dark text on light background),
- upscaled **2x** (nearest-neighbour) for Tesseract,
- written as `P5` PGM `/tmp/subtitle-probe/input.pgm`.

No hard binarisation: anti-aliased edges survive, which reads better on the game
fonts than a `>155 ? 0 : 255` threshold.

## 4. Engines

### Tesseract (default/fast)

```bash
/run/subtitle-ocr/tesseract input.pgm stdout \
  --tessdata-dir /run/subtitle-ocr/tessdata -l eng \
  --psm 7 -c tessedit_create_tsv=1
```

- `--psm 7` for a single line, `--psm 6` for a block.
- TSV is parsed: `level=5` word rows, `conf` column 10, text column 11.
- Words with `conf >= ocr.minConfidence` (55) are kept; the average confidence is
  reported as `conf`.
- The CLI is spawned per frame (~100–270 ms). A persistent `ocr-worker` (Tesseract
  C API, PSM_SINGLE_BLOCK) is also included in `ocr-workers/`; it reads a PGM path
  and prints `READY` / `RESULT\tconf\tms\ttext`.

### Neural MNN (accurate)

```bash
/run/subtitle-ocr/neural-worker /run/subtitle-ocr/rec.mnn /run/subtitle-ocr/rec-keys.txt
```

- Persistent worker: prints `READY`; each input is a PGM path; output is
  `RESULT\tconf\tms\ttext`.
- Resizes the crop to height 48 (width = ceil(48*w/h), clamped 32..1536), 3-channel
  input, greedy decode.
- `ocr.neuralMinConfidence` (70) filters low-confidence output.
- Falls back automatically if the Tesseract text is invalid (`ocr.fallback`).

## 5. Text cleanup

`CleanOcr()` in `runner/Program.cs`:

- collapses whitespace;
- drops trailing noise after the last sentence punctuation (`.`, `?`, `!`);
- extracts a speaker prefix (`Aunt May:`, `Fisk Thug:`) and drops leading glyphs
  from the game UI (`==>`, `|`);
- `Valid()` then requires enough letters, at least two words, ≤ 2 punctuation
  characters, and no single-letter junk words.

## 6. Confidence and fallback

| Path | Threshold |
|---|---|
| Tesseract | `ocr.minConfidence` (55) |
| Neural | `ocr.neuralMinConfidence` (70) |
| Clean/Valid | structural checks |

If the chosen engine returns nothing or invalid text:
- with `ocr.fallback: true`, the other engine is tried;
- otherwise the frame is skipped and the previous overlay stays for a few seconds.

## 7. Known limits

- The capture is 720×540/576; small tutorial/HUD text is often unreadable.
- Dialogue subtitles use a larger font and work best.
- Decorative game fonts (RDR2 notifications, etc.) can confuse both engines.
- Fast lines may change before OCR+translation finishes; the overlay shows the
  latest completed translation for a few seconds.
- The bottom line can be cut if `region.h` is too small; increase it or the crop
  bottom margin.
