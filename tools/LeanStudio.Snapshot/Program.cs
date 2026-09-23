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
        Check(shown?["ok"]?.GetValue<bool>() == true && await WaitFor(() => doc.CaretLine == 4, 5), "an assistant can move the person's cursor to a line");

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
        doc.ReplaceAll(doc.SavedText);

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
        doc.ReplaceAll(doc.SavedText);

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
            await t.SaveAsync(); // closing unsaved work would (rightly) ask first, and nobody is here to answer
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

        Console.WriteLine("day-to-day workbench");
        await vm.RefreshMarkersCommand.ExecuteAsync(null);
        MarkerItem? unfinished = vm.Markers.FirstOrDefault(m => m.Declaration == "unfinished");
        Check(unfinished is not null && vm.MarkersTitle.StartsWith("Sorries 1", StringComparison.Ordinal), $"the Sorries panel finds the sorry in unfinished ({vm.MarkersTitle})");
        vm.BottomTab = MainViewModel.MarkersPanel;
        await vm.OpenMarkerCommand.ExecuteAsync(unfinished);
        Check(await WaitFor(() => vm.Info.Goals.Any(g => g.Target.Contains("a * b = b * a", StringComparison.Ordinal)), 20), "clicking it shows the goal left at that sorry");
        vm.Info.PinCommand.Execute(null);
        Check(vm.Info.HasPinned && vm.Info.Pinned[0].Text.Contains("a * b = b * a", StringComparison.Ordinal), "the goal can be pinned");
        bool blamed = await WaitFor(() => vm.BlameText.Length > 0, 10);
        Check(blamed, $"the status bar says who last changed the line ({vm.BlameText})");
        Snap(window, outDir, "11-workbench");
        vm.Info.UnpinCommand.Execute(vm.Info.Pinned[0]);

        string savedBasic = doc.SavedText;
        vm.Settings.AutoSave = true;
        doc.Document.Insert(doc.Document.TextLength, "-- autosaved\n");
        bool autosaved = await WaitFor(() => !doc.IsDirty && File.ReadAllText(doc.Path).Contains("-- autosaved", StringComparison.Ordinal), 10);
        Check(autosaved, "auto-save writes the file a moment after typing stops");
        doc.Document.Text = savedBasic;
        Check(await WaitFor(() => !doc.IsDirty && File.ReadAllText(doc.Path) == savedBasic, 10), "and again after the change is undone");
        vm.Settings.AutoSave = false;
        var versions = vm.VersionsOfActive();
        Check(versions.Count >= 2 && File.ReadAllText(versions[1].SnapshotFile).Contains("-- autosaved", StringComparison.Ordinal), $"local history kept both versions ({versions.Count})");
        vm.RestoreVersion(versions[1]);
        Check(doc.Document.Text.Contains("-- autosaved", StringComparison.Ordinal) && doc.IsDirty, "an earlier version can be brought back");
        window.FindControl<LeanStudio.App.Editor.LeanEditor>("Editor")!.TextEditor.Undo();
        Check(doc.Document.Text == savedBasic && !doc.IsDirty, "and the restore undone with ⌘Z");
        doc.Document.Text = savedBasic;
        await doc.SaveAsync();

        window.ToggleSidebar();
        window.TogglePanel();
        Check(window.FindControl<Grid>("MainGrid")!.ColumnDefinitions[0].Width.Value == 0 && window.FindControl<Grid>("CenterGrid")!.RowDefinitions[3].Height.Value == 0, "the sidebar and the bottom panel can be hidden");
        Snap(window, outDir, "12-zen");
        window.ToggleSidebar();
        window.TogglePanel();
        Check(window.FindControl<Grid>("MainGrid")!.ColumnDefinitions[0].Width.Value > 0, "and brought back");

        doc.Reveal(14, 7);
        await Task.Delay(200);
        await vm.CloseDocumentCommand.ExecuteAsync(doc);
        doc = (await vm.OpenFileAsync(Path.Combine(repo, "samples", "Proofs", "Proofs", "Basic.lean")))!;
        bool restored = await WaitFor(() => doc.CaretLine == 14 && doc.CaretColumn == 7, 5);
        Check(restored, "a reopened file puts the cursor back where it was");
        DocumentViewModel other = (await vm.OpenFileAsync(Path.Combine(repo, "samples", "Proofs", "Proofs.lean")))!;
        vm.ActiveDocument = doc;
        Check(await WaitFor(() => doc.CaretLine == 14 && doc.CaretColumn == 7, 5), "switching tabs keeps each file's cursor");
        await vm.CloseDocumentCommand.ExecuteAsync(other);

        Check(vm.Tasks().Any(t => t.Title == "lake build") && vm.Tasks().Any(t => t.Title == "lake test"), "the project's tasks are offered");
        await vm.RunTaskAsync(LeanStudio.Core.Workflow.ProjectTasks.Shell("echo workbench-task-ran"));
        Check(vm.Output.Text.Contains("workbench-task-ran", StringComparison.Ordinal), "a shell command runs in the project, output in Output");

        Console.WriteLine("power tools");
        vm.ActiveDocument = doc;
        vm.RightTab = MainViewModel.CodeTab;
        doc.Reveal(1, 5); // on `def double`
        Check(await WaitFor(() => vm.CCode.Contains("l_double", StringComparison.Ordinal), 60), $"the Compiled C tab shows double's C function ({vm.CStatus})");
        Snap(window, outDir, "13-compiled-c");
        doc.Reveal(3, 10); // a theorem
        Check(await WaitFor(() => vm.CStatus.Contains("proofs are erased", StringComparison.Ordinal), 10), "and says a theorem has no code");
        vm.RightTab = MainViewModel.GoalsTab;

        doc.Reveal(16, 14);
        Check(await WaitFor(() => vm.Info.Goals.Count > 0 && vm.Info.Goals[0].TargetTagged is not null, 20), "goals carry Lean's subterm structure");
        LeanStudio.Lsp.TaggedString target = vm.Info.Goals[0].TargetTagged!;
        LeanStudio.Lsp.TaggedSpan whole = target.Spans.MaxBy(sp => sp.Length)!;
        LeanStudio.Lsp.SubtermInfo? info = await vm.Info.InspectAsync(whole.Reference!);
        Check(info?.Type == "Prop", $"hovering a subterm of the goal gives its type ({info?.Type})");

        string proofs = Path.Combine(repo, "samples", "Proofs", "Proofs");
        string proofsDir = proofs;
        string fixes = Path.Combine(proofs, "Fixes.lean"), auto = Path.Combine(proofs, "AutoFix.lean");
        string rep1 = Path.Combine(proofs, "Rep1.lean"), rep2 = Path.Combine(proofs, "Rep2.lean");
        string modA = Path.Combine(proofs, "ModA.lean"), modB = Path.Combine(proofs, "ModB.lean"), modC = Path.Combine(proofs, "ModC.lean");
        try
        {
            await File.WriteAllTextAsync(fixes, "theorem f1 (xs : List Nat) : (xs ++ []).length = xs.length := by\n  simp?\n\ntheorem f2 (n : Nat) : n + 0 = n := by\n  simp?\n");
            DocumentViewModel f = (await vm.OpenFileAsync(fixes))!;
            Check(await WaitFor(() => f.Diagnostics.Count(d => d.Message.StartsWith("Try this", StringComparison.Ordinal)) == 2 && !f.IsProcessing, 90), "two simp? calls each offer a Try this");
            Check((await vm.CodeActionsAtLineAsync(1)).Count > 0, "the lightbulb line has fixes");
            Snap(window, outDir, "14-lightbulbs");
            await vm.FixAllInFileCommand.ExecuteAsync(null);
            Check(!f.Document.Text.Contains("simp?", StringComparison.Ordinal) && f.Document.Text.Split("simp only").Length == 3, "Fix All in File applies both");
            Check(await WaitFor(() => f.Diagnostics.Count == 0 && !f.IsProcessing, 60), "and Lean accepts the result");
            await f.SaveAsync();
            await vm.CloseDocumentCommand.ExecuteAsync(f);

            vm.Settings.AutoApplyFixes = true;
            await File.WriteAllTextAsync(auto, "theorem a1 (n : Nat) : 0 + n = n := by\n  simp?\n");
            DocumentViewModel af = (await vm.OpenFileAsync(auto))!;
            Check(await WaitFor(() => !af.Document.Text.Contains("simp?", StringComparison.Ordinal), 90), "with automatic fixes on, a lone Try this is applied by itself");
            vm.Settings.AutoApplyFixes = false;
            await af.SaveAsync();
            await vm.CloseDocumentCommand.ExecuteAsync(af);

            await File.WriteAllTextAsync(rep1, "def zzOld := 1\n#eval zzOld\n");
            await File.WriteAllTextAsync(rep2, "-- zzOld is referred to here\n");
            vm.SearchQuery = "zzOld";
            vm.ReplaceWith = "zzNew";
            var (matches, files) = await vm.ReplaceInFilesAsync((_, _) => Task.FromResult(true));
            Check(matches == 3 && files == 2 && File.ReadAllText(rep1).Contains("#eval zzNew", StringComparison.Ordinal), $"replace in files changes every match ({matches} in {files})");

            await File.WriteAllTextAsync(modA, "def modA := 1\n");
            await File.WriteAllTextAsync(modB, "import Proofs.ModA\n#eval modA\n");
            await vm.OpenFileAsync(modA);
            string? problem = await vm.RenameModuleAsync("Proofs.ModC");
            Check(problem is null && File.Exists(modC) && !File.Exists(modA) && File.ReadAllText(modB).StartsWith("import Proofs.ModC", StringComparison.Ordinal),
                "renaming a module moves the file and rewrites its imports");
            Check(vm.ActiveDocument?.Path == modC, "and the renamed file stays open");
            await vm.CloseDocumentCommand.ExecuteAsync(vm.ActiveDocument);
        }
        finally
        {
            foreach (string temp in new[] { fixes, auto, rep1, rep2, modA, modB, modC })
            {
                File.Delete(temp);
            }
        }
        vm.ActiveDocument = doc;

        Console.WriteLine("essentials");
        vm.ActiveDocument = doc;
        doc.Reveal(4, 10); // on `double` in `unfold double`
        await Task.Delay(300);
        await vm.GoToDefinitionCommand.ExecuteAsync(null);
        Check(await WaitFor(() => vm.ActiveDocument?.CaretLine == 1, 10), "go to definition lands on def double");
        await vm.GoBackCommand.ExecuteAsync(null);
        Check(await WaitFor(() => vm.ActiveDocument?.CaretLine == 4, 10), "Back returns to where the jump started");
        await vm.GoForwardCommand.ExecuteAsync(null);
        Check(await WaitFor(() => vm.ActiveDocument?.CaretLine == 1, 10), "and Forward goes to the definition again");
        doc.Reveal(0, 0);
        vm.NextProblemCommand.Execute(null);
        Check(await WaitFor(() => doc.CaretLine == 19, 5), "F8 moves to the next problem (the sorry)");

        string scratchA = Path.Combine(proofsDir, "Scratch1.lean"), scratchB = Path.Combine(proofsDir, "Scratch2.lean"), scratchDir = Path.Combine(proofsDir, "ScratchDir");
        string modX = Path.Combine(proofsDir, "ModX.lean"), modY = Path.Combine(proofsDir, "ModY.lean");
        try
        {
            Check(await vm.CreateFileAsync(proofsDir, "Scratch1") is null && File.Exists(scratchA) && vm.ActiveDocument?.Path == scratchA, "the file tree creates and opens a new Lean file");
            Check(await vm.RenamePathAsync(scratchA, "Scratch2.lean") is null && File.Exists(scratchB) && !File.Exists(scratchA) && vm.ActiveDocument?.Path == scratchB, "and renames it, keeping it open");
            Check(vm.CreateFolder(proofsDir, "ScratchDir") is null && Directory.Exists(scratchDir), "and creates folders");
            await vm.CloseDocumentCommand.ExecuteAsync(vm.ActiveDocument);

            await File.WriteAllTextAsync(modX, "def modX := 1\n");
            await File.WriteAllTextAsync(modY, "import Proofs.ModX\n#eval modX\n");
            DocumentViewModel x = (await vm.OpenFileAsync(modX))!;
            DocumentViewModel y = (await vm.OpenFileAsync(modY))!;
            Check(await WaitFor(() => y.Diagnostics.Any(d => d.Message.Trim() == "1"), 120), "a file importing another sees its value");
            vm.ActiveDocument = x;
            x.ReplaceAll("def modX := 2\n");
            await vm.SaveCommand.ExecuteAsync(null);
            vm.ActiveDocument = y;
            bool stale = await WaitFor(() => vm.ImportsStale, 60);
            if (!stale)
            {
                // On a slow runner Lean can miss the first save while it is still busy; save once more.
                vm.ActiveDocument = x;
                await vm.SaveCommand.ExecuteAsync(null);
                vm.ActiveDocument = y;
                stale = await WaitFor(() => vm.ImportsStale, 60);
            }
            Check(stale, "changing an imported file brings up the rebuild banner");
            Snap(window, outDir, "15-stale-imports");
            await vm.RestartFileCommand.ExecuteAsync(null);
            Check(await WaitFor(() => y.Diagnostics.Any(d => d.Message.Trim() == "2") && !vm.ImportsStale, 120), "rebuilding and rechecking picks up the change");
            await vm.CloseDocumentCommand.ExecuteAsync(y);
            await vm.CloseDocumentCommand.ExecuteAsync(x);
        }
        finally
        {
            foreach (string f in new[] { scratchA, scratchB, modX, modY })
            {
                File.Delete(f);
            }
            if (Directory.Exists(scratchDir))
            {
                Directory.Delete(scratchDir, true);
            }
            foreach (string built in Directory.Exists(Path.Combine(repo, "samples", "Proofs", ".lake", "build", "lib", "lean", "Proofs"))
                ? Directory.GetFiles(Path.Combine(repo, "samples", "Proofs", ".lake", "build", "lib", "lean", "Proofs"), "Mod*") : [])
            {
                File.Delete(built);
            }
        }
        vm.ActiveDocument = doc;
        Check(await WaitFor(() => !vm.SourceControl.Unstaged.Any(c => c.FileName is "ModX.lean" or "ModY.lean" or "Scratch2.lean"), 10),
            "the Git panel forgets files that were deleted");

        Console.WriteLine("prove it, why not proved, timing, walkthrough");
        vm.ActiveDocument = doc;
        vm.SidebarTab = MainViewModel.FilesTab;
        doc.Reveal(20, 2); // the sorry in `unfinished` (a * b = b * a)
        await vm.ProveAsync(false);
        SearchResultView? found = vm.Info.Search.Results.FirstOrDefault();
        Check(found?.Result.Best is not null, $"Prove It finds a tactic that proves a * b = b * a ({vm.Info.Search.Status})");
        Console.WriteLine("    closes it: " + string.Join(", ", found?.Result.Successes.Select(t => t.Replacement) ?? []));
        Snap(window, outDir, "16-prove-it");
        if (found?.Shown.FirstOrDefault(t => t.Closes) is TrialView use)
        {
            vm.Info.Search.UseCommand.Execute(use);
            Check(found.IsApplied && !doc.Document.Text.Contains("  sorry", StringComparison.Ordinal), $"using it replaces the sorry with {use.Trial.Replacement}");
            Check(await WaitFor(() => !doc.IsProcessing && !doc.Diagnostics.Any(d => d.Message.Contains("sorry", StringComparison.Ordinal)), 60), "and Lean accepts the proof");
            doc.Document.UndoStack.Undo();
            Check(doc.Document.Text == doc.SavedText, "one undo brings the sorry back");
        }
        vm.Info.Search.CloseCommand.Execute(null);

        await vm.WhyNotProvedAsync("unfinished");
        Check(vm.Verification.Trails.Count == 1 && vm.Verification.TrailTitle.Contains("unfinished uses sorry", StringComparison.Ordinal),
            $"Tenet explains why unfinished is not fully proved ({vm.Verification.TrailTitle})");
        await vm.WhyNotProvedAsync("not_not_elim");
        Check(vm.Verification.Trails.FirstOrDefault()?.Links.Select(l => l.Name).SequenceEqual(["not_not_elim", "em'"]) == true
            && vm.Verification.Trails[0].Links[0].Where == "Basic.lean:15", "and traces not_not_elim to the axiom em' it uses, with where each is written");
        Snap(window, outDir, "17-why-not-proved");

        string slowFile = Path.Combine(proofsDir, "Slow.lean");
        string walkFile = Path.Combine(Path.GetTempPath(), $"leanstudio-walk-{Environment.ProcessId}.html");
        try
        {
            await File.WriteAllTextAsync(slowFile, "theorem slow (x y z w : Int) (h1 : 3*x + 5*y - 7*z + 11*w = 13) (h2 : 2*x - 9*y + 4*z - w = 8)\n"
                + "    (h3 : x + y + z + w = 1) (h4 : 6*x - 2*y + 3*z - 5*w = 21) : 17*x + 3*y - 2*z + w ≠ 1000 := by\n  omega\n\n"
                + "theorem quick (a b : Nat) : a + b = b + a := by\n  omega\n");
            DocumentViewModel sd = (await vm.OpenFileAsync(slowFile))!;
            await vm.ProfileFileAsync();
            Check(vm.TimingItems.FirstOrDefault()?.Declaration.StartsWith("theorem slow", StringComparison.Ordinal) == true && sd.Timings.Count > 0,
                $"profiling finds the slow theorem ({vm.TimingStatus})");
            Check(vm.TimingItems.FirstOrDefault()?.HotSpot.Contains("omega", StringComparison.Ordinal) == true, "and that omega is where its time goes");
            Snap(window, outDir, "18-timing");
            Check(await WaitFor(() => !sd.IsProcessing, 60) && await vm.WriteWalkthroughAsync(walkFile)
                && File.ReadAllText(walkFile).Contains("<h2>quick", StringComparison.Ordinal), "the proofs are written out as a walkthrough web page");
            Check(LeanStudio.Core.Proofs.Walkthrough.MissingOnWeb(sd.Document.Text).Count == 0, "and the file can be shared to the web editor as it is");
            await vm.CloseDocumentCommand.ExecuteAsync(sd);
        }
        finally
        {
            File.Delete(slowFile);
            File.Delete(walkFile);
        }
        vm.ActiveDocument = doc;

        Console.WriteLine("counterexamples, extract as lemma, the REPL, the project map");
        string falseFile = Path.Combine(proofsDir, "Falsehood.lean");
        try
        {
            await File.WriteAllTextAsync(falseFile,
                "theorem squares (n : Nat) (h : n > 2) : n * n < 10 := by\n  sorry\n\n"
                + "theorem big (a b c : Nat) (h1 : a > 2) (h2 : b = a + 1) : a + b + c > 4 := by\n  have key : a + b > 4 := by sorry\n  omega\n");
            DocumentViewModel fd = (await vm.OpenFileAsync(falseFile))!;
            Check(await WaitFor(() => !fd.IsProcessing && fd.Diagnostics.Count > 0, 90), "a file with a false theorem opens");
            fd.Reveal(1, 3);
            await vm.ProveAsync(false);
            SearchResultView? refuted = vm.Info.Search.Results.FirstOrDefault();
            Check(refuted?.Counterexample == "✗ False when n = 4", $"Prove It says the goal is false, with a counterexample ({refuted?.Counterexample})");
            Snap(window, outDir, "20-counterexample");
            vm.Info.Search.CloseCommand.Execute(null);

            fd.Reveal(5, 30);
            string? problem = await vm.ExtractLemmaAtAsync("key_step");
            Check(problem is null && fd.Document.Text.StartsWith("theorem squares", StringComparison.Ordinal)
                && fd.Document.Text.Contains("theorem key_step {a b : Nat} (h1 : a > 2) (h2 : b = a + 1) : a + b > 4 := by", StringComparison.Ordinal)
                && fd.Document.Text.Contains("have key : a + b > 4 := by exact key_step (by assumption) (by assumption)", StringComparison.Ordinal),
                $"a goal is extracted as a lemma with just the hypotheses it needs ({problem})");
            Check(await WaitFor(() => !fd.IsProcessing && fd.Diagnostics.Count(d => d.Message.Contains("sorry", StringComparison.Ordinal)) == 2
                && !fd.Diagnostics.Any(d => d.Severity == LeanStudio.Lsp.DiagnosticSeverity.Error), 60), "and Lean accepts the file, with the sorry now in the new lemma");
            fd.Document.UndoStack.Undo();
            Check(fd.Document.Text == fd.SavedText, "one undo takes the extraction back");
            await vm.CloseDocumentCommand.ExecuteAsync(fd);
        }
        finally
        {
            File.Delete(falseFile);
        }

        vm.ActiveDocument = doc;
        doc.Reveal(20, 2);
        vm.BottomTab = MainViewModel.ReplPanel;
        vm.ReplInput = "double 21";
        await vm.RunReplCommand.ExecuteAsync(null);
        vm.ReplInput = "#check and_swap";
        await vm.RunReplCommand.ExecuteAsync(null);
        Check(vm.ReplEntries.Count == 2 && vm.ReplEntries[0].Output == "42" && vm.ReplEntries[1].Output.Contains("and_swap", StringComparison.Ordinal),
            $"the REPL evaluates in the file's context ({string.Join(" | ", vm.ReplEntries.Select(e => e.Output))})");
        Snap(window, outDir, "21-repl");

        LeanStudio.Core.Verification.ProjectMap? map = await vm.ShowProjectMapAsync();
        Check(map is not null && map.Nodes.Any(n => n.Name == "unfinished" && n.IsSource) && map.Nodes.Any(n => n.Name == "not_not_elim" && n.Status == LeanStudio.Core.Verification.MapStatus.RestsOnAxiom),
            "the project map shows each declaration and what it rests on");
        Check(await WaitFor(() => window.MapWindow is not null, 5), "in a window of its own");
        if (window.MapWindow is { } mw)
        {
            await Task.Delay(500);
            Snap(mw, outDir, "22-project-map");
            mw.View.Select("not_not_elim");
            Snap(mw, outDir, "23-project-map-selected");
            mw.Close();
        }
        vm.ActiveDocument = doc;

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

        Console.WriteLine("light theme");
        vm.ActiveDocument = doc;
        vm.SidebarTab = MainViewModel.FilesTab;
        vm.BottomTab = MainViewModel.ProblemsPanel;
        doc.Reveal(16, 14);
        window.SetTheme("Light");
        await Task.Delay(500);
        Check(Application.Current!.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light, "the light theme applies");
        Snap(window, outDir, "19-light");
        window.SetTheme("Dark");
        await Task.Delay(300);

        Console.WriteLine("dialogs");
        string? dialogName = null;
        void OnDialog(Window d) => Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(400);
            Snap(d, outDir, dialogName!);
            d.Close();
        });
        DialogHooks.Opened += OnDialog;
        dialogName = "24-about";
        await window.ShowAboutAsync();
        dialogName = "25-preferences";
        await window.ShowPreferencesAsync();
        dialogName = "26-connect-assistant";
        await window.ShowConnectAssistantAsync();
        DialogHooks.Opened -= OnDialog;
        Check(File.Exists(Path.Combine(outDir, "24-about.png")) && File.Exists(Path.Combine(outDir, "26-connect-assistant.png")), "the About, Preferences and Connect dialogs open and close");

        vm.SidebarTab = MainViewModel.ToolchainsTab;
        await vm.Toolchains.RefreshAsync();
        Check(vm.Toolchains.Installed.Count > 0, "installed toolchains are listed");
        Snap(window, outDir, "05-toolchains");

        await vm.DisposeAsync();
        return _failures;
    }
}
