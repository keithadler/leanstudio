# Changelog

## Unreleased

**For Mathlib contributors and everyone who writes a lot of Lean**:
- *Lean ▸ Remove Unused Imports* removes the imports a file doesn't need, as one undoable edit, and says why for each: nothing uses it, or another import already brings it in. Lean elaborates the file and every constant, tactic, macro and notation is traced to its module, so an import needed only for `ring` or a notation stays. Works on any file, not only `module` files like `lake shake`.
- *Lean ▸ Lint File* runs the linters CI runs and lists what they find in Problems: Mathlib's standard set (its style linters among them) in a Mathlib project, every linter Lean has elsewhere, and Batteries' environment linters (missing docstrings, `simp` normal form, unused arguments) wherever Batteries is available.
- Renaming a declaration offers to keep the old name as a deprecated alias, as Mathlib asks: `@[deprecated (since := "…")] alias old := new` with Batteries, and the same in core Lean without it.
- A new file joins its library's root file when that file imports every module (as `Mathlib.lean` does), and *Import Every Module in the Library Root* adds any that are missing, like `lake exe mk_all`.
- In the Mathlib repository, *Get Mathlib Cache for Open Files* fetches only what the open files need.
- MCP tools `unused_imports` and `lint`.

**Editing like a pro**:
- *View ▸ Split Editor* (⌘\\ / Ctrl+\\) puts two files, or two places in one, side by side. Each side has its own file; the one you're typing in is the one the Tactic State, the status bar and every command follow.
- Several cursors: ⌘D (Ctrl+D) adds the next occurrence of the selection, ⌘⇧L selects every occurrence, ⌘⌥↑/↓ add a cursor on the line above or below, ⌥-click adds one anywhere. Typing, Backspace and Delete act at every cursor, as one undo step. ⌥-drag selects a column.
- Your own keyboard shortcuts: *View ▸ Keyboard Shortcuts File* opens keybindings.json, which binds any command-palette command to any key (`{ "key": "Cmd+Alt+L", "command": "Lean: Lint File (the linters CI runs)" }`). It's listed with every command to start from, and applies as soon as it's saved.
- Screen readers: the editor is announced as an edit field named after its file, with its text readable, and every button, box and list (dialogs included) has a spoken name. The headless test checks all of them through the same accessibility API VoiceOver and UI Automation use.
- *View ▸ Emacs Keys*: C-f/b/n/p/a/e, M-f/b, C-k and C-y with a kill ring that adds up, the mark and region (C-SPC, C-w, M-w, C-x C-x), C-/ to undo, C-s to search, C-x C-s to save. Kills go to the clipboard.

