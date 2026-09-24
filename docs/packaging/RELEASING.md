# Releasing Lean Studio

A release is a `vX.Y.Z` tag on `main`. CI does the rest: it tests, builds every platform, signs what it has
credentials for, and publishes a GitHub release. After that, the Homebrew and winget manifests are pointed at it.

## 1. Prepare the release commit

On an up-to-date `main` whose CI is green:

1. **Pick the version.** Lean Studio uses `MAJOR.MINOR.PATCH`: a new minor for new features, a patch for fixes only.
2. **Bump it** in [`Directory.Build.props`](../../Directory.Build.props):

   ```xml
   <VersionPrefix>X.Y.Z</VersionPrefix>
   ```

   Everything reads the version from here: the assemblies, the About box and update checker, the macOS
   `Info.plist`, the archive and AppImage names, and the AppStream metadata.
3. **Update [`CHANGELOG.md`](../../CHANGELOG.md).** Rename `## Unreleased` to `## X.Y.Z`, and check that it lists
   everything user-visible since the last release: features, fixes, and new or changed MCP tools.
4. **Update the README's Status section** if it names the version ("Lean Studio is at **X.Y**").
5. **Check locally:**

   ```bash
   dotnet build LeanStudio.slnx -c Release
   dotnet test --project tests/LeanStudio.Tests -c Release --no-build
   ```

6. **Commit** as `Lean Studio X.Y.Z` and push to `main`. Wait for CI to pass on that commit.

## 2. Tag it

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
```

The tag must be `v` followed by exactly the `VersionPrefix`. The update checker compares the tag with the running
version, and the archive names come from `VersionPrefix`.

To try the packaging first (after changing signing, say), start the workflow by hand from **Actions ▸ CI ▸ Run
workflow**. It builds and signs the packages and uploads them as workflow artifacts, without creating a release.

## 3. What CI produces

For a `v*` tag, [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) runs:

1. **Test**, on Ubuntu, macOS and Windows: the build, the unit and Lean integration tests, and the headless UI run
   (on Ubuntu and macOS).
2. **Package**, once per runtime, with [`packaging/publish.sh`](../../packaging/publish.sh):
   - builds a self-contained app;
   - on macOS, signs it with a Developer ID and notarizes it, when those secrets are set (otherwise an ad-hoc
     signature);
   - on Windows, Authenticode-signs it, when those secrets are set;
   - archives it;
   - on Linux, also builds the AppImage.

   See [signing.md](signing.md) for the secrets.
3. **Release**: a GitHub release named `Lean Studio X.Y.Z`, with notes generated from the commits and these assets:

| Asset | Platform | Used by |
|---|---|---|
| `LeanStudio-X.Y.Z-osx-arm64.zip` | macOS, Apple silicon | Direct download, Homebrew cask, update checker |
| `LeanStudio-X.Y.Z-osx-x64.zip` | macOS, Intel | Direct download, Homebrew cask, update checker |
| `LeanStudio-X.Y.Z-win-x64.zip` | Windows x64 | Direct download, winget, update checker |
| `LeanStudio-X.Y.Z-win-arm64.zip` | Windows ARM64 | Direct download, winget, update checker |
| `LeanStudio-X.Y.Z-linux-x64.tar.gz` | Linux x64 | Direct download, update checker |
| `LeanStudio-X.Y.Z-linux-arm64.tar.gz` | Linux ARM64 | Direct download, update checker |
| `LeanStudio-X.Y.Z-x86_64.AppImage` | Linux x64 | Direct download ([appimage.md](appimage.md)) |
| `LeanStudio-X.Y.Z-aarch64.AppImage` | Linux ARM64 | Direct download |

Keep these names: the Homebrew cask, the winget manifests and the in-app update checker all depend on them. The
update checker offers each running app the asset whose name contains its runtime (`-osx-arm64.` and so on).

When the release is up, open its page and check that all eight assets are there. You can then edit the generated
notes to link to the changelog section.

## 4. Update the package managers

Once the release is published:

```bash
git checkout -b release-manifests-X.Y.Z
packaging/update-manifests.py X.Y.Z
git commit -am "Package manifests for X.Y.Z"
```

This rewrites the Homebrew cask and the three winget manifests with the new version, URLs, SHA-256 checksums
(from the digests GitHub records for each asset), release date and release notes link. Open a pull request with the
change, then publish it:

- **Homebrew:** copy `packaging/homebrew/lean-studio.rb` to `Casks/lean-studio.rb` in
  [keithadler/homebrew-tap](https://github.com/keithadler/homebrew-tap), commit and push.
  [homebrew.md](homebrew.md) has the details and checks.
- **winget:** submit `packaging/winget/` to `manifests/k/KeithAdler/LeanStudio/X.Y.Z/` in
  [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), with `wingetcreate submit` or a pull request.
  [winget.md](winget.md) has the details.

## 5. Check the installs

On machines (or VMs) that don't have a development copy:

- macOS: `brew upgrade --cask lean-studio` (or `brew install --cask keithadler/tap/lean-studio`), then open the app.
- Windows: `winget upgrade KeithAdler.LeanStudio`, once the winget pull request is merged.
- Linux: download the AppImage, `chmod +x` it, and run it.
- An older Lean Studio: **Help ▸ Check for Updates…** offers the new version.

## If something goes wrong

- **A package job fails:** fix it on `main`, and release the fix as the next patch version. Don't move a tag that
  has been pushed: people and package managers may already have the first build.
- **Notarization is rejected:** the job prints Apple's log. The usual causes are a file in `Contents/MacOS` that
  was left unsigned, or a missing entitlement. [signing.md](signing.md) describes the signing steps.
- **A release went out broken:** mark it as a pre-release on GitHub so the update checker skips it, then ship a
  patch release.
