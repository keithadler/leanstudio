#!/usr/bin/env python3
"""Point the package manifests in packaging/ at a published release.

    packaging/update-manifests.py 0.6.0

Reads the release's assets from GitHub (tag v0.6.0) and rewrites the version, download URLs and SHA-256 checksums in:
  - packaging/homebrew/lean-studio.rb
  - packaging/winget/KeithAdler.LeanStudio*.yaml (plus the release date and release notes link)

Checksums come from the digests GitHub records for each asset; an asset without one is downloaded and hashed.
Set GITHUB_TOKEN to avoid the API's anonymous rate limit. Needs only the Python standard library.
"""
import hashlib
import json
import os
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

REPO = "keithadler/leanstudio"
ROOT = Path(__file__).resolve().parent.parent


def fetch(url: str, accept: str = "application/vnd.github+json"):
    req = urllib.request.Request(url, headers={"Accept": accept, "User-Agent": "leanstudio-update-manifests"})
    token = os.environ.get("GITHUB_TOKEN")
    if token and url.startswith("https://api.github.com/"):
        req.add_header("Authorization", f"Bearer {token}")
    return urllib.request.urlopen(req)


def release(version: str) -> tuple[dict[str, str], str]:
    """For release v<version>: asset file name -> lowercase SHA-256, and the publication date (YYYY-MM-DD)."""
    try:
        with fetch(f"https://api.github.com/repos/{REPO}/releases/tags/v{version}") as r:
            rel = json.load(r)
    except urllib.error.HTTPError as e:
        sys.exit(f"no release v{version} on {REPO} ({e.code}): has CI finished publishing it?")
    sums = {}
    for asset in rel["assets"]:
        digest = asset.get("digest") or ""
        if digest.startswith("sha256:"):
            sums[asset["name"]] = digest.removeprefix("sha256:").lower()
            continue
        print(f"hashing {asset['name']} (no digest on GitHub)…", file=sys.stderr)
        h = hashlib.sha256()
        with fetch(asset["browser_download_url"], accept="application/octet-stream") as r:
            for chunk in iter(lambda: r.read(1 << 20), b""):
                h.update(chunk)
        sums[asset["name"]] = h.hexdigest()
    return sums, (rel.get("published_at") or rel["created_at"])[:10]


def need(sums: dict[str, str], name: str) -> str:
    if name not in sums:
        sys.exit(f"release has no asset {name}: is the release finished?")
    return sums[name]


def update_cask(version: str, sums: dict[str, str]) -> None:
    path = ROOT / "packaging/homebrew/lean-studio.rb"
    text = path.read_text()
    arm = need(sums, f"LeanStudio-{version}-osx-arm64.zip")
    intel = need(sums, f"LeanStudio-{version}-osx-x64.zip")
    text = re.sub(r'^(  version )"[^"]*"', rf'\g<1>"{version}"', text, count=1, flags=re.M)
    text = re.sub(r'(sha256 arm:\s+)"[0-9a-f]{64}"', rf'\g<1>"{arm}"', text, count=1)
    text = re.sub(r'(intel:\s+)"[0-9a-f]{64}"', rf'\g<1>"{intel}"', text, count=1)
    path.write_text(text)
    print(f"updated {path.relative_to(ROOT)}")


def update_winget(version: str, sums: dict[str, str], date: str) -> None:
    folder = ROOT / "packaging/winget"
    for path in sorted(folder.glob("KeithAdler.LeanStudio*.yaml")):
        text = path.read_text()
        text = re.sub(r"^PackageVersion: .*$", f"PackageVersion: {version}", text, flags=re.M)
        text = re.sub(r"^ReleaseDate: .*$", f"ReleaseDate: {date}", text, flags=re.M)
        text = re.sub(r"releases/tag/v[^/\s]+$", f"releases/tag/v{version}", text, flags=re.M)
        for arch in ("x64", "arm64"):
            name = f"LeanStudio-{version}-win-{arch}.zip"
            url = f"https://github.com/{REPO}/releases/download/v{version}/{name}"
            text = re.sub(
                rf"(- Architecture: {arch}\n    InstallerUrl: )\S+\n    InstallerSha256: [0-9A-Fa-f]{{64}}",
                lambda m: f"{m.group(1)}{url}\n    InstallerSha256: {need(sums, name).upper()}",
                text)
        path.write_text(text)
        print(f"updated {path.relative_to(ROOT)}")


def main() -> None:
    if len(sys.argv) != 2 or not re.fullmatch(r"\d+\.\d+\.\d+", sys.argv[1]):
        sys.exit("usage: packaging/update-manifests.py <version, e.g. 0.6.0>")
    version = sys.argv[1]
    sums, date = release(version)
    update_cask(version, sums)
    update_winget(version, sums, date)


if __name__ == "__main__":
    main()
