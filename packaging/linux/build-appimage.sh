#!/usr/bin/env bash
# Package a Linux build as an AppImage: one file that runs on most distributions, no install needed.
#   packaging/publish.sh linux-x64 && packaging/linux/build-appimage.sh linux-x64
# Reads artifacts/<rid>/LeanStudio (from publish.sh) and writes artifacts/LeanStudio-<version>-<arch>.AppImage,
# where <arch> is x86_64 or aarch64, as AppImages are usually named. Both can be built on an x86_64 machine.
# Downloads appimagetool (and the runtime for the target architecture) from github.com/AppImage unless
# APPIMAGETOOL points at one already.
set -euo pipefail
rid="${1:?usage: build-appimage.sh linux-x64|linux-arm64}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
version="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$root/Directory.Build.props")"
case "$rid" in
  linux-x64) arch=x86_64 ;;
  linux-arm64) arch=aarch64 ;;
  *) echo "not a Linux runtime: $rid" >&2; exit 2 ;;
esac
build="$root/artifacts/$rid/LeanStudio"
[ -x "$build/LeanStudio" ] || { echo "no build in $build: run packaging/publish.sh $rid first" >&2; exit 1; }

appdir="$root/artifacts/$rid/LeanStudio.AppDir"
rm -rf "$appdir"
mkdir -p "$appdir/usr/lib" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/256x256/apps" \
         "$appdir/usr/share/metainfo"
cp -R "$build" "$appdir/usr/lib/leanstudio"

# The entry point: run the app from wherever the AppImage is mounted, passing arguments through
# (a project to open, or --mcp for AI assistants).
cat > "$appdir/AppRun" <<'EOF'
#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
exec "$here/usr/lib/leanstudio/LeanStudio" "$@"
EOF
chmod +x "$appdir/AppRun"

desktop="$root/packaging/linux/leanstudio.desktop"
icon="$root/packaging/icons/leanstudio-256.png"
cp "$desktop" "$appdir/leanstudio.desktop"
cp "$desktop" "$appdir/usr/share/applications/leanstudio.desktop"
cp "$icon" "$appdir/leanstudio.png"
cp "$icon" "$appdir/usr/share/icons/hicolor/256x256/apps/leanstudio.png"
ln -s leanstudio.png "$appdir/.DirIcon"
sed -e "s/__VERSION__/$version/g" -e "s/__DATE__/$(date -u +%Y-%m-%d)/g" "$root/packaging/linux/leanstudio.metainfo.xml" \
  > "$appdir/usr/share/metainfo/com.keithadler.leanstudio.metainfo.xml"

# Check the metadata offline when the validator is installed (appimagetool's own check needs the network).
if command -v appstreamcli >/dev/null; then
  appstreamcli validate --no-net "$appdir/usr/share/metainfo/com.keithadler.leanstudio.metainfo.xml"
fi

tool="${APPIMAGETOOL:-}"
if [ -z "$tool" ]; then
  host="$(uname -m)"
  tool="$root/artifacts/tools/appimagetool-$host.AppImage"
  if [ ! -x "$tool" ]; then
    mkdir -p "$(dirname "$tool")"
    curl -fsSL -o "$tool" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$host.AppImage"
    chmod +x "$tool"
  fi
fi

out="$root/artifacts/LeanStudio-$version-$arch.AppImage"
rm -f "$out"
# Extract-and-run: CI runners, containers and many desktops have no FUSE to mount appimagetool itself.
APPIMAGE_EXTRACT_AND_RUN=1 ARCH="$arch" "$tool" --no-appstream "$appdir" "$out"
echo "built ${out#"$root/"}"
