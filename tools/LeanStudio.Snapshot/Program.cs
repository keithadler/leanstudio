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

if (args.Length > 0 && args[0] == "--native-infoview")
{
    // Not headless: the real window, with the platform's web view, to see the infoview render a widget in it.
    return NativeInfoview.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetFullPath("."), args.Length > 2 ? Path.GetFullPath(args[2]) : Path.GetFullPath("snapshots"));
}

// Headless: there is no native window to put a web view in, so the Infoview tab shows its fallback.
InfoviewPane.NativeWebViewAllowed = false;

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
        failures = args.Length > 2 && args[0] == "--validate"
            ? await Validate.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3..])
            : await Scenario.RunAsync(repo, outDir);
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

    /// <summary>A Lean file with a user widget (core Lean, no ProofWidgets), and the 0-based line of its #widget.</summary>
    internal const string WidgetSource = """
        import Lean
        open Lean Widget

        @[widget_module]
        def helloWidget : Widget.Module where
          javascript := "
            import * as React from 'react';
            export default function(props) {
              return React.createElement('div', {id: 'lean-studio-widget', style: {padding: '8px', border: '2px solid orange', borderRadius: '6px'}},
                'Hello ' + props.name + ' from a Lean widget, in Lean Studio')
            }"

        #widget helloWidget with Json.mkObj [("name", Json.str "Lean Studio")]

        theorem demo (p q : Prop) (hp : p) (hq : q) : p ∧ q := by
          constructor
          · exact hp
          · exact hq
        """;

    internal const int WidgetLine = 12;

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
        string proofsRootFile = Path.Combine(repo, "samples", "Proofs", "Proofs.lean"), proofsRootText = await File.ReadAllTextAsync(proofsRootFile);
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
            await File.WriteAllTextAsync(proofsRootFile, proofsRootText); // the new module joined the library root
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

        Console.WriteLine("editor intelligence: semantic colours, inlay hints, occurrences, callers, traces");
        string smartFile = Path.Combine(proofsDir, "Smart.lean");
        try
        {
            await File.WriteAllTextAsync(smartFile,
                "def lengthOf (xs : List α) : Nat := xs.length\n\n"
                + "def square (n : Nat) : Nat := n * n\n\n"
                + "def usesSquare := square 3 + square 4\n\n"
                + "example : Inhabited (Nat × Bool) := by\n"
                + "  set_option trace.Meta.synthInstance true in\n"
                + "  exact inferInstance\n");
            DocumentViewModel sm = (await vm.OpenFileAsync(smartFile))!;
            var smartEditor = window.FindControl<LeanStudio.App.Editor.LeanEditor>("Editor")!;
            Check(await WaitFor(() => !sm.IsProcessing && smartEditor.SemanticTokenCount > 0, 90), $"Lean's semantic tokens colour the variables ({smartEditor.SemanticTokenCount})");
            Check(await WaitFor(() => smartEditor.InlayHintLabels.Any(l => l.Contains('α', StringComparison.Ordinal)), 30),
                $"an inlay hint shows the implicit α Lean binds ({string.Join(", ", smartEditor.InlayHintLabels)})");
            sm.Reveal(4, 20);
            Check(await WaitFor(() => smartEditor.HasOccurrences, 15), "every use of the name at the cursor is highlighted");
            sm.Reveal(2, 5);
            await vm.ShowCallersCommand.ExecuteAsync(null);
            Check(vm.References.Count == 2 && vm.References.All(r => r.Text.StartsWith("usesSquare", StringComparison.Ordinal)) && vm.ReferencesTitle == "Used by (1)",
                $"Who Uses This lists where square is used ({vm.ReferencesTitle})");
            sm.Reveal(8, 4);
            Check(await WaitFor(() => vm.Info.Messages.Any(m => m.HasTraces), 30), "a trace shows as a tree in the Tactic State");
            TraceNodeView? root = vm.Info.Messages.SelectMany(m => m.Traces).FirstOrDefault();
            Check(root?.Class == "[Meta.synthInstance]" && root.Text.Contains("Inhabited (Nat × Bool)", StringComparison.Ordinal), $"its root is the instance search ({root?.Class} {root?.Text})");
            if (root is not null)
            {
                root.IsExpanded = true;
                Check(await WaitFor(() => root.Children.Count > 0 && root.Children[0].Class.Length > 0, 15), "expanding it fetches its steps from Lean");
            }
            vm.BottomTab = MainViewModel.ReferencesPanel;
            await Task.Delay(300);
            Snap(window, outDir, "28-editor-intelligence");
            await vm.CloseDocumentCommand.ExecuteAsync(sm);
        }
        finally
        {
            File.Delete(smartFile);
        }
        vm.ActiveDocument = doc;

        Console.WriteLine("Vim mode");
        string vimFile = Path.Combine(proofsDir, "VimCheck.lean");
        try
        {
            await File.WriteAllTextAsync(vimFile, "theorem t : True := by\n  skip\n  trivial\n");
            DocumentViewModel vd = (await vm.OpenFileAsync(vimFile))!;
            var vimEditor = window.FindControl<LeanStudio.App.Editor.LeanEditor>("Editor")!;
            vm.Settings.VimMode = true;
            vimEditor.ApplySettings(vm.Settings);
            vimEditor.TextEditor.CaretOffset = 0;
            var area = vimEditor.TextEditor.TextArea;
            area.PerformTextInput("jdd");
            Check(vd.Document.Text == "theorem t : True := by\n  trivial\n" && vm.VimStatus == "-- NORMAL --",
                $"Vim mode: typed keys are commands (jdd deletes a line) and the status bar shows the mode ({vm.VimStatus})");
            area.PerformTextInput("u");
            Check(vd.Document.Text == "theorem t : True := by\n  skip\n  trivial\n", "u undoes it through the editor's own undo");
            area.PerformTextInput("GkA");
            area.PerformTextInput(" -- done");
            Check(vm.VimStatus == "-- INSERT --" && vd.Document.Text.Contains("trivial -- done", StringComparison.Ordinal), "in insert mode typing is text again");
            vimEditor.Vim.Key("<Esc>");
            area.PerformTextInput("^ciwexact");
            vimEditor.Vim.Key("<Esc>");
            Check(vd.Document.Text.Contains("  exact -- done", StringComparison.Ordinal) && vm.VimStatus == "-- NORMAL --", $"operators with text objects work in the editor (ciw) [{vd.Document.Text.Replace("\n", "⏎")}] [{vm.VimStatus}]");
            Snap(window, outDir, "29-vim");
            vm.Settings.VimMode = false;
            vimEditor.ApplySettings(vm.Settings);
            Check(vm.VimStatus == "", "and turning it off leaves ordinary editing");
            await vd.SaveAsync();
            await vm.CloseDocumentCommand.ExecuteAsync(vd);
        }
        finally
        {
            File.Delete(vimFile);
            vm.Settings.VimMode = false;
        }
        vm.ActiveDocument = doc;

        Console.WriteLine("Lean's own infoview, for widgets");
        Uri infoviewPage = vm.StartInfoview();
        using (var http = new HttpClient())
        {
            string html = await http.GetStringAsync(infoviewPage);
            var js = await http.GetAsync(new Uri(infoviewPage, "iv/index.production.min.js"));
            var css = await http.GetAsync(new Uri(infoviewPage, "iv/index.css"));
            var noToken = await http.GetAsync(new Uri(infoviewPage.GetLeftPart(UriPartial.Path)));
            Check(html.Contains("loadRenderInfoview", StringComparison.Ordinal) && js.IsSuccessStatusCode && css.IsSuccessStatusCode
                && noToken.StatusCode == System.Net.HttpStatusCode.Forbidden,
                "the app serves Lean's infoview (with its widgets' runtime) to the browser, only with its token");
        }
        string widgetFile = Path.Combine(repo, "samples", "Proofs", "Proofs", "WidgetDemo.lean");
        try
        {
            await File.WriteAllTextAsync(widgetFile, WidgetSource);
            DocumentViewModel wd = (await vm.OpenFileAsync(widgetFile))!;
            Check(await WaitFor(() => !wd.IsProcessing && wd.Diagnostics.All(d => d.Severity != LeanStudio.Lsp.DiagnosticSeverity.Error), 120), "a file with a user widget elaborates");
            wd.Reveal(WidgetLine, 3);
            Check(await WaitFor(() => vm.Info.HasWidgets, 30) && vm.Info.WidgetsLabel == "Widget", "the Tactic State says there is a widget at the cursor");
            vm.Info.OpenWidgetsCommand.Execute(null);
            Check(vm.RightTab == MainViewModel.InfoviewTab, "and its Widget chip opens the Infoview tab");
            InfoviewPane pane = window.Infoview;
            Border slot = window.FindControl<Border>("InfoviewSlot")!;
            Check(await WaitFor(() => pane.IsVisible && pane.UnavailableReason is not null, 10)
                && Math.Abs(pane.Bounds.Width - slot.Bounds.Width) < 1 && Math.Abs(pane.Bounds.Height - slot.Bounds.Height) < 1,
                "the infoview pane covers the tab (here, headless, it offers the browser instead of a web view)");
            Snap(window, outDir, "27-infoview-tab");
            vm.RightTab = MainViewModel.GoalsTab;
            Check(await WaitFor(() => !pane.IsVisible, 5), "and goes away with the tab");
            wd.Reveal(WidgetLine + 2, 3);
            Check(await WaitFor(() => !vm.Info.HasWidgets, 30), "off the widget, the chip goes too");
            await vm.CloseDocumentCommand.ExecuteAsync(wd);
        }
        finally
        {
            File.Delete(widgetFile);
        }
        vm.ActiveDocument = doc;

        Console.WriteLine("C and the FFI");
        string nativeLean = Path.Combine(proofsDir, "Native.lean");
        string cDir = Path.Combine(repo, "samples", "Proofs", "c");
        string nativeC = Path.Combine(cDir, "native.c");
        try
        {
            Directory.CreateDirectory(cDir);
            await File.WriteAllTextAsync(nativeC, "#include <lean/lean.h>\n\nLEAN_EXPORT uint32_t proofs_add(uint32_t a, uint32_t b) {\n    return a + b;\n}\n");
            await File.WriteAllTextAsync(nativeLean, "/-- Adds in C. -/\n@[extern \"proofs_add\"]\nopaque add (a b : UInt32) : UInt32\n\n@[extern \"proofs_twice\"]\nopaque twice (s : @& String) : IO UInt64\n");
            DocumentViewModel nl = (await vm.OpenFileAsync(nativeLean))!;
            IReadOnlyList<LeanStudio.Core.Workflow.FfiProblem> ffi = await vm.CheckFfiNowAsync();
            Check(ffi.Count == 1 && ffi[0].Message.Contains("No C function proofs_twice", StringComparison.Ordinal)
                && vm.Problems.Any(p => p.Diagnostic.Source == "ffi"), "an @[extern] without its C function shows in Problems");
            nl.Reveal(2, 8);
            await vm.GoToDefinitionCommand.ExecuteAsync(null);
            Check(await WaitFor(() => vm.ActiveDocument?.Path == nativeC && vm.ActiveDocument.CaretLine == 2, 10), "go to definition on an @[extern] opens its C function");
            DocumentViewModel nc = vm.ActiveDocument!;
            if (LeanStudio.Lsp.CLanguageServer.Find() is not null)
            {
                Check(await WaitFor(() => vm.CServer?.IsRunning == true, 30), "clangd serves the C file, with Lean's headers");
                nc.Document.Insert(nc.Document.TextLength, "\nint broken(void) { return nope; }\n");
                Check(await WaitFor(() => nc.Diagnostics.Any(d => d.Message.Contains("nope", StringComparison.Ordinal)), 30)
                    && !nc.Diagnostics.Any(d => d.Message.Contains("lean.h", StringComparison.Ordinal)), "and reports C errors (and finds lean.h)");
                Snap(window, outDir, "27-ffi-c");
                nc.Document.UndoStack.Undo();
            }
            nc.Reveal(2, 25);
            await vm.GoToDefinitionCommand.ExecuteAsync(null);
            Check(await WaitFor(() => vm.ActiveDocument?.Path == nativeLean && vm.ActiveDocument.CaretLine == 1, 10), "and from the C function back to the Lean declaration");
            nl.Reveal(5, 8);
            await vm.WriteCStubCommand.ExecuteAsync(null);
            Check(await WaitFor(() => File.ReadAllText(nativeC).Contains("LEAN_EXPORT lean_obj_res proofs_twice(b_lean_obj_arg s, lean_obj_arg world)", StringComparison.Ordinal), 10),
                "Write C Stub writes the function with the signature Lean expects");
            Check((await vm.CheckFfiNowAsync()).Count == 0, "and then every binding has its C function");
            foreach (DocumentViewModel d in vm.Documents.Where(d => d.Path == nativeC || d.Path == nativeLean).ToList())
            {
                await d.SaveAsync();
                await vm.CloseDocumentCommand.ExecuteAsync(d);
            }
        }
        finally
        {
            File.Delete(nativeLean);
            if (Directory.Exists(cDir))
            {
                Directory.Delete(cDir, true);
            }
            await vm.CheckFfiNowAsync();
        }
        vm.ActiveDocument = doc;

        Console.WriteLine("for people who write a lot of Lean");
        string proofsRoot = Path.Combine(repo, "samples", "Proofs", "Proofs.lean");
        string rootText = await File.ReadAllTextAsync(proofsRoot);
        string tidyFile = Path.Combine(repo, "samples", "Proofs", "Proofs", "Tidy.lean");
        // Answer the rename prompt and the alias confirmation as a person would.
        void Answer(Window w) => Dispatcher.UIThread.Post(async () =>
        {
            // Wait for the dialog's controls to be laid out, then type the new name (the prompt) and press OK.
            Button? ok = null;
            await WaitFor(() => (ok = w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsDefault)) is not null, 10);
            if (w.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is TextBox box)
            {
                box.Text = "doubleIt";
            }
            ok?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        });
        try
        {
            Check(await vm.CreateFileAsync(Path.GetDirectoryName(tidyFile)!, "Tidy") is null
                && (await File.ReadAllTextAsync(proofsRoot)).Contains("import Proofs.Tidy", StringComparison.Ordinal),
                "a new module is imported by the library root, which imports every module");
            DocumentViewModel td = vm.ActiveDocument!;
            td.Document.Text = "import Proofs.Basic\n\n/-- Twice `n`. -/\ndef twiceIt (n : Nat) : Nat := 2 * n\n\ntheorem twiceIt_zero : twiceIt 0 = 0 := rfl\n\ndef undocumented : Nat := twiceIt 1\n";
            await td.SaveAsync();
            Check(await WaitFor(() => !td.IsProcessing, 60), "it elaborates");
            LeanStudio.Core.Workflow.ImportReport? imports = await vm.RemoveUnusedImportsAsync();
            Check(imports?.Removable.Select(r => r.Module).SequenceEqual(["Proofs.Basic"]) == true && !td.Document.Text.Contains("import Proofs.Basic", StringComparison.Ordinal),
                "Remove Unused Imports takes out the import nothing uses, as one edit");
            IReadOnlyList<LeanStudio.Core.Workflow.LintFinding> lint = await vm.LintFileAsync();
            Check(lint.Any(f => f.Linter == "linter.missingDocs" && f.Line == 6) && vm.Problems.Any(p => p.Diagnostic.Source == "lint"),
                "Lint File runs the linters CI runs and lists what they find in Problems");
            Snap(window, outDir, "28-lint");
            Check(await WaitFor(() => !td.IsProcessing, 60), "Lean has the saved file");
            td.Reveal(2, 6); // on twiceIt, where it is declared
            DialogHooks.Opened += Answer;
            await vm.RenameSymbolCommand.ExecuteAsync(null);
            DialogHooks.Opened -= Answer;
            Check(td.Document.Text.Contains("def doubleIt", StringComparison.Ordinal)
                && td.Document.Text.Contains("@[deprecated doubleIt (since := ", StringComparison.Ordinal)
                && td.Document.Text.Contains("def twiceIt : type_of% @doubleIt := @doubleIt", StringComparison.Ordinal),
                "renaming a declaration keeps the old name as a deprecated alias");
            await Task.Delay(500);
            Check(await WaitFor(() => !td.IsProcessing, 60) && td.Diagnostics.All(x => x.Severity != LeanStudio.Lsp.DiagnosticSeverity.Error), "and Lean accepts it");
            Snap(window, outDir, "29-rename-alias");
            await td.SaveAsync();
            await vm.CloseDocumentCommand.ExecuteAsync(td);
        }
        finally
        {
            DialogHooks.Opened -= Answer;
            File.Delete(tidyFile);
            await File.WriteAllTextAsync(proofsRoot, rootText);
            string built = Path.Combine(repo, "samples", "Proofs", ".lake", "build");
            foreach (string f in Directory.Exists(built) ? Directory.GetFiles(built, "Tidy.*", SearchOption.AllDirectories) : [])
            {
                File.Delete(f); // Lint File built it
            }
        }
        vm.ActiveDocument = doc;

        Console.WriteLine("split editor");
        vm.ActiveDocument = doc;
        vm.SplitEditorCommand.Execute(null);
        Check(vm.IsSplit && vm.SplitDocument == doc && window.SplitEditorControl.Document == doc, "Split Editor shows the file on both sides");
        DocumentViewModel second = vm.Documents.First(d => d != doc && d.IsLean);
        vm.SplitDocument = second;
        vm.EditorFocused(split: true);
        Check(vm.ActiveDocument == second && vm.PrimaryDocument == doc && window.FindControl<LeanStudio.App.Editor.LeanEditor>("Editor")!.Document == doc,
            "focusing the right side makes its file the active one, and the left side keeps its own");
        await Task.Delay(300);
        Check(window.SplitEditorControl.Bounds.Width > 300 && window.FindControl<LeanStudio.App.Editor.LeanEditor>("Editor")!.Bounds.Width > 300, "the two sides share the width");
        Snap(window, outDir, "30-split-editor");
        vm.EditorFocused(split: false);
        Check(vm.ActiveDocument == doc, "and focusing the left side makes its file the active one again");
        vm.EditorFocused(split: true);
        vm.CloseSplitCommand.Execute(null);
        Check(!vm.IsSplit && vm.ActiveDocument == doc, "closing the split goes back to the left side's file");

        Console.WriteLine("several cursors");
        string multiFile = Path.Combine(repo, "samples", "Proofs", "Proofs", "Cursors.lean");
        try
        {
            await File.WriteAllTextAsync(multiFile, "example (a b : Nat) (h1 : a = b) (h2 : b = a) : True := by\n  have k1 := h1\n  have k2 := h2\n  trivial\n");
            DocumentViewModel md = (await vm.OpenFileAsync(multiFile))!;
            var ed = window.FindControl<LeanStudio.App.Editor.LeanEditor>("Editor")!;
            AvaloniaEdit.Editing.TextArea area = ed.TextEditor.TextArea;
            void Press(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers mods = Avalonia.Input.KeyModifiers.None) =>
                area.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key, KeyModifiers = mods, Source = area });
            var cmd = OperatingSystem.IsMacOS() ? Avalonia.Input.KeyModifiers.Meta : Avalonia.Input.KeyModifiers.Control;
            string before = md.Document.Text;
            area.Caret.Offset = md.Document.Text.IndexOf("k1", StringComparison.Ordinal);
            Press(Avalonia.Input.Key.D, cmd); // selects k1
            area.Selection = AvaloniaEdit.Editing.Selection.Create(area, area.Selection.SurroundingSegment.Offset, area.Selection.SurroundingSegment.Offset + 1); // just "k"
            Press(Avalonia.Input.Key.D, cmd); // and the next k
            Check(ed.MultiCursor.Cursors.Count == 2, "⌘D adds the next occurrence of the selection");
            area.PerformTextInput("hyp");
            Check(md.Document.Text.Contains("have hyp1 := h1\n  have hyp2 := h2", StringComparison.Ordinal), "typing goes in at every cursor");
            Press(Avalonia.Input.Key.Back);
            Check(md.Document.Text.Contains("have hy1 := h1\n  have hy2 := h2", StringComparison.Ordinal), "and so does Backspace");
            Snap(window, outDir, "31-cursors");
            ed.TextEditor.Undo();
            ed.TextEditor.Undo();
            Check(md.Document.Text == before && !ed.MultiCursor.IsActive, "each edit at every cursor is one undo step, and undo goes back to one cursor");
            area.ClearSelection();
            area.Caret.Offset = md.Document.Text.IndexOf("have k1", StringComparison.Ordinal);
            Press(Avalonia.Input.Key.Down, cmd | Avalonia.Input.KeyModifiers.Alt);
            Press(Avalonia.Input.Key.Down, cmd | Avalonia.Input.KeyModifiers.Alt);
            Check(ed.MultiCursor.Cursors.Count == 3, "⌘⌥↓ adds a cursor on each line below");
            area.PerformTextInput("-- ");
            Check(md.Document.Text.Contains("  -- have k1 := h1\n  -- have k2 := h2\n  -- trivial", StringComparison.Ordinal), "so three lines are commented at once: " + md.Document.Text.Replace("\n", "⏎"));
            Press(Avalonia.Input.Key.Escape);
            Check(!ed.MultiCursor.IsActive, "Escape goes back to one cursor");
            ed.TextEditor.Undo();
            // A column selection (⌥-drag): typing replaces the column on every line.
            area.Selection = new AvaloniaEdit.Editing.RectangleSelection(area,
                new AvaloniaEdit.TextViewPosition(2, 8), new AvaloniaEdit.TextViewPosition(3, 9)); // the k of k1 and k2 (columns count from 1)
            area.PerformTextInput("g");
            Check(md.Document.Text.Contains("have g1 := h1\n  have g2 := h2", StringComparison.Ordinal), "a column selection replaces the same columns on every line: " + md.Document.Text.Replace("\n", "⏎"));
            md.ReplaceAll(before);
            await md.SaveAsync();
            await vm.CloseDocumentCommand.ExecuteAsync(md);
        }
        finally
        {
            File.Delete(multiFile);
        }
        vm.ActiveDocument = doc;

        Console.WriteLine("keyboard: your own shortcuts, and Emacs's keys");
        string keysFile = MainWindow.KeybindingsPath;
        string emacsFile = Path.Combine(repo, "samples", "Proofs", "Proofs", "Keys.lean");
        try
        {
            File.Delete(keysFile);
            await window.EditKeybindingsAsync();
            Check(File.Exists(keysFile) && vm.ActiveDocument?.Path == keysFile && vm.ActiveDocument.Document.Text.Contains("//   Lean: Prove It", StringComparison.Ordinal),
                "keybindings.json opens, listing every command to bind");
            await vm.CloseDocumentCommand.ExecuteAsync(vm.ActiveDocument);
            await File.WriteAllTextAsync(keysFile, "[ { \"key\": \"Cmd+Alt+J\", \"command\": \"View: Split Editor\" } ]");
            Check(window.LoadUserKeys() == 1, "a shortcut of one's own is read");
            vm.ActiveDocument = doc;
            var cmdKey = OperatingSystem.IsMacOS() ? Avalonia.Input.KeyModifiers.Meta : Avalonia.Input.KeyModifiers.Control;
            window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.J, KeyModifiers = cmdKey | Avalonia.Input.KeyModifiers.Alt, Source = window });
            Check(vm.IsSplit, "and runs its command");
            vm.CloseSplitCommand.Execute(null);

            await File.WriteAllTextAsync(emacsFile, "theorem t : True := by\n  trivial\n");
            DocumentViewModel ed = (await vm.OpenFileAsync(emacsFile))!;
            vm.Settings.EmacsMode = true;
            var emacsEditor = window.FindControl<LeanStudio.App.Editor.LeanEditor>("Editor")!;
            AvaloniaEdit.Editing.TextArea emacsArea = emacsEditor.TextEditor.TextArea;
            void Emacs(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers mods) =>
                emacsArea.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key, KeyModifiers = mods, Source = emacsArea });
            var C = Avalonia.Input.KeyModifiers.Control;
            emacsArea.ClearSelection();
            emacsArea.Caret.Offset = 0;
            Emacs(Avalonia.Input.Key.E, C);
            Check(emacsArea.Caret.Offset == 22, "Emacs keys: C-e goes to the end of the line");
            Emacs(Avalonia.Input.Key.N, C);
            Emacs(Avalonia.Input.Key.A, C);
            Emacs(Avalonia.Input.Key.K, C);
            Check(ed.Document.Text == "theorem t : True := by\n\n", "C-k kills the rest of the line");
            Emacs(Avalonia.Input.Key.Y, C);
            Check(ed.Document.Text == "theorem t : True := by\n  trivial\n", "and C-y yanks it back");
            Emacs(Avalonia.Input.Key.X, C);
            Check(vm.VimStatus == "C-x-", "the status bar shows a pending C-x");
            Emacs(Avalonia.Input.Key.S, C);
            Check(!ed.IsDirty && vm.VimStatus == "", "and C-x C-s saves");
            Snap(window, outDir, "32-emacs");

            vm.Settings.EmacsMode = false;
            await File.WriteAllTextAsync(MainWindow.AbbreviationsPath, "{ \"zeta5\": \"ζ(5)\" }");
            Check(window.LoadAbbreviations() == 1, "an abbreviation of one's own is read from abbreviations.json");
            emacsArea.ClearSelection();
            emacsArea.Caret.Offset = ed.Document.TextLength;
            foreach (char ch in "\\zeta5 ")
            {
                emacsArea.PerformTextInput(ch.ToString());
            }
            Check(ed.Document.Text.EndsWith("ζ(5) ", StringComparison.Ordinal), "and typing \\zeta5 gives ζ(5)");
        }
        finally
        {
            File.Delete(MainWindow.AbbreviationsPath);
            window.LoadAbbreviations();
            vm.Settings.EmacsMode = false;
            File.Delete(keysFile);
            window.LoadUserKeys();
            if (vm.Documents.FirstOrDefault(d => d.Path == emacsFile) is DocumentViewModel open)
            {
                await open.SaveAsync(); // closing an unsaved file asks, and nobody answers here
                await vm.CloseDocumentCommand.ExecuteAsync(open);
            }
            File.Delete(emacsFile);
        }
        vm.ActiveDocument = doc;

        Console.WriteLine("screen readers");
        {
            // What a screen reader announces for each control (Avalonia's automation peers: UI Automation on
            // Windows, the accessibility API on macOS).
            var unnamed = window.GetVisualDescendants().OfType<Control>()
                .Where(c => c is Button or ComboBox or TextBox or Avalonia.Controls.Primitives.ToggleButton && c.IsEffectivelyVisible)
                .Select(c => (Control: c, Name: Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(c).GetName()))
                .Where(x => string.IsNullOrWhiteSpace(x.Name) || !x.Name.Any(char.IsLetter)) // "×" says nothing useful
                .Select(x => $"{x.Control.GetType().Name}{(x.Control.Name is { } n ? " " + n : "")} '{x.Name}' in {x.Control.GetVisualParent()?.GetType().Name}")
                .ToList();
            Check(unnamed.Count == 0, "every button, box and list has a name a screen reader can say" + (unnamed.Count == 0 ? "" : ": missing on " + string.Join("; ", unnamed.Take(12))));
            var editorPeer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(window.FindControl<LeanStudio.App.Editor.LeanEditor>("Editor")!);
            Check(editorPeer.GetAutomationControlType() == Avalonia.Automation.Peers.AutomationControlType.Edit
                && editorPeer.GetName().StartsWith("Editor, Basic.lean, line", StringComparison.Ordinal)
                && (editorPeer.GetProvider<Avalonia.Automation.Provider.IValueProvider>()?.Value ?? "").Contains("theorem", StringComparison.Ordinal),
                $"the editor is an edit field named after its file, whose text a screen reader can read ({editorPeer.GetName()})");
        }

        Console.WriteLine("the Tactic State as VS Code's infoview has it, and proof marks");
        vm.ActiveDocument = doc;
        Check(await WaitFor(() => doc.ProofMarks.Any(m => m.Done), 30), "finished proofs are marked where they end");
        doc.Reveal(16, 14); // in not_not_elim: p : Prop, h : ¬¬p, hp : p
        Check(await WaitFor(() => vm.Info.Goals.Count > 0, 30), "goals at the cursor");
        int hypsBefore = vm.Info.Goals[0].Hypotheses.Count;
        vm.Info.HideTypes = true;
        Check(vm.Info.Goals[0].Hypotheses.Count == hypsBefore - 1 && vm.Info.Goals[0].Hypotheses.All(h => h.Names != "p"), "Hide Type Assumptions leaves out p : Prop");
        vm.Info.TargetFirst = true;
        Check(vm.Info.Goals[0].TargetFirst && vm.Info.Goals[0].TargetRow == 0, "Goal Before Assumptions puts the target first");
        Snap(window, outDir, "33-tactic-state-options");
        vm.Info.HideTypes = false;
        vm.Info.TargetFirst = false;
        Check(vm.Settings.HideTypeAssumptions == false && vm.Info.Goals[0].Hypotheses.Count == hypsBefore, "and they come back, and are remembered in Settings");
        string at = vm.Info.Position;
        vm.Info.Paused = true;
        doc.Reveal(5, 2);
        await Task.Delay(1200);
        Check(vm.Info.Position == at, "Pause keeps the state shown while the cursor moves");
        vm.Info.Paused = false;
        Check(await WaitFor(() => vm.Info.Position != at, 10), "and resuming follows the cursor again");
        doc.Reveal(16, 14);
        Check(await WaitFor(() => vm.Info.Goals.Count > 0 && vm.Info.PlainGoals.Contains("hp : p", StringComparison.Ordinal), 20), "back in the proof");
        string beforeComment = doc.Document.Text;
        vm.GoalsToComment();
        Check(doc.Document.Text.Contains("\n  /- ", StringComparison.Ordinal) && doc.Document.Text.Contains("hp : p", StringComparison.Ordinal)
            && doc.Document.Text.Contains(" -/\n  | inl hp", StringComparison.Ordinal),
            "Goals as a Comment puts them above the cursor, indented like its line");
        doc.Document.UndoStack.Undo();
        Check(doc.Document.Text == beforeComment, "and undo takes it out");
        doc.Reveal(20, 3); // the sorry in `unfinished`
        Check(await WaitFor(() => vm.Info.HasSteps && vm.Info.Steps.Any(s => s.ClosedBySorry), 30)
            && vm.Info.Steps.All(s => !s.ClosesAll) && vm.Info.Steps.First(s => s.ClosedBySorry).Summary == "put off with sorry",
            "a step that closes the goal with sorry says so, and isn't counted as done");

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("progress of a long task");
            // What a big build prints, a line a second: the status bar and the banner read it as it comes.
            var fake = new LeanStudio.Core.Workflow.ProjectTask("Build (fake)", "", "/bin/sh",
                ["-c", "echo '✔ [8700/8712] Replayed Mathlib'; for i in 1 2 3 4 5 6; do sleep 1; echo \"✔ [$((8700+i))/8712] Built Apery.M$i ($((i*20))s)\"; done; echo 'warning: Challenge.lean:33:8: declaration uses sorry'; sleep 4"]);
            Task running = vm.RunTaskAsync(fake);
            Check(await WaitFor(() => vm.HasBusyFraction && vm.BusyDetail.Contains("8,705 / 8,712", StringComparison.Ordinal), 20),
                $"the status bar shows how far a build has got, from Lake's own counts ({vm.BusyDetail})");
            Check(vm.BusyDetail.Contains("5 built, 1 from cache", StringComparison.Ordinal) && vm.BusySlowest.StartsWith("Slowest: Apery.M5 (2 min), Apery.M4 (80 s)", StringComparison.Ordinal),
                "and what came from the cache, and the slowest modules");
            Check(vm.BusyPercent == "99%" && vm.BusyShort == "8,705 / 8,712", $"the percentage rounds down, so a build isn't called done early ({vm.BusyPercent}, {vm.BusyShort})");
            Snap(window, outDir, "34-build-progress");
            await running;
            Check(!vm.HasBusyFraction && !vm.IsBusy, "the banner goes when the task ends");
        }

        Console.WriteLine("imports graph, and Lean's processes");
        vm.ActiveDocument = doc;
        await vm.ShowImportGraphAsync();
        Check(vm.References.Any(r => r.Text == "Proofs imports Proofs.Basic") && vm.ReferencesTitle.StartsWith("Proofs.Basic: imports", StringComparison.Ordinal),
            $"Imports and Imported By lists who imports the file ({vm.ReferencesTitle})");
        if (!OperatingSystem.IsWindows())
        {
            IReadOnlyList<LeanStudio.Core.Toolchains.LeanWorker> workers = await LeanStudio.Core.Toolchains.LeanProcesses.ListAsync(Path.Combine(repo, "samples", "Proofs"));
            Check(workers.Any(w => w.File == doc.Path && w.MemoryBytes > 0),
                $"Lean's processes shows the worker for each open file, with its memory ({string.Join(", ", workers.Select(w => Path.GetFileName(w.File) + " " + w.Memory))})");
        }

        Console.WriteLine("dialogs");
        string? dialogName = null;
        var unnamedInDialogs = new List<string>();
        void OnDialog(Window d) => Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(400);
            unnamedInDialogs.AddRange(d.GetVisualDescendants().OfType<Control>()
                .Where(c => c is Button or ComboBox or TextBox or Avalonia.Controls.Primitives.ToggleButton && c.IsEffectivelyVisible)
                .Select(c => (Control: c, Name: Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(c).GetName()))
                .Where(x => string.IsNullOrWhiteSpace(x.Name) || !x.Name.Any(char.IsLetter))
                .Select(x => $"{dialogName}: {x.Control.GetType().Name} '{x.Name}'"));
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
        Check(unnamedInDialogs.Count == 0, "and everything in them has a name a screen reader can say" + (unnamedInDialogs.Count == 0 ? "" : ": " + string.Join("; ", unnamedInDialogs.Take(12))));

        vm.SidebarTab = MainViewModel.ToolchainsTab;
        await vm.Toolchains.RefreshAsync();
        Check(vm.Toolchains.Installed.Count > 0, "installed toolchains are listed");
        Snap(window, outDir, "05-toolchains");

        await vm.DisposeAsync();
        return _failures;
    }
}
