# Troubleshooting

## Capture

### `r=-1`, `ysz=0`, `TZCAPTURE_ACCESS_DENIED`

The runner is not started with the whitelisted `argv[0]`. The helper must use:

```text
exec /usr/bin/bash -c "exec -a /usr/bin/tzcapturesample /usr/bin/dotnet /opt/usr/subtitle-ocr/SubtitleRunner.dll 0"
```

Check the helper binary is the one from this repo and restarted:

```bash
systemctl stop subtitle-helper
systemctl reset-failed subtitle-helper
systemd-run --unit=subtitle-helper --collect /usr/bin/dotnet /opt/usr/subtitle-helper/SubtitleHelper.dll
curl -s http://127.0.0.1:18765/state
```

### Capture worked, then stopped

The TV input/secure state changed. Stop/start the pipeline; if it persists,
reboot the TV, reopen the app, and start again.

### `capture: { "w": 720, "h": 576 }` but OCR sees nothing

Expected resolution limit. See [OCR.md](OCR.md); small HUD/tutorial text is often
unreadable, dialogue subtitles are larger.

## OCR

### `last.ocr` is empty, but the screen has text

- Region is wrong: adjust `region` in the app (**Source region**) or config.
- Text is below the confidence threshold (`conf`), or `Valid()` rejected it.
- The band was classified as a solid line/structure; increase `region` precision.
- The crop is too tight: increase `xpad`/`ypad` in `runner/Program.cs` and rebuild.

### Left/right of the line is cut (`der-Man`, `Spider-Mar`)

Increase the crop x padding (default `max(32, width/3)`) or widen `region.x/w`.
The Tesseract/neural models need a few pixels of margin around the glyphs.

### Bottom of the line is cut

Increase `region.h` and/or the crop bottom margin (default `+10 px` single,
`+40 px` multi-line). The `bandTop/bandBottom` clamp keeps it inside the frame.

### `neural_IOException`

The neural worker died (CPU/RAM, model load, or a bad PGM). The runner restarts it
on the next frame. If it happens repeatedly:

- check free memory: `cat /proc/meminfo`
- check `/tmp/subtitle-probe/runner.log`
- reduce the crop/scale or switch to Tesseract-only (`fallback: false` with
  Tesseract-first).

### OCR text contains `==>`, `|`, trailing junk

`CleanOcr()` removes the common game glyphs and trailing noise. If a specific
game still leaks text, extend the cleanup regexes in `runner/Program.cs`.

## Translation

### `local_IOException` / local server unreachable

Start the server on the Mac and check the TV can reach it:

```bash
curl -s http://192.168.1.20:18881/health
```

Ensure the TV config has the right LAN IP (`translate.endpoint`).

### `translation_http_429` / `456`

DeepL rate limit or quota. Switch to the local endpoint; the free DeepL tier has
a monthly character quota.

### `deepl_key_missing`

No key and no local endpoint. Write the key on the TV:

```bash
umask 077
printf '%s' 'YOUR-KEY' > /opt/usr/subtitle-ocr/deepl-key
chmod 600 /opt/usr/subtitle-ocr/deepl-key
```

### Translations arrive too late

The chain is capture + OCR + translation. Measured: Argos ~0.2–0.3 s, NLLB
~0.3–0.5 s, DeepL ~0.6–1.2 s. Neural OCR adds 500–700 ms. “Zero” latency is not
possible for arbitrary on-screen text; only pre-translated/cached lines can be
instant.

## Helper / app

### The app says “Service unavailable”

The helper is not running or Vetro is down:

```bash
curl -s http://192.168.1.10:8099/status | jq .agent
systemctl status subtitle-helper
```

Open the app; it sends the `systemd-run` command through Vetro `/exec`.

### The helper starts, but the app cannot reach it

- Helper binds `127.0.0.1:18765`; it is not reachable from the LAN by design.
- The WGT must run on the TV; test in the TV browser? No — use the app.

### The app is missing from the TV

- The WGT was not signed with a certificate covering the TV's DUID.
- `apps2samsung-cli tv 192.168.1.10` → check DUID/ready.
- Reinstall with `apps2samsung-cli install dist/SubtitleTV.wgt --tv ... -y`.

### Black band with no text

The overlay is showing an empty scene. The runner writes an empty scene when no
text is found (or after the visible timeout); the band should hide. If it stays:

- check `live-scene.json` (empty vs. text),
- check the overlay process (`ps -ef | grep OverlayProbe`),
- restart the runner (`POST /stop`, `POST /start`).

## WGT / signing

### `install failed[118, -12]`, certificate error

The package is not signed with a valid distributor certificate for this TV.
Use Apps2Samsung (it signs with the local Samsung profile) or Tizen Studio with a
profile containing the TV's DUID.

### Developer mode host IP

- Apps2Samsung/Tizen Studio install needs **Host PC IP = the Mac's IP**.
- Vetro's root agent needs **Host PC IP = 127.0.0.1**.
- Switch + reboot between the two operations when needed.

## Logs

```bash
# TV
cat /tmp/subtitle-probe/runner.log
cat /tmp/subtitle-helper.log
curl -s http://127.0.0.1:18765/state | jq .

# Vetro
curl -s http://TV_IP:8099/status | jq .
```

`runner.log` contains `r=... ysz=...` capture lines, `translated_ms=...` and
`translation_error=...`; the state file has the last OCR/translation and the
engine that produced them.
