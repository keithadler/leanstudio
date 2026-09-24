using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using LeanStudio.App;
using LeanStudio.App.Services;
using LeanStudio.App.ViewModels;
using LeanStudio.App.Views;

/// <summary>
/// The Infoview tab in a real window, with the platform's web view: opens a file with a user widget, shows the tab,
/// and checks that Lean's infoview rendered the widget inside the window (by reading the page), then saves a
/// screenshot of the window when it may. Needs a desktop session; run by hand (macOS, Windows), not in the headless run:
///
///     LeanStudio.Snapshot --native-infoview &lt;repo root&gt; &lt;output dir&gt;
/// </summary>
internal static class NativeInfoview
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
        string widgetFile = Path.Combine(project, "Proofs", "WidgetDemo.lean");
        var window = new MainWindow(new Settings()) { Width = 1400, Height = 860, OpenOnStartup = project };
        try
        {
            await File.WriteAllTextAsync(widgetFile, Scenario.WidgetSource);
            window.Show();
            MainViewModel vm = window.ViewModel;
            Check(await WaitFor(() => Task.FromResult(vm.ServerStatus == "Lean: ready"), 90), "Lean starts");
            DocumentViewModel doc = (await vm.OpenFileAsync(widgetFile))!;
            Check(await WaitFor(() => Task.FromResult(!doc.IsProcessing && doc.Diagnostics.Count >= 0), 120), "the widget file elaborates");
            doc.Reveal(Scenario.WidgetLine, 3);
            Check(await WaitFor(() => Task.FromResult(vm.Info.HasWidgets), 30), "the Tactic State shows the widget chip");
            vm.Info.OpenWidgetsCommand.Execute(null);

            InfoviewPane pane = window.Infoview;
            Check(await WaitFor(() => Task.FromResult(pane.WebView is not null || pane.UnavailableReason is not null), 10), "the Infoview tab loads");
            Check(pane.IsEmbedded, "in the platform's web view" + (pane.UnavailableReason is { } why ? $" (not available: {why})" : ""));
            if (pane.WebView is { } web)
            {
                string text = "";
                bool rendered = await WaitFor(async () =>
                {
                    text = await Page(web, "(document.getElementById('lean-studio-widget') || {}).textContent || ''");
                    return text.Contains("Hello Lean Studio", StringComparison.Ordinal);
                }, 60);
                Check(rendered, "Lean's infoview renders the user widget inside the window: " + text);
                string goals = "";
                doc.Reveal(Scenario.WidgetLine + 4, 4);
                bool follows = await WaitFor(async () =>
                {
                    goals = await Page(web, "document.body.innerText");
                    return goals.Contains("hp : p", StringComparison.Ordinal);
                }, 30);
                Check(follows, "and follows the cursor into the proof, showing its goals" + (follows ? "" : ": " + goals[..Math.Min(goals.Length, 600)]));
                window.SetTheme("Light");
                Check(await WaitFor(async () => await Page(web, "document.documentElement.dataset.theme") == "light", 10), "switching to the light theme restyles it");
                window.SetTheme("Dark");
                Check(await WaitFor(async () => await Page(web, "document.documentElement.dataset.theme") == "dark", 10), "and back to dark");
                doc.Reveal(Scenario.WidgetLine, 3);
                await WaitFor(async () => (await Page(web, "(document.getElementById('lean-studio-widget') || {}).textContent || ''")).Contains("Hello", StringComparison.Ordinal), 20);
                await Task.Delay(800);
                Screenshot(window, Path.Combine(outDir, "infoview-in-window.png"));
            }
        }
        finally
        {
            window.Close();
            File.Delete(widgetFile);
        }
        return failures;
    }

    /// <summary>Run a script in the page and return its string result as text (decoded, non-breaking spaces as spaces).</summary>
    private static async Task<string> Page(Avalonia.Controls.NativeWebView web, string script)
    {
        string? raw = await web.InvokeScript(script);
        string text = raw is ['"', ..] ? System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "" : raw ?? "";
        return text.Replace('\u00a0', ' ');
    }

    /// <summary>
    /// Save what is on screen where the window is (the web view is a native view, so the app can't render it
    /// itself). Best effort: it needs the Screen Recording permission on macOS, and is skipped without it.
    /// </summary>
    private static void Screenshot(MainWindow window, string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.WriteLine("  skip screenshot (macOS only)");
            return;
        }
        double scale = window.RenderScaling;
        PixelPoint at = window.Position;
        Size size = window.ClientSize;
        // screencapture takes points on macOS; Position is in pixels.
        string rect = $"{at.X / scale:0},{at.Y / scale:0},{size.Width:0},{size.Height + 28:0}";
        using var p = Process.Start(new ProcessStartInfo("screencapture", ["-x", "-R", rect, path]) { UseShellExecute = false })!;
        p.WaitForExit();
        Console.WriteLine(p.ExitCode == 0 && File.Exists(path) ? "  saved " + path
            : "  skip screenshot: screencapture failed (give the terminal the Screen Recording permission to save one)");
    }
}
