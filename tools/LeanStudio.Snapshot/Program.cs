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
        editor.Document.UndoStack.ClearAll();
        doc.Document.Text = doc.SavedText;

        vm.SidebarTab = MainViewModel.ToolchainsTab;
        await vm.Toolchains.RefreshAsync();
        Check(vm.Toolchains.Installed.Count > 0, "installed toolchains are listed");
        Snap(window, outDir, "05-toolchains");

        await vm.DisposeAsync();
        return _failures;
    }
}
