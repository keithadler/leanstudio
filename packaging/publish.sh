#!/usr/bin/env bash
# Build self-contained Lean Studio for one platform into artifacts/.
#   packaging/publish.sh <rid> [build|archive]
#   <rid>: osx-arm64 | osx-x64 | linux-x64 | linux-arm64 | win-x64 | win-arm64
# With no phase it does both: build (publish, and on macOS assemble and sign "Lean Studio.app"), then archive
# (artifacts/LeanStudio-<version>-<rid>.zip or .tar.gz). Running the phases separately leaves room to sign the
# Windows build, or notarize the macOS one, before it is archived. Needs the .NET 10 SDK and the tenet submodule.
#
# On macOS, MACOS_SIGN_IDENTITY (a Developer ID Application identity; see docs/packaging/signing.md) signs the app
# for distribution. Without it the app gets an ad-hoc signature, which Apple silicon needs to run it at all.
set -euo pipefail
rid="${1:?usage: publish.sh <runtime id> [build|archive]}"
phase="${2:-all}"
root="$(cd "$(dirname "$0")/.." && pwd)"
version="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$root/Directory.Build.props")"
out="$root/artifacts/$rid"
case "$phase" in
  all|build|archive) ;;
  *) echo "unknown phase: $phase (build or archive)" >&2; exit 2 ;;
esac

build() {
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
      if [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
        "$root/packaging/macos/sign.sh" "$app"
      else
        # Ad-hoc signature so Apple silicon will run it; distribution needs a Developer ID and notarization.
        codesign --force --deep --sign - "$app" 2>/dev/null || true
      fi
      ;;
    linux-*)
      cp "$root/packaging/icons/leanstudio.png" "$out/LeanStudio/"
      cp "$root/packaging/linux/leanstudio.desktop" "$out/LeanStudio/"
      ;;
  esac
}

archive() {
  case "$rid" in
    osx-*)
      local zip="$root/artifacts/LeanStudio-$version-$rid.zip"
      rm -f "$zip"
      local signature
      signature="$(codesign -dv "$out/Lean Studio.app" 2>&1 || true)"
      if [[ "$signature" == *"Authority=Developer ID"* ]]; then
        # Signed for distribution: the signatures of the non-Mach-O files in Contents/MacOS live in extended
        # attributes, so keep them in the zip (__MACOSX), or notarization and Gatekeeper find the seal broken.
        (cd "$out" && ditto -c -k --sequesterRsrc --keepParent "Lean Studio.app" "$zip")
      else
        (cd "$out" && ditto -c -k --keepParent "Lean Studio.app" "$zip")
      fi
      ;;
    win-*)
      local zip="$root/artifacts/LeanStudio-$version-$rid.zip"
      rm -f "$zip"
      # Windows runners have 7-Zip but not zip.
      if command -v zip >/dev/null; then
        (cd "$out" && zip -qr "$zip" LeanStudio)
      else
        (cd "$out" && 7z a -tzip -bso0 "../LeanStudio-$version-$rid.zip" LeanStudio)
      fi
      ;;
    *)
      tar -C "$out" -czf "$root/artifacts/LeanStudio-$version-$rid.tar.gz" LeanStudio
      ;;
  esac
}

if [ "$phase" != archive ]; then
  build
fi
if [ "$phase" != build ]; then
  [ -d "$out" ] || { echo "nothing built in $out: run the build phase first" >&2; exit 1; }
  archive
fi
echo "built artifacts/ for $rid ($phase)"
