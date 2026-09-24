# Showing loading and build progress well

Notes from validating a large project in Lean Studio: [mo271/zeta5](https://github.com/mo271/zeta5), with 253 files
and about 218k lines, on Mathlib and PrimeNumberTheoremAnd, using Lean 4.34.0-rc1, on 2026-09-23. It was a
headless run of the real app (`LeanStudio.Snapshot --validate`), with screenshots every few minutes. What a
person sees while a big project loads, fetches and builds should be better in the next version.

## What happened, and what the window showed

| Stage | Time | What the window showed | What was missing |
|---|---|---|---|
| Open the project | 0:00 | The welcome screen, and "Lean: starting…" in the status bar | The welcome screen stays for the whole run. There is no project overview. |
| elan downloads Lean 4.34.0-rc1 | ~0:00–0:30 | A raw `[lean] info: downloading https://…` line in Output | No progress, size or speed. The status bar says only "Lean: starting…". |
| Mathlib cache (8,700 files) | 0:02–1:56 | Raw `Downloaded: N file(s) [attempted N/8700 = 3%, 137 KB/s]` lines in Output, and an indeterminate bar with "Fetching Mathlib's cache…" | A real progress bar (N of 8,700, %), speed, and time left. |
| `lake build` (8,951 jobs, ~8,700 of them from the cache) | 1:56– | Raw `✔ [8864/8951] Built Apery.Table.V02 (83s)` lines in Output, and an indeterminate bar with "Building…" | See below. |

## What to build

Done in the next commit: 1 (real bar), 2 (time left), 5 (toolchain and cache progress in the status bar), 6 (build warnings in Problems live), 9 (a done note; the dock and taskbar badge are still to do), 11 (Tenet in the status bar, overall, with time left), plus the Proof Steps `sorry` wording. Still to do: 3 (modules in progress, which Lake doesn't print), 4 (a full dashboard view), 7 (module timings in the Timing panel), 8 (the file tree marking), 10 (the server and the build).


1. **A real build progress bar.** Lake prints `[done/total]` on every line. Parse it and show `8864 / 8951 (99%)`
   in the status bar, with a determinate bar. Say that most jobs were replayed from the cache, since a 97% bar
   after two minutes misleads when the rest takes 40.
2. **Time left.** Estimate it from the recent rate of jobs that were built, not replayed. Say plainly when
   the estimate is rough.
3. **What's building now.** Lake runs several `lean` processes at once, and their modules are the useful part.
   Show a small live list: "Building Apery.MainEstimate (2m 10s), Apery.Table.V03 (1m 20s)…". It can come from
   Lake's output (modules started but not finished) or from the running `lean` processes.
4. **A build view instead of the welcome screen.** When a project is open and a build or cache fetch runs, the
   centre should show a dashboard: stage (toolchain, cache, build, verify), the bar, time left, the modules in
   progress, the slowest modules so far, and warnings and errors as they come. The Output panel stays for the
   raw log.
5. **Toolchain and cache downloads with real progress.** elan's download and `lake exe cache get` both report
   progress. Show bytes or files, %, and speed in the status bar, instead of "Lean: starting…".
6. **Build warnings in Problems while it runs.** During the build, `warning: Challenge.lean:33:8: declaration uses
   'sorry'` was in Output while Problems said "No problems". Parse Lake's `warning:` and `error:` lines into
   Problems as they arrive. (Check what BuildProblems does today: it may only fill in at the end.)
7. **Per-module timings afterwards.** Lake prints each module's time (`Built Apery.Stirling (153s)`). After the
   build, show the slowest modules in the Timing panel, where per-declaration profiling already lives.
8. **Mark built and building modules in the file tree.** For example ✔ built, a spinner for building, ✗ for
   failed. This matters in projects with hundreds of files.
9. **Say when it's done.** Show a notification or a dock/taskbar badge when a long build or verification ends
   ("zeta5 built in 38 min: 0 errors, 1 warning"), and put the window's progress on the dock icon on macOS
   and the taskbar on Windows.
10. **Don't let the Lean server and the build fight.** The server was "ready" while `lake build` ran. Opening a
    file during the build makes the server elaborate against half-built imports. Either wait, or say "these
    imports are still being built; results will follow".
11. **Tenet's progress: good, but hidden.** The Tenet panel showed a real bar, `Apery.Table.U55 (145/213) 29/204`
    (module 145 of 213, declaration 29 of 204). The whole re-check took 6 min 46 s for 213 modules and 14,951
    declarations. What's missing: time left, and the same progress in the status bar and on the dock icon. If the
    Tenet panel isn't the open tab, nothing says verification is running.

## Also found on the way

- **Proof Steps says a `sorry` finished the proof.** In Challenge.lean, whose proof is `sorry`, Proof Steps showed
  `sorry` with "✓ done: goals accomplished". Lean closes the goal, but the panel should say "closed by sorry" in
  the warning colour, not the success one. (The ✔ proof-end mark is correct: Lean sends no "Goals accomplished"
  for a `sorry` proof.)
- **Fixed during this run: a name declared in two modules.** zeta5 has `RiemannZetaValues.irrational_five` in both
  Challenge.lean (with `sorry`) and Solution.lean (the proof), in modules that don't import each other. Tenet's
  report resolved names with no module in scope, so both were missing from it ("0 resting on sorry"), and
  `AxiomsOf` gave `[]` for the name. That looked like "no axioms". Now each module's declarations are read from
  that module, a name the project declares twice is resolved as the module reading it sees it, `AxiomsOf` or
  `Details` without a module refuses an ambiguous name, and an unknown name throws instead of returning `[]`.
  There is a test: `TenetKeepsAChallengeAndItsSolutionApart`.
- **206 build warnings (mostly deprecations: `if_pos` → `ite_eq_left`) in Problems.** Grouping them by kind
  ("205 × deprecated `if_pos`…") would make a big project's Problems list readable.

## How to check it

`LeanStudio.Snapshot --validate <project> <out> [theorem…]` drives exactly this flow and saves screenshots. Run
it on zeta5 or another large project before and after, and compare the screenshots.
