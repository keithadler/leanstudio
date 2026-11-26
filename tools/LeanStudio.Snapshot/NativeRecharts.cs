using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using LeanStudio.App;
using LeanStudio.App.Services;
using LeanStudio.App.ViewModels;
using LeanStudio.App.Views;

/// <summary>
/// A ProofWidgets Recharts plot in the Infoview tab, in a real window with the platform's web view. Recharts finds a
/// chart's axes through React contexts, so the chart breaks ("Invariant failed") unless every component of the
/// chart (LineChart, XAxis, YAxis, Line) comes from one instance of ProofWidgets' recharts module. Needs a desktop
/// session and a Lake project that depends on ProofWidgets and is already built; run by hand:
///
///     LeanStudio.Snapshot --native-recharts &lt;project&gt; &lt;file.lean&gt; &lt;line of #html&gt;
///
/// The file is only read. A plain <c>#html Plot fn</c> from ProofWidgets' <c>Demos/Plot.lean</c> is the case to use.
/// While it runs it prints the page's address, so the same page can be opened in a browser to check it there.
/// </summary>
internal static class NativeRecharts
{
    public static int Run(string project, string file, int line)
    {
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        int failures = 0;
        var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                failures = await CheckAsync(project, file, line);
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

    private static async Task<int> CheckAsync(string project, string file, int line)
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
            failures += ok ? 0 : 1;
        }

        var window = new MainWindow(new Settings()) { Width = 1400, Height = 900, OpenOnStartup = project };
        try
        {
            window.Show();
            MainViewModel vm = window.ViewModel;
            Check(await WaitFor(() => Task.FromResult(vm.ServerStatus == "Lean: ready"), 180), "Lean starts");
            DocumentViewModel doc = (await vm.OpenFileAsync(file))!;
            Check(await WaitFor(() => Task.FromResult(!doc.IsProcessing), 600), "the file elaborates");
            doc.Reveal(line, 1);
            vm.ShowInfoview();

            InfoviewPane pane = window.Infoview;
            Check(await WaitFor(() => Task.FromResult(pane.WebView is not null || pane.UnavailableReason is not null), 10), "the Infoview tab loads");
            Console.WriteLine("  page: " + vm.StartInfoview());
            if (pane.WebView is { } web)
            {
                string state = "";
                bool drawn = await WaitFor(async () =>
                {
                    state = await Page(web, """
                        (document.querySelector('.recharts-line-curve') ? 'chart' : '')
                        + (document.body.innerText.includes('Invariant failed') ? ' invariant-failed' : '')
                        + ' | ' + document.body.innerText.slice(0, 400)
                        """);
                    return state.StartsWith("chart", StringComparison.Ordinal) || state.Contains("invariant-failed", StringComparison.Ordinal);
                }, 240);
                Console.WriteLine("  page state: " + await Page(web, "typeof window.leanStudioInfoview + ' | ' + document.getElementById('status').textContent + ' | ' + document.getElementById('infoview').innerText.slice(0, 200)"));
                Check(drawn && state.StartsWith("chart", StringComparison.Ordinal) && !state.Contains("invariant-failed", StringComparison.Ordinal),
                    "the Recharts plot draws in the Infoview tab: " + state.ReplaceLineEndings(" "));
            }
            else
            {
                Check(false, "in the platform's web view: " + pane.UnavailableReason);
            }
            if (Environment.GetEnvironmentVariable("LEANSTUDIO_NATIVE_HOLD") is { } hold && int.TryParse(hold, out int seconds))
            {
                // Keep the window (and its bridge) up for a while, to open the printed page in a browser too.
                await Task.Delay(TimeSpan.FromSeconds(seconds));
            }
        }
        finally
        {
            window.Close();
        }
        return failures;
    }

    private static async Task<string> Page(Avalonia.Controls.NativeWebView web, string script)
    {
        string? raw = await web.InvokeScript(script);
        string text = raw is ['"', ..] ? System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "" : raw ?? "";
        return text.Replace('\u00a0', ' ');
    }
}
