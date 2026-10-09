using CommunityToolkit.Mvvm.Input;
using LeanStudio.App.Services;
using LeanStudio.App.ViewModels;
using LeanStudio.App.Views;

namespace LeanStudio.Snapshot;

/// <summary>
/// The text, Mathlib and Git commands of the Lean menu, run in the real (headless) window on real documents:
/// <c>LeanStudio.Snapshot --commands</c>. Needs git but not Lean, so it runs anywhere the app builds.
/// </summary>
internal static class CommandChecks
{
    private static int _failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok)
        {
            _failures++;
        }
    }

    public static async Task<int> RunAsync()
    {
        string dir = Directory.CreateTempSubdirectory("leanstudio-commands-").FullName;
        try
        {
            await Git(dir, "init", "-q", "-b", "main");
            string path = Path.Combine(dir, "A.lean");
            await File.WriteAllTextAsync(path, "theorem a (n : Nat) : n + 0 = n := by sorry\ntheorem b (n : Nat) : n = n := by sorry\n");
            await Git(dir, "add", "-A");
            await Git(dir, "-c", "user.name=Ada Lovelace", "-c", "user.email=a@l", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "Two sorries");
            await File.WriteAllTextAsync(path, "theorem a (n : Nat) : n + 0 = n := by simp\ntheorem b (n : Nat) : n = n := by sorry\n");
            await Git(dir, "-c", "user.name=Ada Lovelace", "-c", "user.email=a@l", "-c", "commit.gpgsign=false", "commit", "-q", "-am", "Prove a");
            await Git(dir, "config", "user.name", "Ada Lovelace");

            var window = new MainWindow(new Settings()) { Width = 1200, Height = 800 };
            window.Taskbar.UseNative = false;
            window.OpenOnStartup = dir;
            window.Show();
            MainViewModel vm = window.ViewModel;
            Check(await WaitFor(() => vm.Project is not null, 30), "the project opens");
            DocumentViewModel? doc = await vm.OpenFileAsync(path);
            Check(doc is not null, "a Lean file opens");
            if (doc is null)
            {
                return 1;
            }

            string Text() => doc.Document.Text;
            void Set(string text) => doc.Document.Text = text;
            string Output() => vm.Output.Text;
            async Task Run(IRelayCommand command)
            {
                if (command is IAsyncRelayCommand async)
                {
                    await async.ExecuteAsync(null);
                }
                else
                {
                    command.Execute(null);
                }
            }

            Console.WriteLine("sort imports");
            Set("import B\nimport A\nimport B\n\ndef x := 1\n");
            await Run(vm.SortImportsCommand);
            Check(Text() == "import A\nimport B\n\ndef x := 1\n", "Sort Imports orders and de-duplicates the header");
            doc.Document.UndoStack.Undo();
            Check(Text() == "import B\nimport A\nimport B\n\ndef x := 1\n", "and one undo brings the old order back");

            Console.WriteLine("whitespace and style");
            Set("def a := 1  \r\n\tdef b := 2\r\n\r\n\r\n");
            await Run(vm.TidyWhitespaceCommand);
            Check(Text() == "def a := 1\n  def b := 2\n", "Tidy Whitespace fixes trailing spaces, tabs, CRLF and the final newlines");
            Set("import B\nimport A \n\ndef x := 1\t");
            await Run(vm.TidyFileCommand);
            Check(Text() == "import A\nimport B\n\ndef x := 1\n", "Tidy File sorts the imports and fixes the whitespace in one edit");
            Set("-- " + string.Join(' ', Enumerable.Repeat("word", 40)) + "\n");
            await Run(vm.WrapLongCommentsCommand);
            Check(Text().Split('\n').All(l => l.Length <= 100) && Text().Contains("\n-- word", StringComparison.Ordinal), "Wrap Long Comment Lines breaks a long comment");

            Console.WriteLine("junk values");
            Set("noncomputable def n_e : Nat := sInf {n : Nat | n * n = 2}\n\ndef first (xs : List Nat) : Nat := xs.head!\n");
            await Run(vm.FindJunkValuesCommand);
            Check(Output().Contains("`sInf` returns a made-up value", StringComparison.Ordinal) && Output().Contains("head!", StringComparison.Ordinal),
                "Find Junk Values lists sInf of a possibly empty set and head!");

            Console.WriteLine("docstring claims");
            Set("/-- Equivalent to `Irreducible` in Mathlib. -/\ndef MyIrred (x : Nat) : Prop := True\n");
            await Run(vm.CheckDocstringsCommand);
            Check(Output().Contains("no theorem in the scope relates them", StringComparison.Ordinal) && Output().Contains("`Irreducible`", StringComparison.Ordinal),
                "Check Docstrings Against Definitions lists a claim of equivalence no theorem backs up");

            Console.WriteLine("proof clean-ups");
            Set("theorem t : P := by\n  intro x\n  intro y\n  rw [a]\n  rw [b]\n  exact h\n");
            await Run(vm.MergeConsecutiveTacticsCommand);
            Check(Text() == "theorem t : P := by\n  intro x y\n  rw [a, b]\n  exact h\n", "Merge Consecutive rw / intro Steps merges them");
            Set("@[deprecated (since := \"2020-01-01\")] alias old := new\n\ndef new := 1\n");
            await Run(vm.RemoveStaleDeprecationsCommand);
            Check(Text() == "def new := 1\n", "Remove Deprecations Older Than Six Months deletes the old alias");

            Console.WriteLine("Mathlib conventions");
            Set("import A\n");
            await Run(vm.AddMathlibHeaderCommand);
            Check(Text().StartsWith("/-\nCopyright (c) ", StringComparison.Ordinal) && Text().Contains(" Ada Lovelace. All rights reserved.\n", StringComparison.Ordinal)
                && Text().EndsWith("-/\nimport A\n", StringComparison.Ordinal), "Add Mathlib Copyright Header writes the header with the git user's name");
            string before = Text();
            await Run(vm.AddMathlibHeaderCommand);
            Check(Text() == before, "and leaves a file that starts with a comment alone");
            Set("theorem a (a b : ℕ) :\n    a + b = b + a := by omega\n");
            doc.Reveal(1, 0);
            await Run(vm.SuggestTheoremNameCommand);
            Check(Output().Contains("`add_comm`", StringComparison.Ordinal), "Suggest a Name for This Theorem says add_comm");

            Console.WriteLine("project and Git");
            await File.WriteAllTextAsync(Path.Combine(dir, "B.lean"), "theorem b2 (n : Nat) : n = n := by simp\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "C.lean"), "theorem c2 (k : Nat) : k = k := by simp\ndef undocumented := 1\n");
            await Run(vm.ShowProjectHealthCommand);
            Check(Output().Contains("## Project health", StringComparison.Ordinal) && Output().Contains("sorry", StringComparison.Ordinal), "Project Health Summary reports into Output");
            await Run(vm.FindDuplicateStatementsCommand);
            Check(Output().Contains("Same statement:", StringComparison.Ordinal) && Output().Contains("b2", StringComparison.Ordinal) && Output().Contains("c2", StringComparison.Ordinal),
                "Find Duplicate Theorem Statements finds b2 and c2 stating the same thing");
            await Run(vm.ShowSorryBurndownCommand);
            Check(Output().Contains("Sorries over the last 2 commits: █▁  2 → 1 (−1)", StringComparison.Ordinal) || Output().Contains("2 → 1", StringComparison.Ordinal), "Sorry Burndown draws 2 → 1 from the history");
            await Run(vm.ShowStatementChangesCommand);
            Check(Output().Contains("nothing to compare", StringComparison.Ordinal), "What This Branch Changed Mathematically says there is nothing to compare on main itself");
            await Git(dir, "checkout", "-q", "-b", "topic");
            await File.WriteAllTextAsync(path, "theorem a (n : Nat) : n + 0 = n := by simp\ntheorem b (n : Nat) : n = n := by sorry\ntheorem fresh : True := trivial\n");
            await Git(dir, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "commit", "-q", "-am", "Add fresh");
            await Run(vm.ShowStatementChangesCommand);
            Check(Output().Contains("**Added (1)**", StringComparison.Ordinal) && Output().Contains("`fresh` : True", StringComparison.Ordinal), "and on a branch lists the theorem it added");

            Console.WriteLine("the beginner's onramp");
            Set("def double (n : Nat) : Nat := n + n\n");
            doc.Reveal(0, 0);
            await Run(vm.ExplainDeclarationCommand);
            Check(Output().Contains("In plain words: `double` is a function. Given n (a natural number), it gives back a natural number.", StringComparison.Ordinal), "Explain This Declaration reads a def in plain English");
            await Run(vm.HintForGoalCommand);
            Check(Output().Contains("Hint: put the cursor in a proof", StringComparison.Ordinal), "Hint for This Goal says where to put the cursor when there is no goal");
            Set("lemma f (p : Prop) : p -> p :=\nbegin\n  assume h,\n  exact h,\nend\n");
            await Run(vm.CheckForLean3Command);
            Check(Output().Contains("line 2: begin:", StringComparison.Ordinal) && Output().Contains("line 3: assume:", StringComparison.Ordinal) && Output().Contains("Lean 3: ", StringComparison.Ordinal), "Is This Lean 3? finds begin and assume and says what they are now");
            Set("#eval 3 - 5\n");
            await Run(vm.ShowGotchasCommand);
            Check(Output().Contains("line 1: 3 - 5 is 0, not -2.", StringComparison.Ordinal), "Surprises in This File explains 3 - 5");
            Set("theorem t (p : Prop) : p → p := by\n  intro h\n  exact h\n");
            await Run(vm.MakeCheatSheetCommand);
            Check(Output().Contains("# My Lean cheat sheet", StringComparison.Ordinal) && Output().Contains("### `intro`", StringComparison.Ordinal) && Output().Contains("### `exact`", StringComparison.Ordinal), "My Cheat Sheet explains the tactics the file uses");

            await Run(vm.PracticeSymbolsCommand);
            DocumentViewModel? practice = vm.ActiveDocument;
            Check(practice is not null && Path.GetFileName(practice.Path) == "SymbolPractice.lean" && practice.Document.Text.Contains("(type \\", StringComparison.Ordinal), "Practice Typing Symbols opens a worksheet");
            if (practice is not null)
            {
                string answered = practice.Document.Text;
                foreach (var sym in LeanStudio.Core.Learn.SymbolTrainer.Symbols)
                {
                    answered = answered.Replace($"(type \\{sym.Abbreviation})  →  ", $"(type \\{sym.Abbreviation})  →  {sym.Symbol}", StringComparison.Ordinal);
                }
                practice.Document.Text = answered;
                await Run(vm.CheckSymbolPracticeCommand);
                Check(Output().Contains("Symbols: 8 of 8 right. Well done.", StringComparison.Ordinal), "Check My Symbol Practice marks every answer right");
            }

            await Run(vm.NewPuzzleCommand);
            DocumentViewModel? puzzle = vm.ActiveDocument;
            Check(puzzle is not null && Path.GetFileName(puzzle.Path).StartsWith("Puzzle", StringComparison.Ordinal) && puzzle.Document.Text.Contains("sorry", StringComparison.Ordinal), "New Puzzle opens a puzzle with a sorry to replace");
            await Run(vm.PuzzleHintCommand);
            await Run(vm.PuzzleHintCommand);
            Check(Output().Contains("hint 1 of 3:", StringComparison.Ordinal) && Output().Contains("hint 2 of 3:", StringComparison.Ordinal), "Puzzle Hint gives a bigger hint each time");
            await Run(vm.ShowPuzzleSolutionCommand);
            Check(Output().Contains("one solution: replace `sorry` with", StringComparison.Ordinal), "Show Puzzle Solution shows the answer");

            await Run(vm.ShowBadgesCommand);
            Check(Output().Contains(" badges", StringComparison.Ordinal) && Output().Contains("Next: ", StringComparison.Ordinal), "My Badges and What's Next lists the badges and what to try next");
            vm.ShowRosettaCommand.Execute("python");
            Check(Output().Contains("## Coming from Python", StringComparison.Ordinal) && Output().Contains("IO.println \"hi\"", StringComparison.Ordinal), "Coming From Python writes the table");
            await Run(vm.ShowTipCommand);
            Check(Output().Contains("Tip of the day: ", StringComparison.Ordinal), "Tip of the Day shows a tip");

            Console.WriteLine("big projects");
            Directory.CreateDirectory(Path.Combine(dir, "Proj"));
            await File.WriteAllTextAsync(Path.Combine(dir, "Proj", "Basic.lean"), "theorem base : 1 = 1 := rfl\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "Proj", "Mid.lean"), "import Proj.Basic\ntheorem mid : 2 = 2 := by\n  have := base\n  sorry\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "Proj", "Main.lean"), "import Proj.Mid\ntheorem main_thm2 : 3 = 3 := by\n  have := mid\n  rfl\n");
            await Run(vm.ShowNextUpCommand);
            Check(Output().Contains("can be proved now: every theorem they use is already fully proved", StringComparison.Ordinal) && Output().Contains("mid  (Proj/Mid.lean:2): unblocks 1", StringComparison.Ordinal), "Sorries to Prove Next lists the sorry nothing stands in front of");
            await Run(vm.ShowMostBlockingCommand);
            Check(Output().Contains("held up  mid  (Proj/Mid.lean:2)  ← can start now", StringComparison.Ordinal), "Sorries Blocking the Most ranks it");
            await ((IAsyncRelayCommand<string>)vm.ShowWorkPackagesCommand).ExecuteAsync("3");
            Check(Output().Contains("for 3 people", StringComparison.Ordinal), "Share Out the Work for 3 People divides the ready work");
            await Run(vm.ShowSorryAgeCommand);
            Check(Output().Contains("from git blame", StringComparison.Ordinal), "Oldest Sorries reads git blame");
            await Run(vm.ShowForecastCommand);
            Check(Output().Contains("Too little history to say", StringComparison.Ordinal), "When Will the Sorries Run Out? says when the history is too short");
            await Run(vm.ShowCriticalPathCommand);
            Check(Output().Contains("modules in a row", StringComparison.Ordinal) && Output().Contains("Proj.Basic → Proj.Mid → Proj.Main", StringComparison.Ordinal), "Build Critical Path finds the chain");
            await Run(vm.ShowLongProofsCommand);
            Check(Output().Contains("The longest declarations", StringComparison.Ordinal), "Longest Proofs lists them");
            vm.ActiveDocument = doc; // the puzzle opened above is active
            Set("theorem one : True := trivial\ntheorem two : True := trivial\n");
            await Run(vm.ShowSplitAdviceCommand);
            Check(Output().Contains("there is no clean place to split it", StringComparison.Ordinal), "Where to Split This File says when a file is small");
            await File.WriteAllTextAsync(Path.Combine(dir, "Lock.lean"), "theorem main_thm (n : Nat) : n = n := rfl\n");
            Set("theorem main_thm (n : Nat) : n = n := rfl\n");
            doc.Reveal(0, 0);
            await Run(vm.LockStatementCommand);
            Check(Output().Contains("Locked `main_thm`: (n : Nat) : n = n", StringComparison.Ordinal), "Lock This Theorem's Statement locks it");
            await Run(vm.CheckLockedStatementsCommand);
            Check(Output().Contains("Locked statements: 1 of 1 unchanged.", StringComparison.Ordinal), "Check Locked Statements finds it unchanged");
            await File.WriteAllTextAsync(Path.Combine(dir, "Lock.lean"), "theorem main_thm (n : Nat) : n ≤ n := Nat.le_refl n\n");
            await Run(vm.CheckLockedStatementsCommand);
            Check(Output().Contains("CHANGED  main_thm", StringComparison.Ordinal), "and reports it when the statement has been weakened");
            await Run(vm.CheckLayersCommand);
            Check(Output().Contains("No layers are written down", StringComparison.Ordinal), "Check Module Layers says how to write the layers");
            Directory.CreateDirectory(Path.Combine(dir, ".leanstudio"));
            await File.WriteAllTextAsync(Path.Combine(dir, ".leanstudio", "layers.json"), "{ \"layers\": [[\"Proj.Mid\"], [\"Proj.Basic\"], [\"Proj.Main\"]] }");
            await Run(vm.CheckLayersCommand);
            Check(Output().Contains("Proj/Mid.lean:1  Proj.Mid (layer 1) imports Proj.Basic (layer 2)", StringComparison.Ordinal), "and finds an import that reaches up");

            Console.WriteLine("share");
            await Run(vm.CopyForZulipCommand);
            Check(Output().Contains("Copied this file as a Zulip message", StringComparison.Ordinal), "Copy as a Zulip Message copies the file");

            Console.WriteLine("tenet");
            // A Lake project of its own, built, so Tenet has something to read.
            string lake = Path.Combine(dir, "Built");
            Directory.CreateDirectory(lake);
            await File.WriteAllTextAsync(Path.Combine(lake, "lean-toolchain"), "leanprover/lean4:v4.34.0\n");
            await File.WriteAllTextAsync(Path.Combine(lake, "lakefile.toml"), "name = \"Built\"\ndefaultTargets = [\"Built\"]\n\n[[lean_lib]]\nname = \"Built\"\n");
            string built = Path.Combine(lake, "Built.lean");
            await File.WriteAllTextAsync(built, "def double (n : Nat) : Nat := n + n\n\ndef helper (n : Nat) : Nat := double n + 1\n\ntheorem double_eq (n : Nat) : double n = 2 * n := by unfold double; omega\n");
            await vm.CloseAllAsync(force: true); // the edits above stay unsaved; a prompt about them would wait forever here
            await vm.OpenProjectAsync(lake);
            Check(await WaitFor(() => vm.Project?.Root == lake, 30), "a Lake project opens");
            await Run(vm.BuildCommand);
            DocumentViewModel? source = await vm.OpenFileAsync(built);
            Check(source is not null, "its file opens");
            if (source is not null)
            {
                source.Reveal(0, 5);
                await Run(vm.ProvedAboutAtCaretCommand);
                Check(Output().Contains("1 theorem states something about `double`", StringComparison.Ordinal)
                    && Output().Contains("It says that for any n (a natural number), double n equals 2 * n.", StringComparison.Ordinal),
                    "What's Proved About This? reads the theorem about double in plain English");
                await Run(vm.ShowSpecCoverageCommand);
                Check(Output().Contains("1 of 2 definitions have a theorem whose statement mentions them", StringComparison.Ordinal) && Output().Contains("`helper`", StringComparison.Ordinal),
                    "What the Theorems Are About finds helper with no theorem");
            }
        }
        finally
        {
            // The window may still be writing into the folder (Verify After Every Build writes Tenet's cache once the
            // check finishes): try again for a few seconds, and leave a temporary folder behind rather than fail.
            for (int attempt = 1; Directory.Exists(dir); attempt++)
            {
                try
                {
                    foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(f, FileAttributes.Normal);
                    }
                    Directory.Delete(dir, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 20)
                    {
                        Console.WriteLine($"  note: could not delete {dir}: {e.Message}");
                        break;
                    }
                    await Task.Delay(250);
                }
            }
        }
        return _failures;
    }

    private static async Task Git(string dir, params string[] args)
    {
        var result = await LeanStudio.Core.Processes.ProcessRunner.RunAsync("git", args, dir);
        if (!result.Success)
        {
            throw new InvalidOperationException("git " + string.Join(' ', args) + ": " + result.Output);
        }
    }

    private static async Task<bool> WaitFor(Func<bool> condition, double seconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed.TotalSeconds > seconds)
            {
                return false;
            }
            await Task.Delay(50);
        }
        return true;
    }
}
