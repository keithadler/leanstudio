using Avalonia.Threading;
using LeanStudio.App.Services;
using LeanStudio.App.ViewModels;
using LeanStudio.App.Views;

namespace LeanStudio.Snapshot;

/// <summary>
/// <c>--leak &lt;repo&gt;</c>: open and close a file many times in the real window and report how many of the closed
/// documents are still held, and how much memory grew. Quicker than the whole run, for chasing a leak.
/// </summary>
internal static class Leak
{
    public static async Task<int> RunAsync(string repo)
    {
        var window = new MainWindow(new Settings()) { Width = 1200, Height = 800 };
        window.Taskbar.UseNative = false;
        window.OpenOnStartup = Path.Combine(repo, "samples", "Proofs");
        window.Show();
        MainViewModel vm = window.ViewModel;
        await Until(() => vm.Project is not null && vm.ServerStatus == "Lean: ready", 60);
        DocumentViewModel home = (await vm.OpenFileAsync(Path.Combine(repo, "samples", "Proofs", "Proofs", "Basic.lean")))!;
        string file = Path.Combine(repo, "samples", "Proofs", "Proofs", "Leak.lean");
        await File.WriteAllTextAsync(file, "theorem leak : True := trivial\n" + string.Concat(Enumerable.Repeat("-- padding to make a leak visible\n", 2000)));
        var closed = new List<WeakReference>();
        try
        {
            for (int i = 0; i < 40; i++)
            {
                DocumentViewModel? d = await vm.OpenFileAsync(file);
                await Until(() => false, 0.1);
                closed.Add(new WeakReference(d));
                await vm.CloseDocumentCommand.ExecuteAsync(d);
            }
            vm.ActiveDocument = home;
            await Until(() => false, 2);
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            int alive = closed.Count(w => w.IsAlive);
            Console.WriteLine($"{alive} of {closed.Count} closed documents still held");
            return alive <= 1 ? 0 : 1;
        }
        finally
        {
            File.Delete(file);
            await vm.DisposeAsync();
        }
    }

    private static async Task<bool> Until(Func<bool> condition, double seconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed.TotalSeconds > seconds)
            {
                return false;
            }
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(50);
        }
        return true;
    }
}
