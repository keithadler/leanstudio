# The Linux AppImage

Each release tag also attaches an AppImage for each Linux architecture:

| File | For |
|---|---|
| `LeanStudio-<version>-x86_64.AppImage` | 64-bit Intel and AMD |
| `LeanStudio-<version>-aarch64.AppImage` | 64-bit ARM |

An AppImage is one executable file that runs on most distributions, with nothing to install:

```bash
chmod +x LeanStudio-*-x86_64.AppImage
./LeanStudio-*-x86_64.AppImage                  # the app
./LeanStudio-*-x86_64.AppImage path/to/project  # open a project
./LeanStudio-*-x86_64.AppImage --mcp            # the MCP server, for AI assistants
```

It is built with the static [type 2 runtime](https://github.com/AppImage/type2-runtime), so it doesn't need
`libfuse2` on the host. Where FUSE isn't available at all (in some containers, for example), run it with
`--appimage-extract-and-run`. Desktop integration tools such as [Gear Lever](https://github.com/mijorus/gearlever)
or AppImageLauncher add it to the application menu, using the `.desktop` file and icon inside.

## How it is built

[`packaging/linux/build-appimage.sh`](../../packaging/linux/build-appimage.sh) takes the self-contained build that
`packaging/publish.sh` leaves in `artifacts/<rid>/LeanStudio`, and lays out an AppDir:

```
LeanStudio.AppDir/
  AppRun                          runs usr/lib/leanstudio/LeanStudio with the arguments given
  leanstudio.desktop              packaging/linux/leanstudio.desktop
  leanstudio.png, .DirIcon        packaging/icons/leanstudio-256.png
  usr/lib/leanstudio/             the published app
  usr/share/applications/         the .desktop file again, for integration tools
  usr/share/icons/hicolor/256x256/apps/leanstudio.png
  usr/share/metainfo/com.keithadler.leanstudio.metainfo.xml   AppStream metadata: MIT, Keith Adler (@keithadler)
```

Then it runs [appimagetool](https://github.com/AppImage/appimagetool), which it downloads into
`artifacts/tools/` unless `APPIMAGETOOL` points at a copy. Both architectures build on an x86_64 machine;
appimagetool fetches the runtime for the target architecture itself. The AppStream file is validated offline with
`appstreamcli` when it's installed.

To build one locally (on Linux):

```bash
packaging/publish.sh linux-x64
packaging/linux/build-appimage.sh linux-x64     # writes artifacts/LeanStudio-<version>-x86_64.AppImage
```

In CI, the `package` job runs the script after `publish.sh` for both Linux runtimes, and the release job attaches
the AppImages with the other builds. The in-app update checker keeps offering the `.tar.gz` for Linux, because
it looks for the `linux-x64`/`linux-arm64` runtime names, which the AppImages don't use.
