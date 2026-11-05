#!/usr/bin/env bash
# Build the Tizen WGT package from app/ into dist/SubtitleTV.wgt.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/dist"
NAME="SubtitleTV.wgt"

mkdir -p "$OUT"
rm -f "$OUT/$NAME"

cd "$ROOT/app"
zip -r -q "$OUT/$NAME" . \
  -x '.*' -x '*/.*' -x '*.wgt' -x '*.zip'

echo "Built $OUT/$NAME"
