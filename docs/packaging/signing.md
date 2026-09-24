# Signing and notarization

Lean Studio's releases build and publish without any signing credentials. The macOS apps get an ad-hoc signature,
which Apple silicon needs in order to run them, and the Windows builds are unsigned. People can install them, but
macOS asks them to approve the app in System Settings, and Windows SmartScreen shows a warning.

The release workflow can do better on its own, once the right secrets are in the repository:

| Platform | What it does | When |
|---|---|---|
| macOS | Signs `Lean Studio.app` with a **Developer ID** (hardened runtime, secure timestamp) | The two `MACOS_CERTIFICATE*` secrets are set |
| macOS | **Notarizes** it with Apple and staples the ticket, so it opens without a warning, even offline | It is signed, and one set of `APPLE_*` secrets is set |
| Windows | **Authenticode**-signs `LeanStudio.exe` and Lean Studio's own DLLs | A `.pfx` certificate, *or* Azure Artifact Signing, is set up |

Each part is skipped when its secrets are missing, so CI stays green without them. The **Check for signing
credentials** step of each `Package` job logs what will run. Secrets are passed only to the steps that use them,
never to the build.

Add secrets under **Settings ▸ Secrets and variables ▸ Actions ▸ New repository secret**, or with the GitHub CLI
(`gh secret set NAME < file`). Never commit a certificate, key or password to the repository.

## macOS: Developer ID signing

