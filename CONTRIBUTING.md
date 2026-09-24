# Contributing to Lean Studio

Thanks for your interest in Lean Studio. This page covers setting up a build, running the tests, and what a good
change looks like. For how the code is organized, read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first.

Everyone taking part is expected to follow the [code of conduct](CODE_OF_CONDUCT.md).

## Set up

You need:

- The [.NET 10 SDK](https://dotnet.microsoft.com/download). `global.json` pins the version.
- [elan](https://github.com/leanprover/elan#installation), with the toolchain the tests use:
  `elan toolchain install leanprover/lean4:v4.34.0`.
- `git`. The GitHub features also need the [GitHub CLI](https://cli.github.com) (`gh`), but no tests depend on it.

Clone with the Tenet submodule:

```bash
git clone --recurse-submodules https://github.com/keithadler/leanstudio
cd leanstudio
```

If you cloned without `--recurse-submodules`, run `git submodule update --init --recursive`.

## Build and run

```bash
dotnet build LeanStudio.slnx
```

```bash
dotnet run --project src/LeanStudio.App                         # the app
```

```bash
dotnet run --project src/LeanStudio.App -- samples/Proofs       # open a project or file on start
```

```bash
dotnet run --project src/LeanStudio.App -- --mcp                # the MCP server on stdio, to try by hand
```

The build treats warnings as errors, and every public type and member in `src/` needs an XML doc comment
(`///`). A missing or broken doc comment fails the build, just like a compiler warning.

## Test

```bash
dotnet test --project tests/LeanStudio.Tests
```

Most tests run against a real Lean server and a real `lake build`. If elan isn't installed, those tests are
skipped, not failed, so check the summary for skips before you conclude everything passed.

The end-to-end run drives the whole app headlessly against real Lean, and saves a screenshot after each step:

```bash
dotnet build LeanStudio.slnx -c Release
```

```bash
dotnet tools/LeanStudio.Snapshot/bin/Release/net10.0/LeanStudio.Snapshot.dll . snapshots
```

It exits non-zero if anything doesn't behave as expected. If your change affects something visible, look at the
screenshots in `snapshots/`. The images in `docs/images/` come from this run.

The headless run has no native window, so the Infoview tab shows its fallback there. To check the tab with the
platform's web view (after changing the infoview or its bridge), run this on a Mac or a Windows machine with a
desktop session. It opens a real window, and checks that Lean's infoview renders a user widget in it:

```bash
dotnet tools/LeanStudio.Snapshot/bin/Release/net10.0/LeanStudio.Snapshot.dll --native-infoview . snapshots
```

CI runs the build and tests on Linux, macOS and Windows, and runs the snapshot on Linux and macOS. Please make sure
they pass locally first.

## What a good change looks like

- **Let Lean decide.** If Lean can answer a question (a goal, a type, whether a tactic works), ask Lean through the
  server or a scratch copy of the file. Don't approximate it in C#.
- **Keep the person's work safe.** Analyses run on copies of files. Edits go through the editor as one undoable
  change, and edits to files that aren't open are recorded in local history first.
- **Put logic in Core.** Anything that doesn't need a window goes in `LeanStudio.Core`, so the app, the MCP server
  and the tests can all use it. If an assistant could use a new feature too, add an MCP tool for it in
  `src/LeanStudio.Mcp/LeanTools.cs`.
- **Test against real Lean.** New Lean-facing behaviour gets a test that runs Lean. Call `Lean.RequireLean()` so the
  test is skipped where Lean isn't installed, and put it in the `Lean` collection if it starts a server.
- **Document it.** Give new public code `///` comments that say what it does and what a caller needs to know, in
  the plain style of the existing comments. Update the README when a user-facing feature changes, and add a line to
  the `Unreleased` section of [CHANGELOG.md](CHANGELOG.md).
- **Match the surrounding code.** Follow the naming, layout and comment density of the file you're in. The
  settings in `Directory.Build.props` (nullable, latest C#, warnings as errors) apply everywhere.

## Releases

The version is `VersionPrefix` in `Directory.Build.props`. Pushing a `vX.Y.Z` tag makes CI build all six platforms
with `packaging/publish.sh` and publish a GitHub release. The in-app update checker looks for those releases, and
their assets need to keep the `LeanStudio-X.Y.Z-<runtime>` naming.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
