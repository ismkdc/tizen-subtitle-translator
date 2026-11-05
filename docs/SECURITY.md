# Security

This project is a rooted-TV research/personal-use tool. It deliberately uses
privileges and APIs that are not meant to be exposed on a network.

## 1. Trust model

| Component | Bind | Auth |
|---|---|---|
| `SubtitleHelper` | `127.0.0.1:18765` | none |
| Vetro dashboard + `/exec` | `0.0.0.0:8099` | none |
| Argos translation server | `0.0.0.0:18880` | none |
| NLLB translation server | `0.0.0.0:18881` | none |

Vetro's `/exec` runs commands as **root**. Anyone on the LAN who can reach port
8099 can execute commands on the TV. The helper is localhost-only, but the WGT can
reach it, and so can any local process.

**Keep the TV on a trusted LAN. Never expose port 8099 to the internet.**

## 2. Secrets

- DeepL key: `/opt/usr/subtitle-ocr/deepl-key`, mode `600`, owned by root.
- Never commit the key, certificates, `private/`, or `.env` to the repository.
- The translation servers do not need credentials; they proxy the local models.

## 3. Capture whitelist bypass

The runner is started with:

```text
exec -a /usr/bin/tzcapturesample /usr/bin/dotnet .../SubtitleRunner.dll
```

This makes the process pass Samsung's capture process whitelist. It is a
device-local bypass. Use it only on hardware you own; do not package it into
software distributed to third parties without understanding the legal and
security implications.

## 4. Root and system integrity

- Root via Vetro changes the TV's security posture.
- Firmware updates can remove root, change the capture whitelist, or break the
  overlay.
- The helper is a transient systemd unit; it does not survive a reboot until the
  app starts it.
- Do not disable the TV's update/security features blindly.

## 5. Data and privacy

- Screen frames stay on the TV (and `/tmp`); only the OCR **text** is sent to the
  translation backend.
- With DeepL, text leaves the LAN to `api-free.deepl.com`.
- With the local servers, text stays on the LAN.
- The app/helper do not phone home; Vetro and DeepL have their own policies.

## 6. Legal

- Use the tool on your own TV and your own lawful content.
- Game subtitles, broadcasts, and films are copyrighted; do not redistribute
  translations or captured frames.
- The NLLB-200 model is **CC-BY-NC 4.0** (non-commercial). Check the licence before
  any commercial use.
- The Samsung capture API and SamsungOne font are proprietary; they are not
  distributed in this repository.

## 7. Recommended setup

- Put the TV on a dedicated/trusted VLAN.
- Do not port-forward 8099/18880/18881.
- Stop the helper when not in use (`POST /stop`, `systemctl stop subtitle-helper`).
- Keep the DeepL key rotated if it may have leaked.
- Review Vetro's and Apps2Samsung's own security notes.
