#!/usr/bin/env bash
# Build a standalone, single-file modmgr_gui binary at the repo root.
# No .NET runtime required on the target machine.
#
# Usage:
#   ./publish.sh                # defaults to linux-x64
#   ./publish.sh linux-x64
#   ./publish.sh linux-arm64
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
GUI="$ROOT/csgui/MgsvModMgr.Gui"
OUT="$ROOT/publish_tmp"
RID="${1:-linux-x64}"

echo "Publishing single-file self-contained binary for $RID ..."
dotnet publish "$GUI" -c Release -r "$RID" \
    -p:PublishSingleFile=true \
    -o "$OUT"

# dotnet publish drops the binary without an extension on Linux.
SRC="$OUT/modmgr_gui"
DST="$ROOT/modmgr_gui"
cp "$SRC" "$DST"
chmod +x "$DST"
rm -rf "$OUT"

echo
echo "Done. Distributable binary: $DST"
