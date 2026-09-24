# Homebrew cask for Lean Studio, a desktop IDE for Lean 4.
# Created by Keith Adler (@keithadler). MIT License.
#
# This file is the source of truth; it is copied to keithadler/homebrew-tap (Casks/lean-studio.rb).
# packaging/update-manifests.py rewrites the version and checksums for each release.
# See docs/packaging/homebrew.md.
cask "lean-studio" do
  arch arm: "arm64", intel: "x64"

  version "0.5.0"
  sha256 arm:   "394f373bbe494086fcd4ef881408857f127ee5bf9030a10186340b02c234e327",
         intel: "6334d1055412de768f1a16f905bb554ddd97ba6500b1b9122c26e59cd67aaa29"

  url "https://github.com/keithadler/leanstudio/releases/download/v#{version}/LeanStudio-#{version}-osx-#{arch}.zip"
  name "Lean Studio"
  desc "IDE for the Lean 4 theorem prover"
  homepage "https://github.com/keithadler/leanstudio"

  livecheck do
    url :url
    strategy :github_latest
  end

  depends_on macos: ">= :monterey"

  app "Lean Studio.app"
  # `leanstudio --mcp` runs the MCP server for AI assistants; `leanstudio path/to/project` opens a project.
  binary "#{appdir}/Lean Studio.app/Contents/MacOS/LeanStudio", target: "leanstudio"

  # Settings only. The tutorial and playground in ~/Documents/Lean Studio hold the person's own work, so they stay.
  zap trash: "~/.config/LeanStudio"

  caveats <<~EOS
    Lean Studio needs elan, Lean's toolchain manager. If you don't have it, Lean Studio
    offers to install it on first launch, or run:
      brew install elan-init

    Releases that are not yet notarized are blocked by Gatekeeper on first launch.
    If macOS says it cannot check the app, open System Settings > Privacy & Security
    and click "Open Anyway" for Lean Studio.
  EOS
end
