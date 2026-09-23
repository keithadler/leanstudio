using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using LeanStudio.App;
using LeanStudio.App.Services;
using LeanStudio.App.ViewModels;
using LeanStudio.App.Views;

// Usage: LeanStudio.Snapshot <repo root> <output dir>
// Opens the Proofs sample in a real (headless) Lean Studio window with a real Lean server, walks through the main
// features, and saves a screenshot after each. Exits non-zero if an expectation fails.

string repo = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
string outDir = Path.GetFullPath(args.Length > 1 ? args[1] : "snapshots");
Directory.CreateDirectory(outDir);
string settingsDir = Path.Combine(Path.GetTempPath(), "leanstudio-snapshot-" + Environment.ProcessId);
Environment.SetEnvironmentVariable("LEANSTUDIO_SETTINGS_DIR", settingsDir);
// Its own bridge pipe, so a Lean Studio the person has open is left alone.
Environment.SetEnvironmentVariable("LEANSTUDIO_PIPE", "leanstudio-snapshot-" + Environment.ProcessId);
// The tutorial and playground go in a temporary folder, not the person's Documents.
Environment.SetEnvironmentVariable("LEANSTUDIO_HOME", Path.Combine(settingsDir, "home"));

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

int failures = 0;
var done = new CancellationTokenSource();
Dispatcher.UIThread.Post(async () =>
{
    try
    {
        failures = await Scenario.RunAsync(repo, outDir);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine(e);
        failures = 99;
    }
    finally
    {
        done.Cancel();
    }
});
Dispatcher.UIThread.MainLoop(done.Token);
try
{
    Directory.Delete(settingsDir, true);
}
catch (IOException)
{
}
Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} check(s) failed");
return failures == 0 ? 0 : 1;

