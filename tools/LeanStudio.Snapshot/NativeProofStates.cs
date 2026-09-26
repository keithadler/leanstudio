using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using LeanStudio.App;
using LeanStudio.App.Services;
using LeanStudio.App.ViewModels;
using LeanStudio.App.Views;
using LeanStudio.Core.Proofs;

/// <summary>
/// The proof-state map in a real window, with the platform's web view: maps a file of proofs that share states,
/// and checks that the 3D page loaded its data and drew it, and that picking a state in the list selects it in the
/// page and picking one in the page selects it in the list. Needs a desktop session; run by hand, not headless:
///
///     LeanStudio.Snapshot --native-proof-states &lt;repo root&gt; &lt;output dir&gt;
/// </summary>
internal static class NativeProofStates
{
    public static int Run(string repo, string outDir)
    {
        Directory.CreateDirectory(outDir);
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        int failures = 0;
        var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                failures = await CheckAsync(repo, outDir);
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
        Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<bool> WaitFor(Func<Task<bool>> condition, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (!await condition())
        {
            if (sw.Elapsed.TotalSeconds > seconds)
            {
                return false;
            }
            await Task.Delay(250);
        }
        return true;
    }

    private static async Task<int> CheckAsync(string repo, string outDir)
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
            failures += ok ? 0 : 1;
        }

        string project = Path.Combine(repo, "samples", "Proofs");
        string file = Path.Combine(project, "Proofs", "SharedStates.lean");
        var window = new MainWindow(new Settings()) { Width = 1400, Height = 860, OpenOnStartup = project };
        try
        {
            await File.WriteAllTextAsync(file, Scenario.SharedStatesSource);
            window.Show();
            MainViewModel vm = window.ViewModel;
            Check(await WaitFor(() => Task.FromResult(vm.ServerStatus == "Lean: ready"), 90), "Lean starts");
            DocumentViewModel doc = (await vm.OpenFileAsync(file))!;
            Check(await WaitFor(() => Task.FromResult(!doc.IsProcessing), 120), "the file elaborates");
            ProofStatesResult? states = await vm.CollectProofStatesAsync(wholeProject: false);
            Check(states?.Steps.Count == 8, $"every tactic step is read ({states?.Steps.Count} of 8)");
            Check(await WaitFor(() => Task.FromResult(window.StatesWindow is not null), 5), "the map opens in a window");
            if (window.StatesWindow is not { } sw)
            {
                return failures + 1;
            }
            sw.Width = 1240;
            sw.Height = 780;
            Check(sw.WebView is not null, "with the 3D view in the platform's web view");
            if (sw.WebView is { } web)
            {
                Check(await WaitFor(async () => await Page(web, "typeof proofStates === 'object' && proofStates.drawn() > 0 ? 'yes' : 'no'") == "yes", 30),
                    "the page loads the states and draws them");
                StateNode first = sw.Listed[0];
                sw.Select(first.Id, fromPage: false);
                Check(await WaitFor(async () => await Page(web, "String(proofStates.selected())") == first.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), 10),
                    "picking a state in the list selects it in the 3D view");
                StateNode second = sw.Listed[^1];
                await Page(web, $"proofStates.click({second.Id}); 'done'");
                Check(await WaitFor(() => Task.FromResult(IsSelected(sw, second.Id)), 10), "picking a state in the 3D view selects it in the list");
                sw.ShowLevel(StateMatch.Shape);
                Check(await WaitFor(async () => await Page(web, "proofStates.level()") == "shape", 10), "changing what counts as the same changes the 3D view too");
                Check(await Page(web, "document.getElementById('empty').offsetParent === null ? 'hidden' : 'shown'") == "hidden",
                    "with states to show, the empty message is hidden");
                window.SetTheme("Light");
                Check(await WaitFor(async () => await Page(web, "document.documentElement.dataset.theme || ''") == "light", 10),
                    "switching the app to the light theme restyles the 3D view");
                await Task.Delay(2500);
                Screenshot(sw, Path.Combine(outDir, "proof-states-in-window.png"));
            }
            sw.Close();
        }
        finally
        {
            window.Close();
            File.Delete(file);
        }
        return failures;
    }

    private static bool IsSelected(ProofStatesWindow sw, int id) => sw.Selected == id;

    /// <summary>Run a script in the page and return its string result as text.</summary>
    private static async Task<string> Page(Avalonia.Controls.NativeWebView web, string script)
    {
        string? raw = await web.InvokeScript(script);
        return raw is ['"', ..] ? System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "" : raw ?? "";
    }

    /// <summary>Save what is on screen where the window is (best effort; macOS needs the Screen Recording permission).</summary>
    private static void Screenshot(Avalonia.Controls.Window window, string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.WriteLine("  skip screenshot (macOS only)");
            return;
        }
        double scale = window.RenderScaling;
        PixelPoint at = window.Position;
        Size size = window.ClientSize;
        string rect = $"{at.X / scale:0},{at.Y / scale:0},{size.Width:0},{size.Height + 28:0}";
        using var p = Process.Start(new ProcessStartInfo("screencapture", ["-x", "-R", rect, path]) { UseShellExecute = false })!;
        p.WaitForExit();
        Console.WriteLine(p.ExitCode == 0 && File.Exists(path) ? "  saved " + path
            : "  skip screenshot: screencapture failed (give the terminal the Screen Recording permission to save one)");
    }
}
