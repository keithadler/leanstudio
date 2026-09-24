#!/usr/bin/env bash
# Package a Linux build as an AppImage: one file that runs on most distributions, no install needed.
#   packaging/publish.sh linux-x64 && packaging/linux/build-appimage.sh linux-x64
# Reads artifacts/<rid>/LeanStudio (from publish.sh) and writes artifacts/LeanStudio-<version>-<arch>.AppImage,
# where <arch> is x86_64 or aarch64, as AppImages are usually named. Both can be built on an x86_64 machine.
# Downloads a pinned appimagetool and runtime from github.com/AppImage and checks their SHA-256 digests
# (APPIMAGETOOL can point at an appimagetool already on the machine instead).
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

# Pinned versions, checked against their published SHA-256 digests: a release must not run whatever a floating
# "continuous" download serves that day. To update, take the new release's digests from GitHub's release page.
appimagetool_version=1.9.1
runtime_version=20251108
declare -A digests=(
  [appimagetool-x86_64]=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
  [appimagetool-aarch64]=f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158
  [runtime-x86_64]=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d
  [runtime-aarch64]=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444
)
tools="$root/artifacts/tools"
mkdir -p "$tools"
sha256() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
fetch() { # name url: download once, then insist on the pinned digest
  local file="$tools/$1"
  [ -f "$file" ] || curl -fsSL -o "$file" "$2"
  local got; got="$(sha256 "$file")"
  if [ "$got" != "${digests[$1]}" ]; then
    rm -f "$file"
    echo "checksum mismatch for $1: expected ${digests[$1]}, got $got" >&2
    exit 1
  fi
  echo "$file"
}

tool="${APPIMAGETOOL:-}"
if [ -z "$tool" ]; then
  host="$(uname -m)"
  tool="$(fetch "appimagetool-$host" "https://github.com/AppImage/appimagetool/releases/download/$appimagetool_version/appimagetool-$host.AppImage")"
  chmod +x "$tool"
fi
runtime="$(fetch "runtime-$arch" "https://github.com/AppImage/type2-runtime/releases/download/$runtime_version/runtime-$arch")"

out="$root/artifacts/LeanStudio-$version-$arch.AppImage"
rm -f "$out"
# Extract-and-run: CI runners, containers and many desktops have no FUSE to mount appimagetool itself.
APPIMAGE_EXTRACT_AND_RUN=1 ARCH="$arch" "$tool" --no-appstream --runtime-file "$runtime" "$appdir" "$out"
echo "built ${out#"$root/"}"
