# Tizen Subtitle Translator

A live subtitle translator for Tizen-based Samsung TVs. It reads the subtitles in the HDMI/PS5 picture, translates them to Turkish, and draws them over the game. No phone app is needed: a Tizen WGT UI plus a root helper, a screen-capture/OCR/translation runner, and an EFL overlay.

> **Status:** experimental / personal use. Requires root. Not affiliated with Samsung or any game publisher.

> **Naming:** the TV app is called **SubtitleTV**; the repository is `tizen-subtitle-translator`. The subtitle output language is Turkish.

---

## What it does

1. Captures the HDMI input (`libdisplay-capture-api` / `ppicapture`).
2. Finds and crops the subtitle region (row projection + text-band scoring).
3. Runs OCR: **Tesseract 5.5** (fast) or an **MNN CRNN** neural model (more accurate).
4. Translates: **DeepL** (cloud, best quality), **NLLB-200 600M** (local, balanced), or **Argos/OPUS-MT** (local, fastest).
5. Draws the Turkish line over the game with an EFL notification window (`OverlayProbe`).
6. The TV app (`SubtitleTV`) handles start/stop, region, style, and diagnostics.

## Features

- Standalone on the TV (PS5 / camera / any HDMI source), no phone.
- Tizen WGT UI: hub, subtitle style, source region, translation, diagnostics.
- Top/middle/bottom subtitle placement; size, opacity, colour.
- Automatic line detection; multi-line support; OCR text cleanup.
- Auto-restart of crashed OCR workers.
- Switchable translation backends (DeepL / NLLB / Argos).
- LAN translation server (Mac/PC) to remove cloud latency.
- Status API (`ocr_ms`, `tr_ms`, `engine`, `conf`, last OCR/translation).

## Architecture

```mermaid
flowchart LR
    HDMI[PS5 / HDMI] --> TV[Tizen TV]
    TV -->|ppicapture 720x540/576| Runner[SubtitleRunner<br/>(root)]
    Runner -->|crop| OCR{Tesseract 5.5<br/>or MNN CRNN}
    OCR -->|EN text| MT{DeepL / NLLB-600M / Argos}
    MT -->|TR| Scene[live-scene.json]
    Scene --> Overlay[EFL OverlayProbe]
    Overlay --> TV
    App[SubtitleTV WGT] -->|HTTP 127.0.0.1:18765| Helper[SubtitleHelper<br/>(root)]
    Helper -->|start / stop / config| Runner
    App -.->|starts helper via Vetro /exec| Vetro[Vetro root agent]
    MT -.->|LAN HTTP| Server[Local translation server<br/>Mac/PC]
```

### Components

| Path | What | Where |
|---|---|---|
| `app/` | Tizen WGT UI (`SubtitleTV.subtitle`) | TV (web app) |
| `helper-dotnet/` | Root helper service, HTTP API `127.0.0.1:18765` | TV (root) |
| `runner/` | Capture + OCR + translation + overlay orchestration | TV (root) |
| `overlay/` | EFL notification window (draws over the game) | TV (root) |
| `probe/` | Single-frame capture test tool | TV |
| `ocr-workers/` | `neural-worker` (MNN) and `ocr-worker` (Tesseract C API) sources | TV |
| `servers/` | Local translation HTTP servers (Argos / NLLB) | Mac/PC |
| `docs/` | Architecture, install, protocol, troubleshooting | — |
| `scripts/` | Build, TV deploy, translation environment | Mac/PC |

## Requirements

