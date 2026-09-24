# Changelog

## 0.7.0

**Lean's infoview, widgets and all, inside the window**:
- The Infoview tab, next to Goals and Compiled C, is the infoview VS Code uses. It renders ProofWidgets and every other user widget.
- It uses the system's web view: WebKit on macOS, WebView2 on Windows, and WebKitGTK on Linux (where, without WebKitGTK, it offers the browser).
- When Lean shows a widget at the cursor, the Tactic State has a Widget button that opens the tab.
- The infoview follows the app's light or dark theme as it changes, in the window and in the browser.
- Links in the infoview open in the browser.

**Installing**: the Homebrew tap is live, so `brew install --cask keithadler/tap/lean-studio` installs Lean Studio on a Mac.

## 0.6.0

**Vim mode** (off by default):
- Normal, insert and visual modes.
- Motions and operators with counts, and text objects including Lean's ⟨⟩.
- `.` to repeat, search, `:w` and `:q`, and the mode shown in the status bar.

**Lean's own infoview, with widgets**: *View ▸ Lean Infoview in Browser* opens the official `@leanprover/infoview`,
connected to Lean Studio's Lean server and following its cursor. It renders ProofWidgets and every other user
widget. It runs on 127.0.0.1 only, with a secret token.

**Tested against real Mathlib**, weekly in CI:
- In Prove It, `positivity` is suggested before `nlinarith`.
- Library searches (`exact?`, a minute in Mathlib) only run when no other tactic closed the goal.


**The editor knows what Lean knows**:
- Semantic highlighting of variables and fields, with deprecated names struck through.
- Inlay hints.
- Every occurrence of the name at the cursor is highlighted.
- Who Uses This / What This Uses, from Lean's call hierarchy.
- Trace messages are expandable trees, fetched level by level.

Semantic highlighting and inlay hints are Preferences options, both on by default.

**C and Lean's FFI**:
- Go to definition goes from an `@[extern]` to its C function and back.
- `@[extern]` bindings are checked against the project's C files (missing functions, wrong argument counts) in Problems.
- C stubs are written with the signature Lean expects, and New C Binding… writes both sides.
- clangd serves C files, with Lean's headers on the include path.
- MCP tool `ffi_bindings`.

**Installing**:
- A Homebrew cask for Apple silicon and Intel Macs, and winget manifests for Windows x64 and ARM64, published once the tap and the winget submission are live.
- An AppImage for Linux x86_64 and aarch64 with every release.
- Releases can be signed with a Developer ID and notarized on macOS, and Authenticode-signed on Windows, when the signing secrets are set.

**Fixes**:
- A second Lean Studio window could take over the assistant bridge from the first. The first window now keeps it.
- When an MCP tool failed in an unexpected way, the assistant got no answer to that request, and the server could stop when the assistant disconnected. It now answers with an internal error and keeps serving.

**Documentation**:
- An architecture guide ([docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)) and a contributor guide ([CONTRIBUTING.md](CONTRIBUTING.md)).
- Every public type and member in `src/` has an XML doc comment, and the build fails without one.
- The README's status section is brought up to date, and it compares Lean Studio with VS Code and the lean4 extension, gaps included.

## 0.5.0

**Four more features**:
- **Counterexamples**: when no tactic closes a goal, Prove It looks for values that make it false.
- **Extract Goal as Lemma**: Lean writes the lemma, with just the hypotheses the goal needs.
- **A REPL** that runs in the file at the cursor.
- **The Project Map**: a graph of the project's declarations, what rests on sorry, and what to fix first.

MCP tools: `extract_lemma` and `project_map`, and `prove` now reports counterexamples.

**Polish**:
- The menu, toolbar and tab strips share one surface, and panels are divided by hairlines.
- Vector icons are drawn for the toolbar, file tree and Tactic State.
- Panel buttons are consistent chips.
- Empty panels say what will appear in them.
- The welcome screen is redesigned, and the light theme is fixed throughout.
- Dialogs have themed backgrounds and wrapping prompts, and the Preferences buttons stay in view.

