using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using LeanStudio.App.Services;
using LeanStudio.App.ViewModels;
using LeanStudio.App.Views;
using LeanStudio.Core.Git;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Proofs;
using LeanStudio.Core.Workflow;

namespace LeanStudio.Snapshot;

/// <summary>
/// The profiler at scale (<c>--scale-profiler PROJECT OUTDIR FILE</c>): the real app, headless, on a large file of a
/// Mathlib project, each profiler feature timed against an aim set from how long Lean itself takes to check the
/// file. What the aims hold to is Lean Studio's own share: reading Lean's output, the panel, the flame graph, the
/// live updates, the regression check's worktree (which must share Mathlib, not build it again).
/// </summary>
internal static class ProfilerScale
{
    public static async Task<int> RunAsync(string project, string outDir, string file)
    {
        Directory.CreateDirectory(outDir);
        using var log = new StreamWriter(Path.Combine(outDir, "profiler-scale-log.txt"));
        void Note(string s)
        {
            Console.WriteLine(s);
            log.WriteLine(s);
            log.Flush();
        }
        int missed = 0, shot = 0;
        var window = new MainWindow(new Settings { VerifyAfterBuild = false }) { Width = 1600, Height = 1000, OpenOnStartup = project };
        void Snap(string name)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
#pragma warning disable CS0618
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, $"p{++shot:00}-{name}.png"));
#pragma warning restore CS0618
        }
        async Task<double> Time(string what, Func<Task<bool>> run, double aim)
        {
            var sw = Stopwatch.StartNew();
            bool ok = await run();
            double took = sw.Elapsed.TotalSeconds;
            missed += ok && took <= aim ? 0 : 1;
            Note($"{(ok ? (took <= aim ? "ok  " : "SLOW") : "FAIL")} {took,7:F2} s  {what} (aim: under {aim:F1} s)");
            return took;
        }
        async Task<bool> WaitFor(Func<bool> condition, double seconds)
        {
            var sw = Stopwatch.StartNew();
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
        // What the app holds on to: managed memory after a full collection (the working set also counts garbage not yet collected).
        long Memory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(true) >> 20;
        }
        static int Nodes(ProfileReport r) => r.Declarations.Sum(d => d.Trace.Sum(t => t.DescendantsAndSelf().Count()));

        var leanProject = new LeanProject(project);
        string text = File.ReadAllText(file);
        Note($"{Path.GetRelativePath(project, file)}: {text.Split('\n').Length:N0} lines");

        // The yardstick: Lean checking the file on its own, with no profiler.
        string plain = await LeanCli.MirrorAsync(leanProject, file, text, "scale-plain");
        var clock = Stopwatch.StartNew();
        ProcessResult r = await LeanCli.RunAsync(leanProject, ["--json", plain]);
        double lean = clock.Elapsed.TotalSeconds;
        Note($"Lean alone checks it in {lean:F1} s{(r.Success ? "" : " (with errors)")}");

        // Parsing: Lean's output with every profiler on, read into a report.
        string traced = await LeanCli.MirrorAsync(leanProject, file, text, "scale-traced");
        clock.Restart();
        ProcessResult t = await LeanCli.RunAsync(leanProject, ["--json", "-Dtrace.profiler=true", "-Dtrace.profiler.threshold=5", "-Dprofiler=true", "-Dprofiler.threshold=1", traced]);
        double tracedLean = clock.Elapsed.TotalSeconds;
        Note($"with the profilers on, Lean takes {tracedLean:F1} s and prints {t.Output.Length / 1024:N0} KB");
        ProfileReport parsed = null!;
        await Time("reading that output into a report", () => { parsed = Profiler.Parse(t.Output, text.Split('\n')); return Task.FromResult(parsed.Declarations.Count > 0); }, 1);
        Note($"  {parsed.Declarations.Count:N0} declarations, {Nodes(parsed):N0} trace steps, {parsed.Categories.Count} categories");

        MainViewModel vm = window.ViewModel;
        window.Show();
        if (!await WaitFor(() => vm.Project is not null && vm.ServerStatus == "Lean: ready", 300))
        {
            Note("FAIL the project did not open");
            return 1;
        }
        DocumentViewModel d = (await vm.OpenFileAsync(file))!;
        await WaitFor(() => !d.IsProcessing, 900);
        vm.ProfileCounters = true;
        // Tenet reads the project's build (all of Mathlib) in the background after it opens: wait for it, so its
        // memory is not counted as the profiler's.
        await WaitFor(() => vm.Output.Text.Contains("Tenet opened", StringComparison.Ordinal), 600);
        long before = Memory();

        await Time("Profile file, with counters (two runs of Lean)", async () => { await vm.ProfileFileAsync(); return vm.TimingItems.Count > 0; }, tracedLean * 2.5 + 10);
        Note($"  {vm.TimingStatus}");
        long afterOne = Memory();
        await vm.ProfileFileAsync();
        long afterTwo = Memory();
        Note($"  managed memory {before:N0} MB before, {afterOne:N0} MB after one profile, {afterTwo:N0} MB after a second; {vm.ProfileCounterBars.Count} counter rows");
        bool steady = afterTwo - afterOne < 50;
        Note($"{(steady ? "ok  " : "FAIL")} a second profile of the file does not hold on to more memory");
        missed += steady ? 0 : 1;
        FlameGraph flame = window.GetVisualDescendants().OfType<FlameGraph>().First();
        await Time("the whole file's flame graph drawn", () =>
        {
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            return Task.FromResult(flame.Root is not null);
        }, 1);
        Snap("file-flame");
        await Time("picking the costliest declaration (details, flame graph)", () =>
        {
            vm.SelectedTiming = vm.TimingItems[0];
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            return Task.FromResult(vm.FlameRoot?.Text == vm.TimingItems[0].Timing.Name);
        }, 0.5);
        Note($"  {vm.ProfileDetailTitle}: {vm.TimingItems[0].Time}, {vm.HotSteps.Count} bottom-up rows, {vm.LineCosts.Count} tactic lines");
        Snap("declaration-flame");
        await Time("every declaration picked in turn (up to 50)", () =>
        {
            foreach (TimingItem item in vm.TimingItems.Take(50).ToList())
            {
                vm.SelectedTiming = item;
            }
            return Task.FromResult(true);
        }, 2);

        d.Reveal(vm.TimingItems[0].Timing.Line, 0);
        await Time("This declaration (the costliest)", async () => { await vm.ProfileDeclarationAsync(); return vm.TimingItems.Count == 1; }, tracedLean * 1.5 + 5);
        vm.ProfileHeartbeats = true;
        await Time("Profile file in heartbeats", async () => { await vm.ProfileFileAsync(); return vm.TimingItems.Count > 0; }, tracedLean * 1.5 + 10);
        vm.ProfileHeartbeats = false;

        // Live: the first check expands every trace; an edit at the end must cost only what it changes.
        clock.Restart();
        vm.ProfileLive = true;
        await Time("Live: the first profile", async () => await WaitFor(() => vm.Profile is { Live: true } p && p.Declarations.Count > 0, tracedLean * 3 + 60), tracedLean * 1.5 + 10);
        Note($"  {vm.Profile?.Declarations.Count:N0} declarations, {(vm.Profile is { } lp ? Nodes(lp) : 0):N0} trace steps read so far");
        // The costliest declaration whose tree has not been fetched (the one at the cursor has).
        TimingItem costliest = vm.TimingItems.FirstOrDefault(t => !t.Timing.TraceComplete) ?? vm.TimingItems[0];
        await Time($"Live: picking the costliest declaration fetches its tree ({costliest.Timing.Name})", async () =>
        {
            vm.SelectedTiming = costliest;
            return await WaitFor(() => vm.SelectedTiming is { Timing.TraceComplete: true } t && t.Timing.Name == costliest.Timing.Name, 120);
        }, 10);
        Note($"  {vm.SelectedTiming?.Timing.Trace.Sum(n => n.DescendantsAndSelf().Count()):N0} steps in its tree");
        ProfileReport firstLive = vm.Profile!;
        d.Document.Insert(d.Document.TextLength, "\n\ntheorem leanstudio_scale_edit : 2 + 2 = 4 := by norm_num\n");
        await Time("Live: an edit at the end, until the profile shows it", async () =>
            await WaitFor(() => vm.Profile is { Live: true } p && !ReferenceEquals(p, firstLive) && d.Timings.Count > 0, 120), MainViewModel.LiveProfileDelay / 1000.0 + 6);
        Note($"  {vm.TimingStatus}");
        Snap("live");
        vm.ProfileLive = false;
        d.Document.UndoStack.Undo();
        Note($"  managed memory {Memory():N0} MB");

        // The regression check, with the dependents: the old revision is built in a worktree that shares Mathlib.
        if (GitRepository.Find(project) is GitRepository git)
        {
            string? head = await git.ShortHashAsync("HEAD");
            File.AppendAllText(file, "\n\ntheorem leanstudio_scale_check : 3 + 4 = 7 := by norm_num\n");
            try
            {
                CheckReport? check = null;
                await Time($"Check for Regressions against HEAD ({head}), with the files that import it", async () =>
                {
                    check = await vm.CheckRegressionsAsync("HEAD");
                    return check is not null && check.Files.All(f => f.Error is null);
                }, tracedLean * 4 + 120);
                Note($"  {vm.TimingStatus}");
                string packages = Path.Combine(project, ".lake", "leanstudio", "check", head ?? "", ".lake", "packages");
                bool shared = new DirectoryInfo(packages).LinkTarget is not null;
                Note($"{(shared ? "ok  " : "FAIL")} the worktree shares the project's packages instead of fetching or building them");
                missed += shared ? 0 : 1;
                Snap("check");
            }
            finally
            {
                File.WriteAllText(file, text);
            }
        }
        else
        {
            Note("(not a git repository: the regression check is skipped)");
        }
        await vm.DisposeAsync();
        Note(missed == 0 ? "every action was within its aim" : $"{missed} action(s) failed or missed their aim");
        return missed;
    }
}
