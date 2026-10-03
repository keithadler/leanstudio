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

            Console.WriteLine("share");
            await Run(vm.CopyForZulipCommand);
            Check(Output().Contains("Copied this file as a Zulip message", StringComparison.Ordinal), "Copy as a Zulip Message copies the file");
        }
        finally
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(dir, true);
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
