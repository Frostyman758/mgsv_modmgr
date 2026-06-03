#!/usr/bin/env bash
# Build a standalone, single-file modmgr_gui binary for macOS at the repo root.
#
# Usage:
#   ./publish-mac.sh             # defaults to osx-arm64 (Apple Silicon)
#   ./publish-mac.sh osx-arm64
#   ./publish-mac.sh osx-x64     # Intel Macs
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
GUI="$ROOT/csgui/MgsvModMgr.Gui"
OUT="$ROOT/publish_tmp"
RID="${1:-osx-arm64}"

echo "Publishing single-file self-contained binary for $RID ..."
dotnet publish "$GUI" -c Release -r "$RID" \
    -p:PublishSingleFile=true \
    -o "$OUT"

SRC="$OUT/modmgr_gui"
DST="$ROOT/modmgr_gui"
cp "$SRC" "$DST"
chmod +x "$DST"
rm -rf "$OUT"

echo
echo "Done. Distributable binary: $DST"
echo "Note: macOS Gatekeeper will quarantine unsigned binaries on first run."
echo "      Users may need to run: xattr -dr com.apple.quarantine \"$DST\""