**Fixes**: when Lean was asked whether a file was done, it could answer with an early batch of messages; it now waits until they stop arriving.

## 0.4.0

**Five new features**:
- **Prove It** runs a portfolio of tactics (rfl, decide, simp, omega, norm_num, ring, linarith, aesop, grind, exact? and more) against each `sorry`, independently and in one pass of Lean. Use any that works with a click, or fill in every sorry it proved.
- **Why isn't this proved?** shows the chain of lemmas from a theorem down to the `sorry` or axiom it rests on, found by Tenet.
- **A performance heat map** shows Lean's profiler per declaration, with the costliest step in each, in a Timing panel and in the editor.
- **Proof walkthroughs**: every proof in a file, step by step, as a web page anyone can read. Share links open the file in the Lean 4 web editor.
- **Plain-English search of Mathlib** uses LeanSearch.

MCP tools for all five: `prove`, `why_not_proved`, `profile`, `export_walkthrough`, `search_mathlib`.

**Essentials**: "Install Lean" for a computer without it (the official elan installer and the latest stable
Lean); back and forward through jumps; next and previous problem (F8); a banner to rebuild imports and recheck
when an imported file changes; "Add import" for unknown names, found on Loogle; creating, renaming (as a
module, imports and all), trashing, revealing files and opening terminals from the file tree; drag and drop;
a Preferences window; New Window; Lean restarted if it crashes; unexpected errors logged instead of fatal.

**Fixes**: the Git panel could keep listing deleted files when a partial refresh cancelled a full one.

## 0.3.0

**Power tools**: the C Lean emits for the definition at the cursor, side by side; hover any subterm of a goal
for its type, full form and docs; pin goals to compare; lightbulbs where Lean offers fixes, Fix All in File,
and optional automatic application of a lone Try this.

**Daily work**: a Sorries & TODOs panel for the whole project; build errors from every file in Problems;
auto-save and local history; search and replace across files with regex groups; rename a module with its
imports; Loogle search of all of Mathlib and documentation links; line blame in the status bar; tasks (lake
test, lint, executables, scripts, shell commands); layout toggles, zen mode and word wrap; cursor and file
restored between sessions.

**Fixes**: the unsaved marker could be wrong after a whole-file change, which disabled blame and misled
auto-save; restoring a version could not be undone; the release job attached screenshots to releases.

## 0.2.0

**For newcomers**: a ten-lesson tutorial in the editor with progress; plain-English explanations of tactics,
keywords and errors; goals read aloud in English; a playground; famous theorems to try; `#eval` results at the
end of the line; ▶ Run for programs with `main`; snippets; a symbol palette; live ✓/◐/✗ proof status in the
Outline.

**Editing and navigation**: Try this suggestions as buttons and a quick-fix menu; rename; find references;
outline; go to symbol; go to file; find in files; command palette; auto-closing brackets (including `⟨⟩`);
bracket matching; smart indentation; folding; how to type any symbol on hover.

**Git and GitHub**: a Source Control panel over your own git (diffs, stage, commit, push, pull, branches),
change bars in the gutter, clone `owner/repo`, publish to GitHub, pull requests, open on GitHub, and the Lean CI
workflow, via the GitHub CLI.

**AI assistants**: `LeanStudio --mcp` is an MCP server for Claude Code, Gemini CLI, Codex, Grok and others, with
a bridge to the open window; AI ▸ Connect an AI Assistant sets them up.

**Updates**: Lean Studio checks GitHub Releases once a day and offers the new version for download.

**Fixes**: Output text was invisible in the dark theme; switching files could blank the editor; underscores
vanished from names on buttons.

## 0.1.0

The first version: the editor, tactic state with proof steps, Tenet verification, the declaration navigator,
projects and toolchains, packaging for macOS, Windows and Linux.
