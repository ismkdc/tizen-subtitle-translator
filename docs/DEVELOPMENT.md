# Development

## 1. Code map

```text
app/                 Tizen WGT (HTML/CSS/JS)
helper-dotnet/       root helper: HTTP API, start/stop, config
runner/              capture + OCR + translation + scene
  Program.cs         main loop, OCR, translation, status
  Capture.cs         ppicapture P/Invoke
overlay/             EFL notification window + scene renderer
probe/               single-frame capture tool
ocr-workers/         neural-worker.cpp (MNN), ocr-worker.cpp (Tesseract C API)
servers/             Argos and NLLB HTTP servers
docs/legacy/         runner-v1 (old Tesseract-only prototype)
scripts/             build/deploy/install helpers
```

## 2. .NET build

Target: `netcoreapp3.1` (the TV's `/usr/bin/dotnet` runs these assemblies).

```bash
dotnet build runner/SubtitleRunner.csproj      -c Release
dotnet build overlay/OverlayProbe.csproj       -c Release
dotnet build helper-dotnet/SubtitleHelper.csproj -c Release
dotnet build probe/FrameProbe.csproj           -c Release
```

The outputs are DLL + runtimeconfig; deploy both.

## 3. WGT build

```bash
bash scripts/build-wgt.sh
# dist/SubtitleTV.wgt
```

The app is plain HTML/CSS/JS; no bundler. `config.xml` declares the package
`SubtitleTV`, app `SubtitleTV.subtitle`, `tv-samsung` profile, and network access.

## 4. OCR workers (ARM)

The repository contains the C++ sources; the TV binaries are not shipped.

### Tesseract worker

- Links against Tesseract/Leptonica (static recommended).
- `ocr-worker.cpp`: `TessBaseAPI`, `OEM_LSTM_ONLY`, `PSM_SINGLE_BLOCK`; reads PGM
  paths on stdin, prints `READY` and `RESULT\tconf\tms\ttext`.
- Build with the Tizen/ARM toolchain; install `/opt/usr/subtitle-ocr/ocr-worker`.
- The runner currently uses the `tesseract` CLI for `--psm 7/6`; the worker is an
  alternative for a persistent PSM 6 engine.

### Neural worker (MNN)

- `neural-worker.cpp`: MNN interpreter, `rec.mnn`, `rec-keys.txt`; resizes to height
  48; greedy decode; prints `READY`/`RESULT`.
- Build with MNN's ARM static library (`libMNN.a`) and the Tizen sysroot.
- `rec.mnn` + `rec-keys.txt` are models; keep them out of git (`*.mnn` is ignored).

### Models

- Tesseract: `tessdata/eng.traineddata` (~4 MB).
- Neural OCR: `rec.mnn` (~21 MB) + `rec-keys.txt`.
- They are installed into `/opt/usr/subtitle-ocr/` on the TV; see
  [`INSTALL.md`](INSTALL.md).

## 5. Local translation servers

```bash
bash scripts/install-translation.sh
./translate-env/bin/python servers/translate-server.py   # Argos :18880
./translate-env/bin/python servers/nllb-server.py        # NLLB :18881
```

NLLB conversion:

```bash
./translate-env/bin/ct2-transformers-converter \
  --model facebook/nllb-200-distilled-600M \
  --output_dir nllb600-ct2 --quantization int8 --force
```

The Argos package is installed by `argostranslate.package` (en→tr).

## 6. Deploy

```bash
bash scripts/build-wgt.sh
apps2samsung-cli install dist/SubtitleTV.wgt --tv 192.168.1.10 -y

python3 -m http.server 8000 --bind 192.168.1.20
TV_IP=192.168.1.10 MAC_IP=192.168.1.20 python3 scripts/deploy-tv.py
```

The helper is started with `systemd-run` (see `scripts/deploy-tv.py`).

## 7. Debugging

```bash
# TV
cat /tmp/subtitle-probe/runner.log
cat /tmp/subtitle-probe/status.json
cat /tmp/subtitle-probe/live-scene.json
ps -ef | grep -E 'SubtitleRunner|OverlayProbe|neural-worker|ocr-worker'
curl -s http://127.0.0.1:18765/state

# capture-only test
bash -c 'exec -a /usr/bin/tzcapturesample /usr/bin/dotnet /tmp/subtitle-probe/FrameProbe.dll'
```

`FrameProbe` writes `frame.y`/`frame.c`/`frame.meta`; extract the Y plane at
`rw × rh`, stride `rw`.

## 8. Testing

The repository has no automated hardware tests. Manual checks:

- `probe` returns `r=0` and a plausible frame.
- `status.json` shows `capture`, `last.ocr`, `engine`, `conf`.
- The overlay shows the test scene (`live-scene.json` with a subtitle object).
- Stop/start from the app; the helper restarts the pipeline.

## 9. Style

- C#: small files, no external packages; `netcoreapp3.1`; ASCII identifiers.
- JS: ES5-compatible (Tizen 6 WebView), no bundler.
- Docs: English; commands with placeholders (`192.168.1.10`, `192.168.1.20`).
- Never commit keys, models, binaries, or local IPs.
