#!/usr/bin/env bash
# Build both Thunderstore packages. Run from the repo root:
#   bash packaging/build-packages.sh
#
# Produces packaging/dist/*.zip. Thunderstore wants icon.png (256x256),
# README.md, CHANGELOG.md and manifest.json at the zip root, with plugin DLLs
# under plugins/.
set -euo pipefail

DOTNET="${DOTNET:-dotnet}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIST="$ROOT/packaging/dist"

rm -rf "$DIST"
mkdir -p "$DIST"

"$DOTNET" build "$ROOT/src/ComboMod.Editor/ComboMod.Editor.csproj" -c Release

pack() {
  local name="$1" src="$2" dll="$3"
  local stage="$DIST/$name"

  mkdir -p "$stage/plugins/ComboMod"
  cp "$ROOT/packaging/$src/manifest.json" "$stage/"
  cp "$ROOT/README.md" "$ROOT/CHANGELOG.md" "$ROOT/LICENSE" "$stage/"
  [ -f "$ROOT/packaging/$src/icon.png" ] && cp "$ROOT/packaging/$src/icon.png" "$stage/"
  cp "$dll" "$stage/plugins/ComboMod/"

  ( cd "$stage" && zip -qr "$DIST/$name.zip" . )
  echo "built $DIST/$name.zip"
}

pack "ComboMod"        "ComboMod"        "$ROOT/src/ComboMod.Core/bin/Release/ComboMod.Core.dll"
pack "ComboMod-Editor" "ComboMod-Editor" "$ROOT/src/ComboMod.Editor/bin/Release/ComboMod.Editor.dll"

echo
echo "Note: SampleTweaks is a demo and is deliberately not packaged."
echo "Add a 256x256 icon.png under packaging/<name>/ before uploading."
