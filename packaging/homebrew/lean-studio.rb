# Homebrew cask for Lean Studio, a desktop IDE for Lean 4.
# Created by Keith Adler (@keithadler). MIT License.
#
# This file is the source of truth; it is copied to keithadler/homebrew-tap (Casks/lean-studio.rb).
# packaging/update-manifests.py rewrites the version and checksums for each release.
# See docs/packaging/homebrew.md.
cask "lean-studio" do
  arch arm: "arm64", intel: "x64"

  version "1.2.0"
  sha256 arm:   "a7af3a7fceb4102aaf534c651f2263c8f14ed2f30cac748139aec6c519d439f3",
         intel: "cc08508f707465c3d90dbfa0b10cc13212107572a1532ccfcdc0f7dba1ae2e98"

  url "https://github.com/keithadler/leanstudio/releases/download/v#{version}/LeanStudio-#{version}-osx-#{arch}.zip"
  name "Lean Studio"
  desc "IDE for the Lean 4 theorem prover"
  homepage "https://github.com/keithadler/leanstudio"

  livecheck do
    url :url
    strategy :github_latest
  end

  # .NET 10 needs macOS 14 or later. macOS 27 runs only on Apple silicon; the Intel build is for Intel Macs on 14–26.
  depends_on macos: :sonoma

  app "Lean Studio.app"
  # `leanstudio --mcp` runs the MCP server for AI assistants; `leanstudio path/to/project` opens a project.
  binary "#{appdir}/Lean Studio.app/Contents/MacOS/LeanStudio", target: "leanstudio"

  # Settings and the crash log only. The tutorial and playground in ~/Documents/Lean Studio hold the person's own work,
  # so they stay.
  zap trash: "~/Library/Application Support/LeanStudio"

  caveats <<~EOS
    Lean Studio needs elan, Lean's toolchain manager. If you don't have it, Lean Studio
    offers to install it on first launch, or run:
      brew install elan-init

    Releases that are not yet notarized are blocked by Gatekeeper on first launch.
    If macOS says it cannot check the app, open System Settings > Privacy & Security
    and click "Open Anyway" for Lean Studio.
  EOS
end
