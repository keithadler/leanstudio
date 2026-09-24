#!/usr/bin/env bash
# Sign "Lean Studio.app" for distribution: a Developer ID Application identity, a secure timestamp and the
# hardened runtime, as notarization requires. publish.sh runs this when MACOS_SIGN_IDENTITY is set.
#   MACOS_SIGN_IDENTITY  the identity's name ("Developer ID Application: …") or SHA-1 hash, from
#                        `security find-identity -v -p codesigning`
#   MACOS_KEYCHAIN       optional: the keychain holding it (CI imports the certificate into its own)
set -euo pipefail
app="${1:?usage: sign.sh <path to Lean Studio.app>}"
identity="${MACOS_SIGN_IDENTITY:?set MACOS_SIGN_IDENTITY to a Developer ID Application identity}"
entitlements="$(cd "$(dirname "$0")" && pwd)/entitlements.plist"
args=(--force --timestamp --options runtime --sign "$identity")
if [ -n "${MACOS_KEYCHAIN:-}" ]; then
  args+=(--keychain "$MACOS_KEYCHAIN")
fi

# Inside out: every file in Contents/MacOS (the native libraries, and the .NET assemblies, which codesign treats
# as code there), then the bundle, which signs the main executable with the entitlements .NET needs.
main="$app/Contents/MacOS/LeanStudio"
find "$app/Contents/MacOS" -type f ! -path "$main" -print0 | while IFS= read -r -d '' f; do
  codesign "${args[@]}" "$f"
done
codesign "${args[@]}" --entitlements "$entitlements" "$app"

codesign --verify --strict --deep --verbose=2 "$app"
echo "signed $app"
