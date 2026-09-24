# Publishing to winget

Once the manifest is accepted into the Windows Package Manager's community repository, Lean Studio installs with:

```powershell
winget install KeithAdler.LeanStudio
```

The manifests live in [`packaging/winget/`](../../packaging/winget), one set per version, in the multi-file format
(manifest schema 1.9.0):

| File | What it holds |
|---|---|
| `KeithAdler.LeanStudio.yaml` | The version manifest: identifier, version, default locale. |
| `KeithAdler.LeanStudio.installer.yaml` | The `win-x64` and `win-arm64` release zips, with their SHA-256 checksums. |
| `KeithAdler.LeanStudio.locale.en-US.yaml` | Name, description, publisher and author (Keith Adler, @keithadler), MIT license, links. |

## How it installs

Each release zip holds a `LeanStudio` folder with `LeanStudio.exe` and the libraries it loads. The manifest
declares the zip as an archive holding a **portable** app, and winget extracts it into its packages folder.
`ArchiveBinariesDependOnPath: true` adds that folder to the PATH instead of linking to the exe alone, so the
libraries next to it are always found. After installing:

- `leanstudio` starts the app from any terminal. `leanstudio path\to\project` opens a project, and
  `leanstudio --mcp` runs the MCP server for AI assistants.
- `winget upgrade KeithAdler.LeanStudio` updates it, and `winget uninstall KeithAdler.LeanStudio` removes it.
  Settings in `%APPDATA%\LeanStudio` and work in `Documents\Lean Studio` are kept.

winget doesn't create a Start menu entry for portable apps. People who want one can pin `LeanStudio.exe`, or use the
zip from the release page.

## Updating the manifests for a release

After CI has published the GitHub release for tag `vX.Y.Z`:

```bash
packaging/update-manifests.py X.Y.Z
```

This sets the version, download URLs, SHA-256 checksums (from the digests GitHub records for each asset),
release date and release notes link in all three files. It updates the Homebrew cask too. Commit the result.

## Submitting to winget-pkgs

Submissions go to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) as a pull request. Each
version goes in its own folder, `manifests/k/KeithAdler/LeanStudio/<version>/`. You need a GitHub account. The
first submission is reviewed by a moderator; updates to an existing package usually go through faster once
validation passes.

On Windows, validate and test locally first:

```powershell
winget validate --manifest packaging\winget
winget settings --enable LocalManifestFiles    # once, as administrator
winget install --manifest packaging\winget
leanstudio                                     # the app opens
winget uninstall KeithAdler.LeanStudio
```

Then submit, with either of:

- **wingetcreate** (simplest):

  ```powershell
  winget install Microsoft.WingetCreate
  wingetcreate submit --token <a GitHub token with public_repo scope> packaging\winget
  ```

  For later versions, `wingetcreate update KeithAdler.LeanStudio --version X.Y.Z --urls <x64 zip url> <arm64 zip url> --submit`
  produces the same result from the published manifest.

- **By hand:** fork `microsoft/winget-pkgs`, copy the three files to
  `manifests/k/KeithAdler/LeanStudio/X.Y.Z/`, and open a pull request. The bot runs validation, and installs the
  package in a sandbox.

Never commit the GitHub token. Pass it on the command line or through `WINGET_CREATE_GITHUB_TOKEN`.

## Signing

winget checks every download against the manifest's SHA-256. Authenticode signing (see [signing.md](signing.md))
adds a verifiable publisher, Keith Adler, to `LeanStudio.exe`. It also lets SmartScreen build a reputation for the
app, so people who download the zip in a browser see fewer warnings.
