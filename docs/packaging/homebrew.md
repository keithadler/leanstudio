# Publishing the Homebrew cask

Lean Studio is installed on macOS with:

```bash
brew install --cask keithadler/tap/lean-studio
```

That command reads `Casks/lean-studio.rb` from the tap repository
[keithadler/homebrew-tap](https://github.com/keithadler/homebrew-tap). The source of that file lives in this
repository at [`packaging/homebrew/lean-studio.rb`](../../packaging/homebrew/lean-studio.rb). Edit it here and copy
it to the tap, so the history of the cask stays with the code.

## What the cask does

- Downloads `LeanStudio-<version>-osx-arm64.zip` on Apple silicon, or `-osx-x64.zip` on Intel, from the GitHub
  release `v<version>`. It checks each file against its SHA-256 checksum.
- Installs `Lean Studio.app` into `/Applications`.
- Links `leanstudio` onto the PATH, so `leanstudio --mcp` works in AI assistants' MCP settings, and
  `leanstudio path/to/project` opens a project.
- On `brew uninstall --zap`, removes the settings in `~/.config/LeanStudio`. It never removes
  `~/Documents/Lean Studio`, which holds the person's tutorial and playground work.
- Requires macOS 12 (Monterey) or later, like the app's `Info.plist`.

## Setting up the tap (once)

1. Create a public GitHub repository named **`homebrew-tap`** under `keithadler`. Homebrew maps
   `keithadler/tap` to `github.com/keithadler/homebrew-tap`.
2. Add the cask:

   ```bash
   git clone https://github.com/keithadler/homebrew-tap
   mkdir -p homebrew-tap/Casks
   cp packaging/homebrew/lean-studio.rb homebrew-tap/Casks/
   cd homebrew-tap && git add Casks/lean-studio.rb && git commit -m "lean-studio 0.5.0" && git push
   ```

3. Check it on a Mac:

   ```bash
   brew tap keithadler/tap
   brew audit --cask --online keithadler/tap/lean-studio
   brew style keithadler/tap/lean-studio
   brew install --cask keithadler/tap/lean-studio
   ```

## Updating it for a release

After CI has published the GitHub release for tag `vX.Y.Z`:

```bash
packaging/update-manifests.py X.Y.Z
```

The script sets `version` and both `sha256` values from the checksums GitHub recorded for the release's assets.
Commit the change here, copy the file to `Casks/lean-studio.rb` in the tap, and push. `brew upgrade` picks it
up from there. The `livecheck` block also lets `brew livecheck lean-studio` report new releases.

## Gatekeeper

Until releases are signed with a Developer ID and notarized (see [signing.md](signing.md)), macOS blocks the first
launch of an app Homebrew downloaded. The cask's caveats tell people to allow it in **System Settings ▸ Privacy &
Security ▸ Open Anyway**. Once notarized builds ship, remove that paragraph from the caveats.

## The main Homebrew repository

The official `homebrew/cask` repository requires apps that pass Gatekeeper (signed and notarized) and meet its
notability rules. Until both are true, the tap is the way to go. After that, the same cask can be submitted there,
following Homebrew's [acceptable casks](https://docs.brew.sh/Acceptable-Casks) guidelines.