- **Samsung Tizen TV**, preferably 2019+ (developed/tested on QE55Q70AATXTK, Tizen 6.0).
- **Root** on the TV: [Vetro](https://github.com/andrearicci93/vetro) and Chris Ritsen's Samsung TV root research.
- **Developer Mode** and [Apps2Samsung](https://github.com/Apps2Samsung/Apps2Samsung) for signing/installing the WGT.
- Mac/PC: .NET SDK (netcoreapp3.1 target), `zip`, Python 3.10+ (for local translation).
- TV and Mac on the same LAN.

> **Why root?** Screen capture, the EFL notification window, and the `/run/subtitle-ocr` mount need it. A WGT alone cannot do any of these.

## Quick start

### 1) Prepare the TV

See [`docs/INSTALL.md`](docs/INSTALL.md).

- Install Vetro with Apps2Samsung, reboot the TV.
- Enable Developer Mode (`Apps` screen, `1 2 3 4 5`), set `Host PC IP` to the Mac's IP.
- Confirm the Vetro root agent is connected: `http://TV_IP:8099/status` → `agent.connected: true`.

### 2) Build and install the WGT

```bash
bash scripts/build-wgt.sh
apps2samsung-cli install dist/SubtitleTV.wgt --tv 192.168.1.10 -y
```

See `docs/INSTALL.md` → *WGT signing* for the certificate profile.

### 3) Deploy runner / overlay / helper

Serve the repo root over HTTP:

```bash
python3 -m http.server 8000 --bind 192.168.1.20
```

In another terminal:

```bash
TV_IP=192.168.1.10 MAC_IP=192.168.1.20 python3 scripts/deploy-tv.py
```

The script:

- downloads `SubtitleRunner.dll`, `OverlayProbe.dll` and their runtimeconfig files to `/opt/usr/subtitle-ocr/`,
- downloads `SubtitleHelper.dll` to `/opt/usr/subtitle-helper/`,
- starts the root helper with `systemd-run`,
- writes `config.json` if missing.

`deploy-tv.py` only moves **sources**; OCR binaries/models and `deepl-key` are installed separately (below).

### 4) OCR binaries and models

See [`docs/OCR.md`](docs/OCR.md). On the TV, `/opt/usr/subtitle-ocr/` must contain:

```text
tesseract            # Tesseract 5.5.1 (ARM)
tessdata/eng.traineddata
neural-worker        # MNN CRNN worker
rec.mnn              # neural OCR model
rec-keys.txt         # character vocabulary
deepl-key            # (optional) DeepL API key, mode 600
```

The C++ sources are in `ocr-workers/`; build steps are in `docs/DEVELOPMENT.md`.

### 5) Local translation (optional, recommended)

```bash
bash scripts/install-translation.sh        # Argos en→tr
./translate-env/bin/python servers/translate-server.py     # :18880
./translate-env/bin/python servers/nllb-server.py          # :18881 (NLLB-600M)
```

In the TV config:

```json
"translate": {
  "source": "EN",
  "target": "TR",
  "endpoint": "http://192.168.1.20:18881/translate"
}
```

If `endpoint` is empty, the runner uses **DeepL**.

### 6) Open the app

On the TV: **SubtitleTV** → check the region → **Start**. Play a game/cutscene; the Turkish band appears.

## Configuration

File: `/opt/usr/subtitle-ocr/config.json` (example: [`config.example.json`](config.example.json))

| Field | Description |
|---|---|
| `region.x/y/w/h` | OCR rectangle, 0–1 relative to the capture |
| `ocr.threshold` | Bright-text threshold (155) |
| `ocr.minConfidence` | Tesseract word confidence (55) |
| `ocr.neuralMinConfidence` | Neural OCR confidence (70) |
| `ocr.fallback` | Fall back to neural when Tesseract fails |
| `style.size` | Overlay font size (1080p coordinates) |
| `style.bottom` | Bottom edge of the band (small value = top, e.g. 220) |
| `style.opacity` | Background opacity (0.2–1.0) |
| `style.color` | Text colour (`#RRGGBB`) |
| `translate.endpoint` | Empty = DeepL; set = local `/translate` |
| `translate.source/target` | Language pair |

Details: [`docs/CONFIG.md`](docs/CONFIG.md).

## Translation backends

| Backend | Latency (TV→) | Quality | Notes |
|---|---|---|---|
| **Argos / OPUS-MT** | ~30–50 ms | Low | Fastest; literal |
| **NLLB-200 600M int8** | ~100–165 ms | Good | Balanced; CTranslate2 on a Mac |
| **DeepL** | ~300–900 ms | Best | API key; cloud |

Measured end-to-end: **~0.2–0.3 s** (Argos), **~0.3–0.5 s** (NLLB), **~0.6–1.2 s** (DeepL). OCR and capture are included.

> The NLLB-200 model is licensed **CC-BY-NC 4.0** (non-commercial). Check the licence for commercial use.

Details: [`docs/TRANSLATION.md`](docs/TRANSLATION.md).

## OCR pipeline

- Bright rows are found inside the region (row projection).
- The widest/largest text band is chosen; UI icons and HUD are skipped.
- Single line → Tesseract `--psm 7`; multi-line → `--psm 6`.
- The image is **raw grayscale, inverted, upscaled 2x** (no hard binarisation).
- If confidence is low, the neural MNN worker is used.
- OCR text is cleaned: leading game glyphs (`==>`, `|`), trailing noise, speaker prefixes.

Details: [`docs/OCR.md`](docs/OCR.md).

## Screen capture

- `libdisplay-capture-api.so.0` → `ppicapture(RequestedData*, y, c, size)`.
- Typical frame: **720×540 / 720×576**, Y-plane stride = width.
- Samsung's process whitelist rejects `/usr/bin/dotnet` (`TZCAPTURE_ACCESS_DENIED`).
- Workaround: start the runner with `exec -a /usr/bin/tzcapturesample` so `argv[0]` matches a whitelisted name.

This bypasses Samsung's capture permission check; use it **only on your own device**.

Details: [`docs/CAPTURE.md`](docs/CAPTURE.md).

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `r=-1`, `TZCAPTURE_ACCESS_DENIED` | Runner not started with `exec -a /usr/bin/tzcapturesample`; check the helper's `StartPipeline` |
| Black band but no text | Overlay is showing an empty scene; OCR text is empty or below confidence |
| `neural_IOException` | Neural worker crashed; the runner restarts it; check RAM/CPU |
| `local_*` | Local translation server is down; DeepL fallback is used |
| `translation_http_429/456` | DeepL rate limit/quota; switch to the local endpoint |
| No app on the TV | WGT not signed / wrong TV; install with Apps2Samsung |
| Helper won't start | Vetro agent not connected; check `http://TV_IP:8099/status` |
| Wrong region | App → **Source region**; or `config.json` → `region` |
| Bottom of subtitles cut | Increase `region.h`; see the crop padding in `docs/OCR.md` |
| Small HUD/tutorial text unreadable | 720×576 capture limit; dialogue subtitles have a larger font and work better |

Details: [`docs/TROUBLESHOOTING.md`](docs/TROUBLESHOOTING.md).

## Security

- The helper binds `127.0.0.1:18765`; no authentication.
- Vetro's dashboard (`:8099`) and `/exec` run **unauthenticated root commands**. Do not expose the TV to untrusted networks.
- The DeepL key lives on the TV at `/opt/usr/subtitle-ocr/deepl-key` (mode 600); never commit it.
- The capture whitelist bypass is for your own device only.
- Root affects the TV warranty and update security.

Details: [`docs/SECURITY.md`](docs/SECURITY.md).

## Repository layout

```text
app/                 Tizen WGT (config.xml, index.html, css, js, icon)
helper-dotnet/       Root helper (C#)
runner/              Capture + OCR + translation + overlay (C#)
overlay/             EFL overlay (C#)
probe/               Single-frame capture test (C#)
ocr-workers/         neural-worker.cpp, ocr-worker.cpp
servers/             translate-server.py (Argos), nllb-server.py (NLLB)
scripts/             build-wgt.sh, deploy-tv.py, install-translation.sh
docs/                architecture, install, protocol, OCR, translation, security
config.example.json
```

## Development

- .NET projects: `dotnet build -c Release` (netcoreapp3.1).
- WGT: `bash scripts/build-wgt.sh`.
- OCR workers: ARM toolchain + MNN/Tesseract; [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md).
- Local translation: `scripts/install-translation.sh`; NLLB conversion in `docs/TRANSLATION.md`.
- Logs: TV `/tmp/subtitle-probe/runner.log`; helper `/tmp/subtitle-helper.log`.

## Credits

- [Vetro](https://github.com/andrearicci93/vetro) — Samsung Tizen root manager and root agent.
- [Chris Ritsen](https://github.com/chris-ritsen/samsung-tv-root) — Samsung TV root research.
- [Apps2Samsung](https://github.com/Apps2Samsung/Apps2Samsung) — WGT signing/install.
- [MNN](https://github.com/alibaba/MNN) — neural OCR runtime.
- [Tesseract](https://github.com/tesseract-ocr/tesseract) — OCR engine.
- [Argos Translate](https://github.com/argosopentech/argos-translate) / [OPUS-MT](https://github.com/Helsinki-NLP/OPUS-MT) — local translation.
- [NLLB-200](https://github.com/facebookresearch/fairseq/tree/nllb) — local translation model.
- DeepL — cloud translation.

## License

Code: **MIT** (see [`LICENSE`](LICENSE)).
Third-party components and models keep their own licences; see [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).

---

**Warning:** experimental research/personal-use tooling. Use it on your own TV and your own lawful content. Game subtitles and broadcast content are copyrighted; do not redistribute translations.
