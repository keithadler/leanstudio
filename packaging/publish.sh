#!/usr/bin/env bash
# Build self-contained Lean Studio for one platform into artifacts/.
#   packaging/publish.sh osx-arm64 | osx-x64 | linux-x64 | linux-arm64 | win-x64
# On macOS targets this also assembles "Lean Studio.app". Needs the .NET 10 SDK and the tenet submodule.
set -euo pipefail
rid="${1:?usage: publish.sh <runtime id>}"
root="$(cd "$(dirname "$0")/.." && pwd)"
version="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$root/Directory.Build.props")"
out="$root/artifacts/$rid"
rm -rf "$out"
dotnet publish "$root/src/LeanStudio.App/LeanStudio.App.csproj" -c Release -r "$rid" --self-contained \
  -p:PublishSingleFile=false -p:DebugType=none -o "$out/LeanStudio"

case "$rid" in
  osx-*)
    app="$out/Lean Studio.app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cp -R "$out/LeanStudio/." "$app/Contents/MacOS/"
    sed "s/__VERSION__/$version/g" "$root/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
    cp "$root/packaging/icons/leanstudio.icns" "$app/Contents/Resources/"
    rm -rf "$out/LeanStudio"
    # Ad-hoc signature so Apple silicon will run it; distribution needs a Developer ID and notarization.
    codesign --force --deep --sign - "$app" 2>/dev/null || true
    (cd "$out" && ditto -c -k --keepParent "Lean Studio.app" "$root/artifacts/LeanStudio-$version-$rid.zip")
    ;;
  win-*)
    # Windows runners have 7-Zip but not zip.
    if command -v zip >/dev/null; then
      (cd "$out" && zip -qr "$root/artifacts/LeanStudio-$version-$rid.zip" LeanStudio)
    else
      (cd "$out" && 7z a -tzip -bso0 "../LeanStudio-$version-$rid.zip" LeanStudio)
    fi
    ;;
  *)
    cp "$root/packaging/icons/leanstudio.png" "$out/LeanStudio/"
    cp "$root/packaging/linux/leanstudio.desktop" "$out/LeanStudio/"
    tar -C "$out" -czf "$root/artifacts/LeanStudio-$version-$rid.tar.gz" LeanStudio
    ;;
esac
echo "built artifacts/ for $rid"