internal static class Scenario
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

    private static async Task<bool> WaitFor(Func<bool> condition, double seconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed.TotalSeconds > seconds)
            {
                return false;
            }
            await Task.Delay(100);
        }
        return true;
    }

    private static void Snap(Window w, string dir, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        string path = Path.Combine(dir, name + ".png");
#pragma warning disable CS0618 // the replacement overload needs encoder options this tool has no use for
        frame?.Save(path);
#pragma warning restore CS0618
        Console.WriteLine("  saved " + path);
    }

    public static async Task<int> RunAsync(string repo, string outDir)
    {
        var settings = new Settings();
        var window = new MainWindow(settings) { Width = 1440, Height = 900 };
        window.OpenOnStartup = Path.Combine(repo, "samples", "Proofs");
        window.Show();
        MainViewModel vm = window.ViewModel;

        Console.WriteLine("open project");
        Check(await WaitFor(() => vm.Project is not null && vm.ServerStatus == "Lean: ready", 60), "the project opens and Lean starts");
        Button? credit = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "XLink");
        Check(credit?.Content as string == "@keithadler on X" && credit.IsEffectivelyVisible, "the welcome screen credits @keithadler on X");
        Check(Credits.XUrl == "https://x.com/keithadler" && Credits.Author == "Keith Adler", "the About box credits Keith Adler and links x.com/keithadler");
        Snap(window, outDir, "01-welcome");

        Console.WriteLine("open a file");
        DocumentViewModel? doc = await vm.OpenFileAsync(Path.Combine(repo, "samples", "Proofs", "Proofs", "Basic.lean"));
        Check(doc is not null, "Basic.lean opens");
        Check(await WaitFor(() => doc!.Diagnostics.Count > 0 && !doc.IsProcessing, 90), "Lean elaborates it and reports diagnostics");
        Check(doc!.Diagnostics.Any(d => d.Message.Contains("sorry", StringComparison.Ordinal)), "the sorry is reported");

        Console.WriteLine("goals at the cursor");
        doc.Reveal(16, 14); // on `exact` in `| inl hp => exact hp`
        Check(await WaitFor(() => vm.Info.HasGoals, 30), "the tactic state shows goals");
        Check(vm.Info.Goals.Any(g => g.Hypotheses.Any(h => h.Names == "hp")), "hypothesis hp is listed");
        Check(await WaitFor(() => vm.Info.HasSteps && vm.Info.Steps.All(s => s.GoalsAfter >= 0), 30), "every proof step has a state");
        Snap(window, outDir, "02-goals");

        Console.WriteLine("an AI assistant asks the window what the person is looking at");
        System.Text.Json.Nodes.JsonObject? context = await LeanStudio.Core.Agents.StudioBridge.RequestAsync(new System.Text.Json.Nodes.JsonObject { ["method"] = "context" });
        Check(context?["file"]?.GetValue<string>() == doc.Path, "the bridge reports the open file");
        Check(context?["line"]?.GetValue<int>() == 17, "and the cursor line (1-based)");
        Check(context?["goals"]?.GetValue<string>().Contains("hp : p", StringComparison.Ordinal) == true, "and the goals at the cursor");
        System.Text.Json.Nodes.JsonObject? shown = await LeanStudio.Core.Agents.StudioBridge.RequestAsync(new System.Text.Json.Nodes.JsonObject
        {
            ["method"] = "show", ["path"] = doc.Path, ["line"] = 5, ["column"] = 3,
        });
        Check(shown?["ok"]?.GetValue<bool>() == true && doc.CaretLine == 4, "an assistant can move the person's cursor to a line");

        Console.WriteLine("a file changed on disk by an assistant reloads");
        string original = doc.SavedText;
        await File.WriteAllTextAsync(doc.Path, original + "\n-- added by an assistant\n");
        Check(await WaitFor(() => doc.Document.Text.Contains("added by an assistant", StringComparison.Ordinal), 10), "the open editor picks up the change");
        await File.WriteAllTextAsync(doc.Path, original);
        Check(await WaitFor(() => doc.Document.Text == original, 10), "and the change back");
        doc.Reveal(16, 14);

        Console.WriteLine("build and verify with Tenet");
        await vm.BuildCommand.ExecuteAsync(null);
        Check(await WaitFor(() => vm.Verification.Report is not null && !vm.Verification.IsRunning, 120), "Tenet produces a report");
        Check(vm.Verification.Report?.Verified >= 3, "at least three declarations verified");
        Check(vm.Verification.Report?.Conditional == 3, "em', not_not_elim and unfinished rest on an assumption");
        Check(doc.Verdicts.ContainsKey(2) && doc.Verdicts.ContainsKey(13), "badges sit on the declaration line, not its doc comment");
        Check(doc.Verdicts.Count >= 5, "the gutter has a verdict for each declaration");
        Snap(window, outDir, "03-tenet");

        Console.WriteLine("declaration navigator");
        vm.SidebarTab = MainViewModel.LibraryTab;
        vm.Navigator.Query = "not_not_elim";
        Check(await WaitFor(() => vm.Navigator.Results.Count > 0, 20), "search finds not_not_elim");
        vm.Navigator.Selected = vm.Navigator.Results[0];
        Check(await WaitFor(() => vm.Navigator.AxiomSummary.Length > 0, 20), "its axioms are computed");
        Check(vm.Navigator.Axioms.Contains("em'"), "and include em'");
        Snap(window, outDir, "04-navigator");

        Console.WriteLine("unicode input");
        TextEditor editor = window.FindControl<LeanStudio.App.Editor.LeanEditor>("Editor")!.TextEditor;
        editor.CaretOffset = editor.Document.TextLength;
        foreach (char c in "\n-- \\alpha \\to \\N")
        {
            editor.TextArea.PerformTextInput(c.ToString());
        }
        editor.TextArea.PerformTextInput(" ");
        string tail = editor.Document.Text[^12..];
        Check(tail.Contains("α → ℕ", StringComparison.Ordinal), $"\\alpha \\to \\N became α → ℕ (got '{tail.Trim()}')");
        doc.Document.Text = doc.SavedText;
        doc.Document.UndoStack.MarkAsOriginalFile();

        Console.WriteLine("editing: brackets and indentation");
        editor.CaretOffset = editor.Document.TextLength;
        foreach (char c in "\nexample : True ∧ True := by")
        {
            editor.TextArea.PerformTextInput(c.ToString());
        }
        editor.TextArea.PerformTextInput("\n");
        string afterBy = editor.Document.GetText(editor.Document.GetLineByNumber(editor.Document.LineCount));
        Check(afterBy == "  ", $"Enter after `:= by` indents two spaces (got '{afterBy}')");
        foreach (char c in "exact \\<")
        {
            editor.TextArea.PerformTextInput(c.ToString());
        }
        editor.TextArea.PerformTextInput(" ");
        string lastLine = editor.Document.GetText(editor.Document.GetLineByNumber(editor.Document.LineCount));
        Check(lastLine.Contains("⟨⟩", StringComparison.Ordinal), $"\\< becomes ⟨ and closes itself (got '{lastLine.Trim()}')");
        editor.TextArea.PerformTextInput("(");
        lastLine = editor.Document.GetText(editor.Document.GetLineByNumber(editor.Document.LineCount));
        Check(lastLine.Contains("⟨()⟩", StringComparison.Ordinal), $"( closes itself inside ⟨⟩ (got '{lastLine.Trim()}')");
        editor.TextArea.PerformTextInput(")");
        lastLine = editor.Document.GetText(editor.Document.GetLineByNumber(editor.Document.LineCount));
        Check(lastLine.Contains("⟨()⟩", StringComparison.Ordinal) && !lastLine.Contains("))", StringComparison.Ordinal), "typing ) steps over the closer instead of doubling it");
        doc.Document.Text = doc.SavedText;
        doc.Document.UndoStack.MarkAsOriginalFile();

        Console.WriteLine("outline, references, find in files");
        vm.SidebarTab = MainViewModel.OutlineTab;
        Check(await WaitFor(() => vm.Outline.Any(o => o.Name == "not_not_elim"), 20), "the outline lists the file's declarations");
        Check(vm.Outline.Any(o => o.Name == "em'" && o.Kind == "axiom"), "and knows an axiom from a theorem");
        doc.Reveal(1, 5); // on `double` in its definition
        await Task.Delay(300);
        await vm.FindReferencesCommand.ExecuteAsync(null);
        Check(vm.References.Count >= 2, $"references to double are found ({vm.References.Count})");
        vm.SearchQuery = "and_swap";
        await vm.SearchInFilesCommand.ExecuteAsync(null);
        Check(vm.SearchResults.Any(r => r.File == "Basic.lean"), "find in files finds and_swap");
        Snap(window, outDir, "06-outline");

        Console.WriteLine("Try this: apply Lean's suggestion");
        string tryFile = Path.Combine(repo, "samples", "Proofs", "Proofs", "TryThis.lean");
        await File.WriteAllTextAsync(tryFile, "theorem two : 1 + 1 = 2 := by\n  exact?\n");
        try
        {
            DocumentViewModel? t = await vm.OpenFileAsync(tryFile);
            Check(await WaitFor(() => t!.Diagnostics.Any(d => d.Message.Contains("Try this", StringComparison.Ordinal)), 90), "exact? offers a suggestion");
            t!.Reveal(1, 4);
            bool offered = await WaitFor(() => vm.Info.Messages.Any(m => m.HasSuggestions), 30);
            Check(offered, "the tactic state shows it as a button");
            await Task.Delay(300);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            string buttonText = window.GetVisualDescendants().OfType<Button>()
                .Select(b => (b.Content as TextBlock)?.Text ?? "").FirstOrDefault(x => x.StartsWith("Try this", StringComparison.Ordinal)) ?? "";
            Check(buttonText.Contains('_', StringComparison.Ordinal), $"the button shows the name with its underscore ({buttonText})");
            Snap(window, outDir, "07-try-this");
            MessageView m = vm.Info.Messages.First(m => m.HasSuggestions);
            vm.Info.ApplySuggestionCommand.Execute(m.Suggestions[0]);
            Check(await WaitFor(() => !t.Document.Text.Contains("exact?", StringComparison.Ordinal), 10), "clicking it replaces exact? with the proof");
            Check(await WaitFor(() => t.Diagnostics.Count == 0 && !t.IsProcessing, 60), "and Lean accepts the result");
            await vm.CloseDocumentCommand.ExecuteAsync(t);
        }
        finally
        {
            File.Delete(tryFile);
        }

        Console.WriteLine("git");
        vm.ActiveDocument = doc;
        doc.Reveal(0, 0);
        Check(await WaitFor(() => !vm.Info.HasSteps, 10), "outside a proof, no proof steps are shown");
        vm.SidebarTab = MainViewModel.GitTab;
        await vm.SourceControl.RefreshAsync();
        Check(vm.SourceControl.IsRepository && vm.SourceControl.Branch.Length > 0, $"source control finds the repository and branch ({vm.SourceControl.Branch})");
        Check(vm.BranchLabel.StartsWith("⎇", StringComparison.Ordinal), "the status bar shows the branch");
        Check(vm.SourceControl.GitHubRepository == "keithadler/leanstudio" || vm.SourceControl.GitHubRepository is null, "and recognises a GitHub remote when there is one");
        Snap(window, outDir, "08-git");

        Console.WriteLine("for newcomers");
        vm.ActiveDocument = doc;
        Check(await WaitFor(() => vm.Outline.Any(o => o.Name == "and_swap" && o.Status == "✓") && vm.Outline.Any(o => o.Name == "unfinished" && o.Status == "◐"), 20),
            "the outline marks proved theorems ✓ and unfinished ones ◐");
        doc.Reveal(8, 2); // inside and_swap
        Check(await WaitFor(() => vm.Info.Goals.Any(g => g.English.StartsWith("In words:", StringComparison.Ordinal)), 20), "goals are read in plain English");
        Console.WriteLine("    " + vm.Info.Goals.First().English);

        vm.SidebarTab = MainViewModel.LearnTab;
        await vm.Learn.StartTutorialCommand.ExecuteAsync(null);
        DocumentViewModel? lesson = vm.ActiveDocument;
        Check(lesson?.Path.EndsWith("01_Hello.lean", StringComparison.Ordinal) == true, "the tutorial opens lesson 1");
        Check(await WaitFor(() => lesson!.Diagnostics.Any(d => d.Message.Contains("sorry", StringComparison.Ordinal)) && !lesson.IsProcessing, 90), "the lesson's exercises show as unfinished");
        Check(lesson!.Diagnostics.Any(d => d.Severity == LeanStudio.Lsp.DiagnosticSeverity.Information && d.Message.Trim() == "4"), "#eval 2 + 2 gives 4 (shown at the end of its line)");
        MessageView? explained = null;
        lesson.Reveal(lesson.Document.Text.Split('\n').ToList().FindIndex(l => l.StartsWith("def addOne", StringComparison.Ordinal)), 5);
        Check(await WaitFor(() => (explained = vm.Info.Messages.FirstOrDefault(m => m.HasExplanation)) is not null, 20), "the sorry warning is explained in plain words");
        Snap(window, outDir, "09-tutorial");
        lesson.Document.Text = LeanStudio.Core.Learn.Tutorial.Solved(LeanStudio.Core.Learn.Tutorial.Lessons[0]);
        Check(await WaitFor(() => vm.Learn.Lessons[0].Done, 90), "solving it ticks lesson 1 off");
        Check(vm.Learn.Progress.StartsWith("1 of 10", StringComparison.Ordinal), $"and the progress says so ({vm.Learn.Progress})");
        await lesson.SaveAsync();

        await vm.Learn.OpenPlaygroundCommand.ExecuteAsync(null);
        DocumentViewModel? play = vm.ActiveDocument;
        Check(play?.Path.EndsWith("Playground.lean", StringComparison.Ordinal) == true, "the playground opens");
        vm.Learn.InsertSymbolCommand.Execute(vm.Learn.SymbolGroups[0].Symbols[0]);
        Check(play!.Document.Text.Contains('∀', StringComparison.Ordinal), "a click in the symbol palette inserts ∀");
        play.Document.Text = play.Document.Text.Replace("∀", "", StringComparison.Ordinal);
        window.FindControl<LeanStudio.App.Editor.LeanEditor>("Editor")!.InsertSnippet(LeanStudio.Core.Learn.Snippets.All.Single(s => s.Name == "Program with main"));
        await play.SaveAsync();
        Check(await WaitFor(() => vm.CanRun, 10), "a file with main gets a Run button");
        await vm.RunProgramCommand.ExecuteAsync(null);
        Check(vm.Output.Text.Contains("Hello from Lean!", StringComparison.Ordinal), "▶ Run shows the program's output");
        vm.Learn.SelectedTheorem = vm.Learn.Theorems.First(t => t.LeanName == "Nat.add_comm");
        await vm.Learn.TryTheoremCommand.ExecuteAsync(null);
        Check(play.Document.Text.Contains("#print axioms Nat.add_comm", StringComparison.Ordinal), "a famous theorem lands in the open playground");
        Check(await WaitFor(() => play.Diagnostics.Any(d => d.Message.Contains("'Nat.add_comm' depends on axioms", StringComparison.Ordinal)
            || d.Message.Contains("'Nat.add_comm' does not depend on any axioms", StringComparison.Ordinal)), 60), "and Lean shows its statement and what it rests on");
        vm.BottomTab = MainViewModel.OutputPanel;
        await Task.Delay(300);
        Check(((TextEditor)window.FindControl<LeanStudio.App.Editor.OutputView>("OutputView")!.Content!).TextArea.TextView.VisualLines.Count > 3, "the Output panel shows its latest lines");
        Snap(window, outDir, "10-playground");

        vm.SidebarTab = MainViewModel.ToolchainsTab;
        await vm.Toolchains.RefreshAsync();
        Check(vm.Toolchains.Installed.Count > 0, "installed toolchains are listed");
        Snap(window, outDir, "05-toolchains");

        await vm.DisposeAsync();
        return _failures;
    }
}
