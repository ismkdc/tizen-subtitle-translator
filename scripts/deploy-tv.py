#!/usr/bin/env python3
"""Deploy the .NET components to a rooted Samsung TV through Vetro.

Usage:
    TV_IP=192.168.1.10 MAC_IP=192.168.1.20 python3 scripts/deploy-tv.py

The script expects the built DLLs in the standard bin/Release/netcoreapp3.1
directories and an HTTP server serving the repository root on MAC_IP:8000:

    python3 -m http.server 8000 --bind 192.168.1.20
"""

import json
import os
import shlex
import sys
import time
import urllib.request

TV_IP = os.environ.get("TV_IP", "")
MAC_IP = os.environ.get("MAC_IP", "")
PORT = os.environ.get("HTTP_PORT", "8000")
VETRO = f"http://{TV_IP}:8099/exec"

OCR_DIR = "/opt/usr/subtitle-ocr"
HELPER_DIR = "/opt/usr/subtitle-helper"

RUNNER = "runner/bin/Release/netcoreapp3.1"
OVERLAY = "overlay/bin/Release/netcoreapp3.1"
HELPER = "helper-dotnet/bin/Release/netcoreapp3.1"

FILES = [
    (f"{RUNNER}/SubtitleRunner.dll", f"{OCR_DIR}/SubtitleRunner.dll"),
    (f"{RUNNER}/SubtitleRunner.runtimeconfig.json", f"{OCR_DIR}/SubtitleRunner.runtimeconfig.json"),
    (f"{OVERLAY}/OverlayProbe.dll", f"{OCR_DIR}/OverlayProbe.dll"),
    (f"{OVERLAY}/OverlayProbe.runtimeconfig.json", f"{OCR_DIR}/OverlayProbe.runtimeconfig.json"),
    (f"{HELPER}/SubtitleHelper.dll", f"{HELPER_DIR}/SubtitleHelper.dll"),
    (f"{HELPER}/SubtitleHelper.runtimeconfig.json", f"{HELPER_DIR}/SubtitleHelper.runtimeconfig.json"),
]


def exec_root(cmd: str) -> dict:
    body = json.dumps({"cmd": cmd}).encode()
    req = urllib.request.Request(
        VETRO, data=body, headers={"Content-Type": "application/json"}, method="POST"
    )
    with urllib.request.urlopen(req, timeout=30) as res:
        raw = res.read().decode("utf-8", "replace")
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return {"raw": raw}


def main() -> int:
    if not TV_IP or not MAC_IP:
        print("Set TV_IP and MAC_IP (see the docstring).", file=sys.stderr)
        return 2

    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    missing = [src for src, _ in FILES if not os.path.isfile(os.path.join(root, src))]
    if missing:
        print("Missing build output; run dotnet build first:", file=sys.stderr)
        for m in missing:
            print("  " + m, file=sys.stderr)
        return 1

    base = f"http://{MAC_IP}:{PORT}"
    steps = [
        f"mkdir -p {OCR_DIR} {HELPER_DIR} /tmp/subtitle-probe",
    ]
    for src, dst in FILES:
        steps.append(f"curl -fsSL -o {shlex.quote(dst)} {shlex.quote(base + '/' + src)}")

    steps.append(
        f"[ -f {OCR_DIR}/config.json ] || "
        f"curl -fsSL -o {OCR_DIR}/config.json {shlex.quote(base + '/config.example.json')}"
    )
    steps.append("systemctl stop subtitle-helper 2>/dev/null || true")
    steps.append("systemctl reset-failed subtitle-helper 2>/dev/null || true")
    steps.append(
        "systemd-run --unit=subtitle-helper --collect "
        f"/usr/bin/dotnet {HELPER_DIR}/SubtitleHelper.dll"
    )
    steps.append("sleep 1; curl -s http://127.0.0.1:18765/health || true")

    for step in steps:
        print("$ " + step)
        result = exec_root(step)
        if result:
            print(json.dumps(result)[:400])
        time.sleep(0.2)

    print()
    print("Deployed. Open the TV app or POST http://127.0.0.1:18765/start from the TV.")
    print("OCR binaries and models are not deployed by this script; see docs/OCR.md.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
