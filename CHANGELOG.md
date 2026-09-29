# Changelog

## Unreleased

**A first-class profiler** (Lean ▸ Profiler, ⌘⌥T / Ctrl+Alt+T; the Timing panel is now the Profiler panel):
- A flame graph of Lean's trace for the whole file or one declaration: each step as wide as its cost, coloured by the kind of work (elaboration, instances, `simp`, unification, reduction, the kernel), failed attempts outlined. Hover to read a step; click to zoom in.
- Bottom up: every step of the trace summed by what it is, by its own cost.
- The cost of each tactic line, in the panel and at the end of the line in the editor.
- Lean's own profiler (`set_option profiler true`): its time per category, and per tactic, instance problem and check.
- Counters (`set_option diagnostics true`, in a run of its own): the simp lemmas each declaration tries and how many succeed, the instances it uses, the definitions it unfolds.
- Heartbeats instead of time: deterministic, in `maxHeartbeats` units, with each declaration's share of the default limit.
- The median of 3 or 5 runs, with how much each time varied.
- A baseline: after an edit, each declaration's change, and the total before and after.
- Profile only the declaration at the cursor (the file is cut after it, so it is quick to repeat), or every file of the project.
- Copy a profile as a Markdown table; save Lean's trace for the Firefox Profiler.
- The `profile` MCP tool gets all of it: `declaration`, `measure` (time or heartbeats), `runs`, `counters`, and `compare_content` to check that a change made a file faster.

## 0.10.0