You need a paid [Apple Developer Program](https://developer.apple.com/programs/) membership, and a
**Developer ID Application** certificate. Create one in Xcode (Settings ▸ Accounts ▸ Manage Certificates ▸ + ▸
Developer ID Application), or at [developer.apple.com](https://developer.apple.com/account/resources/certificates).

Export the certificate *with its private key* from Keychain Access as a `.p12` file, protected by a password.

| Secret | Value |
|---|---|
| `MACOS_CERTIFICATE` | The `.p12`, base64-encoded: `base64 -i DeveloperID.p12 \| pbcopy` |
| `MACOS_CERTIFICATE_PASSWORD` | The password you chose when exporting it |

In CI, [`packaging/macos/import-certificate.sh`](../../packaging/macos/import-certificate.sh) imports the
certificate into a temporary keychain, which is deleted at the end of the job. Then
[`packaging/macos/sign.sh`](../../packaging/macos/sign.sh) signs every file in `Contents/MacOS`, and then the
bundle, with the hardened runtime and the entitlements in
[`packaging/macos/entitlements.plist`](../../packaging/macos/entitlements.plist). Those entitlements allow the JIT
and loading the runtime's own native libraries, which .NET needs.

## macOS: notarization

Notarization needs a signed app, and one of these sets of credentials. The API key is the better choice for CI,
because it isn't tied to a person's Apple ID and two-factor sign-in.

**An App Store Connect API key (recommended).** In [App Store Connect](https://appstoreconnect.apple.com) ▸ Users
and Access ▸ Integrations ▸ Team Keys, create a key with the *Developer* role, and download the `.p8` file (you can
only download it once).

| Secret | Value |
|---|---|
| `APPLE_API_KEY` | The contents of `AuthKey_<id>.p8`, including the `BEGIN`/`END` lines |
| `APPLE_API_KEY_ID` | The key's ID (10 characters) |
| `APPLE_API_ISSUER` | The Issuer ID shown above the list of keys (a UUID) |

**Or an Apple ID.** Create an app-specific password at [account.apple.com](https://account.apple.com) ▸ Sign-In
and Security ▸ App-Specific Passwords.

| Secret | Value |
|---|---|
| `APPLE_ID` | The Apple ID email of a member of the developer team |
| `APPLE_APP_SPECIFIC_PASSWORD` | The app-specific password |
| `APPLE_TEAM_ID` | The team's ID (10 characters, in the Membership details on developer.apple.com) |

[`packaging/macos/notarize.sh`](../../packaging/macos/notarize.sh) submits the app with `notarytool`, waits for
Apple's verdict (and prints Apple's log if it isn't *Accepted*), staples the ticket to the app, and checks it with
`spctl`. The archive step then zips the stapled app, keeping the extended attributes that hold the signatures of
the files inside it.

Once notarized releases ship, remove the Gatekeeper paragraph from the Homebrew cask's caveats, and the note in the
README's Install section.

### Signing a build on your own Mac

With the certificate in your login keychain:

```bash
security find-identity -v -p codesigning        # note the "Developer ID Application: …" identity
export MACOS_SIGN_IDENTITY="Developer ID Application: Keith Adler (TEAMID)"
packaging/publish.sh osx-arm64 build           # signs the app
APPLE_ID=… APPLE_APP_SPECIFIC_PASSWORD=… APPLE_TEAM_ID=… packaging/macos/notarize.sh osx-arm64
packaging/publish.sh osx-arm64 archive
```

## Windows: Authenticode signing

Since June 2023, publicly trusted code-signing keys have to live in hardware: a USB token, a cloud HSM, or a
signing service. Set up **one** of the two options below. If both are, the certificate is used.

### Option A: a certificate as a `.pfx`

For a certificate whose key you can export (from an HSM service that offers one, or an older certificate):

| Secret | Value |
|---|---|
| `WINDOWS_CERTIFICATE` | The `.pfx`, base64-encoded: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))` in PowerShell, or `base64 -w0 cert.pfx` |
| `WINDOWS_CERTIFICATE_PASSWORD` | The `.pfx` password |

[`packaging/windows/sign.ps1`](../../packaging/windows/sign.ps1) signs with `signtool`, SHA-256, and a DigiCert
RFC 3161 timestamp (set `WINDOWS_TIMESTAMP_URL` to use another). It writes the `.pfx` to a temporary file only for
the signing, and verifies the result afterwards.

### Option B: Azure Artifact Signing

[Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/) (formerly Trusted Signing) keeps the
key in Microsoft's HSMs, and costs a few dollars a month. Create a signing account and a *Public Trust* certificate
profile (after identity validation). Then create an app registration (service principal) with a client secret, and
give it the certificate profile signer role on the account (*Artifact Signing Certificate Profile Signer*, called
*Trusted Signing Certificate Profile Signer* before the rename).

| Secret | Value |
|---|---|
| `AZURE_TENANT_ID` | The Microsoft Entra tenant (directory) ID |
| `AZURE_CLIENT_ID` | The app registration's client (application) ID |
| `AZURE_CLIENT_SECRET` | Its client secret |
| `ARTIFACT_SIGNING_ENDPOINT` | The account's region endpoint, for example `https://eus.codesigning.azure.net/` |
| `ARTIFACT_SIGNING_ACCOUNT` | The signing account's name |
| `ARTIFACT_SIGNING_PROFILE` | The certificate profile's name |

The workflow uses Microsoft's
[artifact-signing-action](https://github.com/Azure/artifact-signing-action), pinned to a commit, to sign the files
that `packaging/windows/sign.ps1 -ListOnly` names.

### What gets signed

`LeanStudio.exe`, `LeanStudio.dll`, `LeanStudio.Core.dll`, `LeanStudio.Lsp.dll`, `LeanStudio.Mcp.dll`,
`Tenet.Kernel.dll` and `Tenet.Olean.dll`: Lean Studio's own code. The .NET runtime's files are already signed by
Microsoft. Other libraries keep whatever signature their authors gave them.

## Trying it before a release

The `Package` jobs run for `v*` tags, and also when the workflow is started by hand (**Actions ▸ CI ▸ Run
workflow**). A manual run builds, signs and notarizes the packages, and uploads them as workflow artifacts without
creating a release, so the signing setup can be checked before tagging. To check a downloaded build:

```bash
codesign --verify --strict --deep --verbose=2 "Lean Studio.app"
spctl --assess --type execute --verbose=2 "Lean Studio.app"     # "source=Notarized Developer ID"
xcrun stapler validate "Lean Studio.app"
```

```powershell
Get-AuthenticodeSignature .\LeanStudio\LeanStudio.exe | Format-List Status, SignerCertificate
```
