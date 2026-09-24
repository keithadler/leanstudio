#!/usr/bin/env bash
# CI only: put the Developer ID certificate into a temporary keychain, and tell later steps which identity and
# keychain to sign with (MACOS_SIGN_IDENTITY, MACOS_KEYCHAIN in $GITHUB_ENV).
#   MACOS_CERTIFICATE           the Developer ID Application certificate and private key, as a base64 .p12
#   MACOS_CERTIFICATE_PASSWORD  the .p12's password
set -euo pipefail
: "${MACOS_CERTIFICATE:?}" "${MACOS_CERTIFICATE_PASSWORD:?}" "${GITHUB_ENV:?this script is for GitHub Actions}"
keychain="${RUNNER_TEMP:?}/signing.keychain-db"
password="$(openssl rand -hex 24)"   # the temporary keychain's own password, never stored
p12="$RUNNER_TEMP/certificate.p12"
trap 'rm -f "$p12"' EXIT

printf '%s' "$MACOS_CERTIFICATE" | base64 --decode > "$p12"
security create-keychain -p "$password" "$keychain"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$password" "$keychain"
security import "$p12" -k "$keychain" -P "$MACOS_CERTIFICATE_PASSWORD" -T /usr/bin/codesign
security set-key-partition-list -S apple-tool:,apple: -k "$password" "$keychain" >/dev/null
# Keep the existing keychains searchable too, and add this one.
keychains=("$keychain")
while IFS= read -r k; do
  keychains+=("$(printf '%s' "$k" | sed -e 's/^[[:space:]]*"//' -e 's/"[[:space:]]*$//')")
done < <(security list-keychains -d user)
security list-keychains -d user -s "${keychains[@]}"

identity="$(security find-identity -v -p codesigning "$keychain" | awk '/Developer ID Application/ && !found { print $2; found = 1 }')"
[ -n "$identity" ] || { echo "no Developer ID Application identity in the certificate" >&2; exit 1; }
echo "MACOS_SIGN_IDENTITY=$identity" >> "$GITHUB_ENV"
echo "MACOS_KEYCHAIN=$keychain" >> "$GITHUB_ENV"
echo "imported identity $identity"