**A newer Lean, when it's safe to say so.** The Toolchains panel says when a stable Lean newer than the project's is out, e.g. "Lean v4.34.1 is out. This project uses v4.34.0.", and *Use it for this project* installs it, pins it in `lean-toolchain` and restarts Lean. It stays quiet for a project that requires Mathlib or any other package, whose Lean has to follow its dependencies, and for one pinned to a release candidate, a nightly or the `stable` channel. The latest release is looked up on GitHub once per run, only while update checks are on (the same switch as Lean Studio's own).

A long toolchain name in the Toolchains list, such as a nightly, was cut off mid-word with nothing to show there was more. It ends in an ellipsis now, with the whole name on hover.

**Tests:**
- `ExtractsAGoalAsALemmaThatLeanAccepts` compared Lean's two `sorry` warnings in the order they arrived. Lean elaborates declarations in parallel, so on a CI Mac the second sometimes came first. It compares them in file order now, which is what the test meant.
- The headless run's Toolchains screenshot named a nightly the project no longer pinned: the run pins one to test *Use for project*, writes the file back, and never refreshed the panel. It refreshes now, and the three new checks cover the offer (made to a project with no dependencies, not to one that requires Mathlib, not with update checks off) against a stand-in for GitHub.
- A test now pins that a `sorry` behind a structure's field is not verified. A theorem that takes such a structure mentions neither `sorry` nor the constructor, only the type, and a walk that follows only types never reaches the field. Tenet had exactly that bug until 54dbb20; every Lean Studio release already has the fix, so this guards the next submodule bump. Checked by pointing the submodule at the commit before the fix, where the test fails with the theorem marked verified.
- `ServesManyRequestsAtOnceAndTurnsStrangersAway` failed about one run in three on a busy machine. Its 300 simultaneous connections overflowed the listen queue, which macOS caps at 128 (`kern.ipc.somaxconn`) and resets past, so it was testing the operating system rather than the bridge. It still sends 300 requests at once, over at most 64 connections, as a browser would: 0 failures in 20 runs under the same load.

## 0.9.1

**Fixes, found by driving the app from outside** (Finder, `open`, AppleScript, VoiceOver, an MCP assistant):
- A window that crashed left its assistant pipe (a socket file on macOS and Linux) behind, and every window opened after it quietly stopped serving assistants: `studio_context` and `studio_show` answered "Lean Studio is not running" while it was. A socket nothing is listening on is now reclaimed, and a window that can't serve says so in Output.
- The crash behind it: a picker (Go to File, the command palette, snippets, quick fixes and the rest) being dismissed while it was already closing ended the app.
- A `.lean` file or project opened from Finder, the Dock or `open -a "Lean Studio" file.lean` was ignored, and the last session opened instead. It now opens, at launch and while Lean Studio is running.
- AppleScript's `quit app "Lean Studio"` (and anything else that sends the quit Apple Event) was told "User canceled" (-128) although Lean Studio quit: Avalonia accepts a quit by telling macOS "cancel" and ending the app itself. Lean Studio now answers the quit event itself, with success, or with "User canceled" only when it stops to ask about unsaved changes. A quit with nothing unsaved no longer cancels and closes the window again afterwards.
- VoiceOver and other accessibility tools read the toolbar's buttons (and every button or menu item with an icon beside its text) as "Avalonia.Controls.StackPanel". They are read by their text now, or their tooltip.
- After Restart File, the infoview said "Click somewhere in the Lean file to enable the infoview" until the cursor moved. It shows the state at the cursor again when the file reopens.
- `leanstudio --help` opened the IDE instead of printing help, and `-h` was taken as a file name. Both print the options now, and `--version` prints the version.
- `brew uninstall --zap` removed `~/.config/LeanStudio`, but on macOS the settings and crash log are in `~/Library/Application Support/LeanStudio`.

## 0.9.0

**Proof-State Map** (Lean ▸ Proof-State Map ▸ This File / Whole Project):
- Every tactic step's goals, from Lean's language server, merged across proofs into one map of states: where different proofs reach the same state they share it. The window lists the states two or more proofs reach, most shared first, with the proofs to jump to and *Extract as Lemma…*, which has Lean write the lemma for that state above the first proof that reaches it.
- The same states in 3D beside the list, in the window's own web view (the browser where there is none): drag to turn, scroll to zoom. Picking a state in the list selects it in 3D and the other way round, and the view follows the light and dark themes. It is drawn by a small script of its own, with no library added.
- What counts as the same state is a choice: exact (up to renaming local names), same goal (hypotheses ignored), or same shape (names and numbers ignored). Trivial goals (`False`, `True`, `a = a`, numbers only) are never listed.

**AI in the editor, on your own computer**:
- Lean Studio has its own AI, and prefers a model that runs locally: Apple's on-device model on macOS 27 (through the `fm` command, started and stopped by Lean Studio), or Ollama, LM Studio, llama.cpp or MLX when one is running. Claude (with your Anthropic key) or any OpenAI-compatible service can be chosen instead; the automatic choice never uses the cloud unless you allow it. Keys are kept in the Keychain. *AI ▸ Choose a Model…* shows what was found.
- *AI ▸ Ask AI to Prove This Sorry* (⌘⌥A): the model suggests several proofs, Lean runs each from the sorry's own state, and only the ones Lean accepts are offered, ✦-marked, in the Prove It card. If none works, it asks again with the rejected attempts. Proofs of several lines keep their layout when filled in.
- Prove It asks the AI too when no tactic in its portfolio closes a goal (the AI menu turns this off), and each goal it can't close has *✦ Ask AI for a proof*.
- *AI ▸ Explain This* explains the error or goal at the cursor; *AI ▸ Ask AI…* (⌘⌥K) is a conversation about the code at the cursor, with *Insert at Cursor* on every code block.
- Prompts are cut to fit the model: the goal and the declaration for Apple's 4K-token model, more of the file for larger ones. Reasoning models' `<think>` sections are hidden.

**macOS 27**:
- The app needs macOS 14 or later, as .NET 10 does (it said 12). The Homebrew cask says so too.
- The Intel build running under Rosetta on an Apple silicon Mac says so in Output, and the update check offers the Apple silicon build: macOS 27 is the last macOS to run Intel apps.
- `.lean` files are declared as source code, so Finder, Spotlight and Quick Look treat them as text; the app is filed under Developer Tools.
- CI uses the Node 24 versions of GitHub's actions.

**Following a long build**:
- A build dashboard: while a build, the Mathlib cache or Tenet runs with no file open (or from *Details* on the progress banner), the editor area shows the whole task: the stage (toolchain, cache, build, verify), the bar and the time left, the modules compiling now, the slowest so far, and warnings and errors as they come.
- The progress shows on the app's icon: the percentage as a badge on the Dock icon on macOS, and the taskbar button's progress bar on Windows. When a long task ends while Lean Studio is in the background, the Dock icon bounces once, or the taskbar button flashes.
- Problems groups repeated messages: 205 uses of one deprecated lemma are one row, "205 × … in 12 files", which opens to list them.
- The progress banner no longer misses the last lines of a burst (a warning printed right after a module).

**Fixes, found by stress and fuzz testing** (new: thousands of random inputs through every parser and editing engine, and concurrency, crashes, floods and garbage thrown at the Lean connection, the MCP server and the app):
- One unexpected message from Lean's server (not a JSON object, or one a handler choked on) stopped all reading from it, so everything after it hung. Such a message is now skipped and logged.
- Assistants checking the same file at the same moment could get each other's messages, and two `run_lean` snippets each other's results; Prove It and the other checks that use a scratch copy could cross the same way. Checks of one file now take turns.
- Requests and edits still on their way when Lean restarted failed in a way nothing caught; they now fail as a closed connection does.
- Vim: repeating (`.`) in visual mode a change that had typed a `.` repeated forever until the stack overflowed, which would close the app. `i(` on the empty last line crashed, and `{` and `}` took seconds in a large file.
- Emacs keys: after a kill, the mark could point past the end of the text, and the next move crashed.
- Several cursors: Backspace or Delete where a selection meets another cursor crashed.
- A number where text belongs in commands.json or keybindings.json broke the command palette or the keys; it is now taken as text, or reported.
- A Loogle answer of an unexpected shape crashed the search, and closing a window after its project was put away logged an error.
- Every file ever opened stayed in memory after it was closed (the editor remembered each one's scroll position): about 0.7 MB per closed file of that size, all day. Closed files are now freed, and the tests check it.

**Fixes, found by checking every feature against what it says**:
- Uses of deprecated names are struck through again. Lean sends no semantic token for most names, so the strike-through now comes from Lean's "has been deprecated" warning.
- Cancel stops every long task: a task from the Tasks menu or a project command, and Tenet's verification, not only builds and cache fetches.
- A long task's closing note counts the warnings it printed at the very end.
- Local history keeps every version when two saves land in the same millisecond.
- Prove It's counterexamples from Plausible (in Mathlib projects, for lists, reals and more) are reported again, as `xs = [1, 0]`: Plausible's message starts with a rule of `=` signs, which hid it.
- When LeanSearch's service has trouble, the Library says so ("LeanSearch is having trouble right now (it answered HTTP 500)"), instead of saying it could not be reached.
- Comments fold, as the README says: Lean's server folds declarations and namespaces but not comments, so the editor now finds multi-line `/- … -/` comments itself.
- Completion right after typing answers about what was just typed: the edit Lean hadn't been sent yet is sent first, instead of waiting a fixed moment and hoping.
- Move to Trash on macOS uses the system's Trash directly, so it no longer asks for permission to control Finder, and Put Back still works.

## 0.8.0

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
- Keeping a renamed declaration's old name as a deprecated alias added LF lines to a file with Windows (CRLF) line endings. It now matches the file.

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
