# Homebrew cask for Lean Studio, a desktop IDE for Lean 4.
# Created by Keith Adler (@keithadler). MIT License.
#
# This file is the source of truth; it is copied to keithadler/homebrew-tap (Casks/lean-studio.rb).
# packaging/update-manifests.py rewrites the version and checksums for each release.
# See docs/packaging/homebrew.md.
cask "lean-studio" do
  arch arm: "arm64", intel: "x64"

  version "0.9.1"
  sha256 arm:   "05801db82a3dfe21e2df340716fd017ffacaed27e6c8156e0733f6394b079b1c",
         intel: "94c5edcafc686e7f2bec488271ccb5ce8dfebce6f6c897a145672afde249443a"

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
