#!/usr/bin/env bash
# Notarize a signed macOS build and staple the ticket to the app, so Gatekeeper opens it without a warning,
# even offline. Runs between the build and archive phases of publish.sh:
#   MACOS_SIGN_IDENTITY=… packaging/publish.sh osx-arm64 build
#   packaging/macos/notarize.sh osx-arm64
#   packaging/publish.sh osx-arm64 archive
# Credentials, one of (see docs/packaging/signing.md):
#   App Store Connect API key: APPLE_API_KEY (the .p8 file's contents), APPLE_API_KEY_ID, APPLE_API_ISSUER
#   Apple ID:                  APPLE_ID, APPLE_APP_SPECIFIC_PASSWORD, APPLE_TEAM_ID
set -euo pipefail
rid="${1:?usage: notarize.sh <osx-arm64|osx-x64>}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
app="$root/artifacts/$rid/Lean Studio.app"
[ -d "$app" ] || { echo "no app at $app: run packaging/publish.sh $rid build first" >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
if [ -n "${APPLE_API_KEY:-}" ]; then
  printf '%s' "$APPLE_API_KEY" > "$work/key.p8"
  auth=(--key "$work/key.p8" --key-id "${APPLE_API_KEY_ID:?}" --issuer "${APPLE_API_ISSUER:?}")
elif [ -n "${APPLE_ID:-}" ]; then
  auth=(--apple-id "$APPLE_ID" --password "${APPLE_APP_SPECIFIC_PASSWORD:?}" --team-id "${APPLE_TEAM_ID:?}")
else
  echo "no notarization credentials: set APPLE_API_KEY (with APPLE_API_KEY_ID, APPLE_API_ISSUER) or APPLE_ID" >&2
  exit 1
fi

# Submit a zip of the app (keeping the extended attributes that hold its nested signatures) and wait for Apple.
zip="$work/submit.zip"
ditto -c -k --sequesterRsrc --keepParent "$app" "$zip"
xcrun notarytool submit "$zip" "${auth[@]}" --wait --timeout 30m --output-format json | tee "$work/result.json"
echo
status="$(plutil -extract status raw -o - "$work/result.json")"
if [ "$status" != "Accepted" ]; then
  id="$(plutil -extract id raw -o - "$work/result.json" || true)"
  echo "notarization ended with status $status" >&2
  [ -n "$id" ] && xcrun notarytool log "$id" "${auth[@]}" >&2 || true
  exit 1
fi

xcrun stapler staple "$app"
xcrun stapler validate "$app"
spctl --assess --type execute --verbose=2 "$app"
echo "notarized and stapled $app"