**The Tactic State, as VS Code's infoview has it**:
- Its ⋯ menu hides type assumptions (`α : Type`), instances, inaccessible names (`n✝`) and let values, and can put each goal's target before its hypotheses. The choices are remembered.
- *Pause* keeps showing the state while the cursor moves elsewhere; a Paused chip resumes it.
- *Copy Goals*, and *Goals as a Comment Above the Cursor* (VS Code's Copy Contents to Comment).
- The end of each proof is marked in the editor: a quiet ✔ where Lean says the goals are accomplished, "⊢ goals left" where they aren't (Preferences can turn it off). Lean's silent "Goals accomplished!" messages are asked for and kept apart, so they never show as messages.

**Tools for large projects and for troubleshooting**:
- Math in docstrings reads as text in hovers and the Library: `$\sum_{i < n} x_i^2 \le C$` shows as `∑_(i < n) xᵢ² ≤ C`, with ℝ, ℕ, fractions, roots and Greek.
- Unicode abbreviations of your own: *View ▸ Unicode Abbreviations File* opens abbreviations.json (`{ "zeta5": "ζ(5)" }`, VS Code's `customTranslations` format). Yours win over built-in ones, and apply when saved.
- *Lean ▸ Imports and Imported By* lists what a module imports and which of the project's modules import it, and says how many modules rebuild when it changes.
- *Lean ▸ Instances of Class at Cursor* asks Lean for every instance of a type class the file can see, with its type. Pick one to see it in the Library.
- *Lean ▸ Lean's Processes* lists Lean's file workers, biggest first, with their memory and time running. Pick one to stop it (the file restarts if it's open).
- *Lean ▸ Count Heartbeats* measures each declaration in `maxHeartbeats`' units, heaviest first, with its share of the default limit, and warns about any past half of it. It works in core Lean, without Mathlib's `#count_heartbeats`.
- *Lean ▸ Update Mathlib (and see what broke)* updates Mathlib and moves the project to Mathlib's toolchain. It then fetches the cache, builds, and reports the commits before and after and the errors by file. It also offers to rename every use of a name the update deprecated, to what Lean says to use instead. *Undo Last Dependency Update* puts lake-manifest.json and lean-toolchain back.
- *Tenet ▸ Check the Blueprint Against Lean* reads a leanblueprint blueprint (`blueprint/src/*.tex`) and checks each node against the last build: done, proved but not marked `\leanok`, ready to prove, not started. Disagreements come first: a `\leanok` over a declaration that rests on sorry, or a `\lean{…}` that names a declaration Lean doesn't have. Click one to go to it in the .tex file.
- Builds show more of what they're doing: the modules being compiled right now, and for how long, under the progress bar (Lake names a module only when it finishes, so they're read from the running `lean` processes); marks in the file tree (✓ built, ⋯ compiling, ◐ uses sorry, ✗ errors, and a folder shows the worst inside it); and the slowest modules of the last build in the Timing panel, where a click opens one. Files opened during a build, or whose imports went stale, are checked again when it ends, instead of keeping errors the build has fixed.
- Remote projects: *Remote: Open a Project on Another Machine (SSH)* runs Lean's server, Lake and elan on another machine over SSH while you edit through a local mount of its folder (sshfs, a network drive), with paths rewritten both ways so everything in the editor names local files. The tests run a real Lean server this way, through a stand-in for ssh and a mount whose path differs from the remote one.
- Plugins: a .NET class library built against the new `LeanStudio.Plugins` assembly, put in the `plugins` folder of the settings folder, is loaded at start in a load context of its own. It can add palette commands, read and edit the active file, read Lean's messages, check Lean code with the project's dependencies, run programs, and hear files open and save. Output lists what loaded and why anything didn't; *Plugins: Open the Plugins Folder* and *Plugins: List Loaded Plugins* are in the palette. `samples/Plugins/HelloLean` is a complete example.
- Project commands: `.leanstudio/commands.json` gives a project commands of its own (a program and arguments with `${file}`, `${module}`, `${line}`, `${word}`, `${selection}`, `${root}`). They show in the command palette as "Project: …" and can be bound to keys.
- MCP tools `heartbeats`, `instances` and `blueprint`.
- Measured on the Mathlib repository itself (about 8,500 files): the project opens at once, Tenet reads its build in 23 s, a Library search takes under 1 s, Sorries & TODOs under 3 s, find in files under 1 s, and Lean checks `Algebra/Group/Basic.lean` in 13 s. `LeanStudio.Snapshot --scale` repeats the measurement.
- Preferences: arguments for Lean's server (such as `-DmaxHeartbeats=400000`), and a log of every message with it, for troubleshooting.

**Progress you can read**:
- A build, the Mathlib cache and Tenet's verification show a real progress bar: Lake's own `[done/total]`, what came from the cache and what was compiled, the time left (from the rate of real work, not replayed jobs), the module just finished, and the slowest ones so far. It sits in the status bar and in a banner over the editor, with Cancel. The percentage rounds down, so 8,705 of 8,712 reads 99%.
- Warnings and errors reach Problems while the build is still running.
- When Lean has to be downloaded or installed first, the status bar says so.
- A long task ends with a note of how long it took and what it found.
- Proof Steps no longer calls a `sorry` "goals accomplished": it says "put off with sorry".

**Fixes**:
- Tenet no longer counts a module whose source file was deleted but whose build was left behind.
- A name declared in two of the project's modules that don't import each other (a benchmark's challenge statement and its solution) was dropped from Tenet's report, and its axioms came back empty. Each module's declarations are now read from that module, such a name is resolved as the module reading it sees it, and asking about it without saying which module is refused rather than guessed. Found while validating the ζ(5) formalization (mo271/zeta5).

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
