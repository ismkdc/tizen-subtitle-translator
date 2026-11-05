#!/usr/bin/env bash
# Create a local translation environment and install the Argos en->tr package.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VENV="$ROOT/translate-env"

python3 -m venv "$VENV"
"$VENV/bin/pip" install --upgrade pip wheel
"$VENV/bin/pip" install argostranslate

"$VENV/bin/python" - <<'PY'
import argostranslate.package as pkg

pkg.update_package_index()
available = pkg.get_available_packages()
match = [p for p in available if p.from_code == "en" and p.to_code == "tr"]
if not match:
    raise SystemExit("No en->tr Argos package found")
pkg.install_from_path(match[0].download())
print("Installed Argos en->tr")
PY

echo
echo "Argos server: $VENV/bin/python $ROOT/servers/translate-server.py   # :18880"
echo "For NLLB, see docs/TRANSLATION.md (requires a one-time CT2 conversion)."
