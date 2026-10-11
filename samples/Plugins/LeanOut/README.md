# LeanOut, a Lean Studio plugin for [leanout](https://github.com/keithadler/leanout)

leanout is a clean-room exporter from Lean 4 to the lean4export NDJSON format that other kernels check. This
plugin puts it in the command palette so that, while you write a proof, you can ask from the caret:

- **Export: Axioms of the Declaration at the Caret**: what this theorem rests on. It lists every axiom in the
  closure of the declaration, and says which are the standard three (`propext`, `Quot.sound`,
  `Classical.choice`), which is a `sorry` (`sorryAx`, directly or in something it uses), and which one a
  dependency adds. It also says how many declarations the proof rests on. Faster than a rebuild and `verify`,
  and it works on one declaration.
- **Export: Check the Declaration at the Caret with Another Kernel**: exports the declaration with everything it
  depends on, checks the export is well formed, and, when [Tenet](https://github.com/keithadler/tenet) is installed
  (`dotnet tool install -g tenet`), has it re-check the lot. A second opinion on one proof while you work.
- **Export: This File's Module to NDJSON**: writes the module's own declarations (with their dependencies) to
  `.lake/leanout/module.ndjson` in the project, validated, ready to hand to any checker or to CI.
- **Export: Set Up the Exporter for This Project's Lean**: builds the exporter for the project's toolchain now,
  instead of on first use.

The caret's declaration is found from the file's text (`namespace`, `section`, `end`, and the last declaration
keyword at or above the caret). Select a name instead to use that one. Private and anonymous declarations are
reported as such, since a file cannot spell their names.

## How it works

leanout reads the project's `.olean` files through the same Lean that built them, so there is one exporter build
per toolchain. The first command clones leanout into `<settings folder>/leanout/<toolchain>/` and builds it
with `elan run <toolchain> lake build leanout` (about fifteen seconds, once per toolchain), then runs it through
`lake env` in the project. It reads the **last build**: build the project after editing, as with `verify`.

`LEANOUT_REPO` points the clone at a fork or a local checkout, and `LEANOUT_HOME` moves the builds elsewhere.

## Build and install

```bash
dotnet build -c Release -o ~/Library/Application\ Support/LeanStudio/plugins/LeanOut
```

then restart Lean Studio. Output says `Plugin: LeanOut (LeanOut.dll)` when it loads. Needs `git`, elan and Lean
4.33.0 or later in the project. The plugin runs with Lean Studio's permissions, like any plugin.
