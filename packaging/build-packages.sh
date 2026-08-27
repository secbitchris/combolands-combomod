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

"$DOTNET" build "$ROOT/src/ComboMod.Cheats/ComboMod.Cheats.csproj" -c Release   # builds all three

# Zip without assuming `zip` is installed -- it is absent from a stock Windows
# toolchain, which is where this is most likely to be run. Python is already a
# dependency here for tools/make-icons.py.
archive() {
  local src="$1" out="$2"

  if command -v zip >/dev/null 2>&1; then
    ( cd "$src" && zip -qr "$out" . )
    return
  fi

  local py
  py="$(command -v python3 || command -v py || true)"
  [ -n "$py" ] || { echo "need either zip or python to build archives" >&2; exit 1; }

  "$py" - "$src" "$out" <<'PYZIP'
import os, sys, zipfile
src, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(src):
        for f in files:
            full = os.path.join(root, f)
            # Thunderstore wants manifest.json and icon.png at the archive root.
            z.write(full, os.path.relpath(full, src).replace(os.sep, "/"))
PYZIP
}

pack() {
  local name="$1" src="$2" dll="$3"
  local stage="$DIST/$name"

  mkdir -p "$stage/plugins/ComboMod"
  cp "$ROOT/packaging/$src/manifest.json" "$stage/"
  cp "$ROOT/README.md" "$ROOT/CHANGELOG.md" "$ROOT/LICENSE" "$stage/"
  [ -f "$ROOT/packaging/$src/icon.png" ] && cp "$ROOT/packaging/$src/icon.png" "$stage/"
  cp "$dll" "$stage/plugins/ComboMod/"

  archive "$stage" "$DIST/$name.zip"
  echo "built $DIST/$name.zip"
}

pack "ComboMod"        "ComboMod"        "$ROOT/src/ComboMod.Core/bin/Release/ComboMod.Core.dll"
pack "ComboMod-Editor" "ComboMod-Editor" "$ROOT/src/ComboMod.Editor/bin/Release/ComboMod.Editor.dll"
pack "ComboMod-Cheats" "ComboMod-Cheats" "$ROOT/src/ComboMod.Cheats/bin/Release/ComboMod.Cheats.dll"

echo
# The installer bundle: everything a person needs, nothing they have to think about.
INSTALLER="$DIST/ComboMod-installer"
mkdir -p "$INSTALLER/plugins"
cp "$ROOT/install/Install-ComboMod.ps1" "$ROOT/install/Uninstall-ComboMod.ps1" "$INSTALLER/"
cp "$ROOT/install/Install ComboMod.bat" "$ROOT/install/Uninstall ComboMod.bat" "$INSTALLER/"
cp "$ROOT/README.md" "$ROOT/CHANGELOG.md" "$ROOT/LICENSE" "$INSTALLER/"
cp "$ROOT/src/ComboMod.Core/bin/Release/ComboMod.Core.dll" \
   "$ROOT/src/ComboMod.Editor/bin/Release/ComboMod.Editor.dll" \
   "$ROOT/src/ComboMod.Cheats/bin/Release/ComboMod.Cheats.dll" "$INSTALLER/plugins/"
archive "$INSTALLER" "$DIST/ComboMod-installer.zip"
rm -rf "$INSTALLER"
echo "built $DIST/ComboMod-installer.zip  (double-click install, no mod manager needed)"
echo
echo "Note: SampleTweaks is a demo and is deliberately not packaged."
echo "Icons come from tools/make-icons.py; rerun it after editing that script."
