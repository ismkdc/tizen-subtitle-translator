# Installation

Tested on a Samsung QE55Q70AATXTK (Q70A, 2021, Tizen 6.0). Other 2019+ Tizen TVs
should work, but the capture API and overlay behaviour vary by model and firmware.

## 0. Prerequisites

- A Samsung Tizen TV (2019 or newer).
- A Mac/PC on the same LAN.
- .NET SDK (the projects target `netcoreapp3.1`).
- Python 3.10+ (only for the optional local translation server).
- `zip`.
- [Apps2Samsung CLI](https://github.com/Apps2Samsung/Apps2Samsung) and a valid
  Samsung certificate profile covering the TV's DUID.
- [Vetro](https://github.com/andrearicci93/vetro) installed on the TV (root).

> **Root is mandatory.** Screen capture, the EFL overlay, and the bind mount all
> need root. Vetro provides the root agent and the local SDB route.

## 1. Root the TV with Vetro

Follow Vetro's installation guide. The short version:

1. Install Vetro with Apps2Samsung (or Tizen Studio).
2. Open Vetro once on the TV.
3. In Developer Mode set **Host PC IP** to `127.0.0.1`, then reboot the TV.
4. Confirm the root agent:

```bash
curl -s http://192.168.1.10:8099/status | jq .agent
# { "connected": true, "uid": 0, ... }
```

Vetro's dashboard is at `http://TV_IP:8099/`.

> The dashboard and `/exec` are **unauthenticated**. Keep the TV on a trusted LAN.

## 2. Developer Mode and WGT signing

1. On the TV: `Apps` screen → `1 2 3 4 5`.
   - 2022 and older: type `1 2 3 4 5` directly on the Apps screen.
   - 2023 and newer: open `Settings` at the bottom of the Apps page, then type
     `1 2 3 4 5`.
2. Enable **Developer mode**; set **Host PC IP** to the Mac's IP.
3. Restart the TV (hold the power button until the Samsung logo appears).
4. Install/verify the certificate profile with Apps2Samsung:

```bash
apps2samsung-cli tv 192.168.1.10
# ready, developer mode on, host 192.168.1.10, DUID ...
apps2samsung-cli cert
# Partner: valid ..., covers 1 TV(s)
```

> Apps2Samsung must see the TV's developer host IP equal to the Mac's IP. If Vetro
> has set it to `127.0.0.1`, temporarily set it to the Mac's IP for the install,
> then restore `127.0.0.1` and reboot for Vetro.

## 3. Build the WGT

```bash
bash scripts/build-wgt.sh
# dist/SubtitleTV.wgt
```

## 4. Install the WGT

```bash
apps2samsung-cli install dist/SubtitleTV.wgt --tv 192.168.1.10 -y
apps2samsung-cli launch SubtitleTV.subtitle --tv 192.168.1.10
```

The CLI signs the package with the local Samsung certificate (author +
distributor, TV DUID) and installs it. If you prefer, use Tizen Studio and
`tizen install`.

## 5. Build the .NET components

```bash
dotnet build runner/SubtitleRunner.csproj   -c Release
dotnet build overlay/OverlayProbe.csproj    -c Release
dotnet build helper-dotnet/SubtitleHelper.csproj -c Release
dotnet build probe/FrameProbe.csproj        -c Release
```

Outputs:

```text
runner/bin/Release/netcoreapp3.1/SubtitleRunner.dll
runner/bin/Release/netcoreapp3.1/SubtitleRunner.runtimeconfig.json
overlay/bin/Release/netcoreapp3.1/OverlayProbe.dll
overlay/bin/Release/netcoreapp3.1/OverlayProbe.runtimeconfig.json
helper-dotnet/bin/Release/netcoreapp3.1/SubtitleHelper.dll
helper-dotnet/bin/Release/netcoreapp3.1/SubtitleHelper.runtimeconfig.json
probe/bin/Release/netcoreapp3.1/FrameProbe.dll
```

## 6. Deploy to the TV

Serve the repository over HTTP:

```bash
python3 -m http.server 8000 --bind 192.168.1.20
```

Then:

```bash
TV_IP=192.168.1.10 MAC_IP=192.168.1.20 python3 scripts/deploy-tv.py
```

The script downloads the DLLs/runtimeconfigs into:

```text
/opt/usr/subtitle-ocr/SubtitleRunner.dll
/opt/usr/subtitle-ocr/SubtitleRunner.runtimeconfig.json
/opt/usr/subtitle-ocr/OverlayProbe.dll
/opt/usr/subtitle-ocr/OverlayProbe.runtimeconfig.json
/opt/usr/subtitle-helper/SubtitleHelper.dll
/opt/usr/subtitle-helper/SubtitleHelper.runtimeconfig.json
```

It then starts the helper:

```bash
systemctl stop subtitle-helper 2>/dev/null
systemctl reset-failed subtitle-helper 2>/dev/null
systemd-run --unit=subtitle-helper --collect \
  /usr/bin/dotnet /opt/usr/subtitle-helper/SubtitleHelper.dll
curl -s http://127.0.0.1:18765/health
```

If `deploy-tv.py` cannot reach Vetro, run the same commands manually through
`http://TV_IP:8099/exec`.

## 7. OCR binaries and models

The repository does **not** contain the ARM binaries or the OCR models. On the TV:

```text
/opt/usr/subtitle-ocr/tesseract                  # Tesseract 5.5.1 (ARM)
/opt/usr/subtitle-ocr/tessdata/eng.traineddata   # Tesseract model (~4 MB)
/opt/usr/subtitle-ocr/neural-worker              # MNN CRNN worker
/opt/usr/subtitle-ocr/rec.mnn                    # OCR model
/opt/usr/subtitle-ocr/rec-keys.txt               # vocabulary
```

Build instructions: [`DEVELOPMENT.md`](DEVELOPMENT.md) and [`OCR.md`](OCR.md).

Without `eng.traineddata` the Tesseract path fails; without `rec.mnn` the neural
fallback fails. The runner still runs, but OCR returns nothing.

## 8. Optional: DeepL key

```bash
# on the TV, as root
umask 077
printf '%s' 'YOUR-DEEPL-KEY' > /opt/usr/subtitle-ocr/deepl-key
chmod 600 /opt/usr/subtitle-ocr/deepl-key
```

If no key is present and no local endpoint is set, translation fails; the status
API reports `deepl_key_missing`.

## 9. Local translation server (recommended)

```bash
bash scripts/install-translation.sh

# Argos (fast)
./translate-env/bin/python servers/translate-server.py      # 0.0.0.0:18880

# NLLB-600M (better quality)
./translate-env/bin/python servers/nllb-server.py           # 0.0.0.0:18881
```

Both servers expose `GET /health` and `POST /translate`.

Point the TV at one of them:

```json
"translate": {
  "source": "EN",
  "target": "TR",
  "endpoint": "http://192.168.1.20:18881/translate"
}
```

## 10. Configure and run

```bash
# on the TV
cat /opt/usr/subtitle-ocr/config.json
```

Start from the app (**SubtitleTV → Start**) or via the helper:

```bash
curl -s -X POST http://127.0.0.1:18765/start | jq .
curl -s http://127.0.0.1:18765/state | jq .
```

Expected state:

```json
{
  "ok": true,
  "running": true,
  "capture": { "w": 720, "h": 576 },
  "last": { "ocr": "Spider-Man: ...", "tr": "...", "engine": "tesseract", "conf": 91 },
  "error": null
}
```

## 11. Updating

- Replace the DLLs in `/opt/usr/subtitle-ocr` and `/opt/usr/subtitle-helper`.
- Restart the helper (`systemctl restart subtitle-helper` or stop + `systemd-run`).
- Rebuild/reinstall the WGT if `app/` changed.

## 12. Uninstalling

```bash
# on the TV
curl -s -X POST http://127.0.0.1:18765/stop
systemctl stop subtitle-helper
rm -rf /opt/usr/subtitle-helper /opt/usr/subtitle-ocr /tmp/subtitle-probe
pkgcmd -u -n SubtitleTV
```

Keep `/opt/usr/subtitle-ocr/deepl-key` if you want to reuse it elsewhere.

## Troubleshooting

See [`TROUBLESHOOTING.md`](TROUBLESHOOTING.md). The most common first-run issues:

- `r=-1` / `TZCAPTURE_ACCESS_DENIED` → the runner was not started through
  `exec -a /usr/bin/tzcapturesample`; redeploy the helper.
- `neural_IOException` → the neural worker died; the runner restarts it.
- `local_*` → the Mac server is down; DeepL fallback is used.
- No app icon → the WGT was not signed with a certificate covering the TV's DUID.
