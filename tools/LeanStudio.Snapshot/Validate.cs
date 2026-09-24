using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using LeanStudio.App.Services;
using LeanStudio.App.ViewModels;
using LeanStudio.App.Views;
using LeanStudio.Core.Verification;

/// <summary>
/// Validate a Lean project the way a person would in Lean Studio, and keep screenshots of each stage:
/// open it, fetch Mathlib's cache, build it with Lean, open the files that state and prove the result, re-check
/// every declaration with Tenet (an independent kernel), and trace the axioms the main theorems rest on.
///
///     LeanStudio.Snapshot --validate &lt;project&gt; &lt;output dir&gt; [theorem…]
/// </summary>
internal static class Validate
{
    public static async Task<int> RunAsync(string project, string outDir, IReadOnlyList<string> theorems)
    {
        Directory.CreateDirectory(outDir);
        var clock = Stopwatch.StartNew();
        using var log = new StreamWriter(Path.Combine(outDir, "validation-log.txt"));
        int failures = 0, shot = 0;
        void Note(string s)
        {
            string line = $"[{clock.Elapsed:hh\\:mm\\:ss}] {s}";
            Console.WriteLine(line);
            log.WriteLine(line);
            log.Flush();
        }
        void Check(bool ok, string what)
        {
            Note((ok ? "ok   " : "FAIL ") + what);
            failures += ok ? 0 : 1;
        }

        var window = new MainWindow(new Settings { VerifyAfterBuild = false }) { Width = 1600, Height = 1000, OpenOnStartup = project };
        void Snap(string name)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            string path = Path.Combine(outDir, $"{++shot:00}-{name}.png");
#pragma warning disable CS0618
            window.CaptureRenderedFrame()?.Save(path);
#pragma warning restore CS0618
            Note("screenshot " + Path.GetFileName(path));
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
                await Task.Delay(250);
            }
            return true;
        }
        // While a long task runs, a screenshot every few minutes.
        async Task Watch(Task task, string name, TimeSpan every)
        {
            int n = 0;
            while (await Task.WhenAny(task, Task.Delay(every)) != task)
            {
                Snap($"{name}-{++n:00}");
            }
            await task;
        }
        string OutputTail(int lines) => string.Join('\n', window.ViewModel.Output.Text.Split('\n').TakeLast(lines));

        window.Show();
        MainViewModel vm = window.ViewModel;
        Note($"Lean Studio validating {project}");

        Check(await WaitFor(() => vm.Project is not null, 120), "the project opens");
        vm.BottomTab = MainViewModel.OutputPanel;
        await Task.Delay(1500);
        Snap("project-opened");

        if (vm.Project!.DependsOnMathlib)
        {
            Note("fetching Mathlib's cache (lake exe cache get)");
            vm.BottomTab = MainViewModel.OutputPanel;
            await Watch(vm.GetMathlibCacheCommand.ExecuteAsync(null), "mathlib-cache", TimeSpan.FromSeconds(45));
            Note("cache: " + OutputTail(3).Replace('\n', ' '));
            Snap("mathlib-cache-done");
        }

        Note("building with Lean (lake build)");
        vm.BottomTab = MainViewModel.OutputPanel;
        await Watch(vm.BuildCommand.ExecuteAsync(null), "building", TimeSpan.FromMinutes(3));
        string built = OutputTail(6);
        Note("build output ends: " + built.Replace('\n', ' '));
        Check(built.Contains("Build completed successfully", StringComparison.Ordinal), "Lean builds every module");
        Snap("built");

        Check(await WaitFor(() => vm.ServerStatus == "Lean: ready", 600), "Lean's server is ready");
        foreach (string file in new[] { "Solution.lean", "Challenge.lean" }.Select(f => Path.Combine(project, f)).Where(File.Exists))
        {
            DocumentViewModel d = (await vm.OpenFileAsync(file))!;
            Check(await WaitFor(() => !d.IsProcessing && d.Diagnostics.Count + d.ProofMarks.Count > 0, 900), $"Lean checks {Path.GetFileName(file)}");
            var errors = d.Diagnostics.Where(x => x.Severity == LeanStudio.Lsp.DiagnosticSeverity.Error).ToList();
            Note($"{Path.GetFileName(file)}: {errors.Count} errors, {d.Diagnostics.Count(x => x.Message.Contains("sorry", StringComparison.Ordinal))} sorry warnings, marks {string.Join(", ", d.ProofMarks.Select(m => $"line {m.Line + 1} {(m.Done ? "done" : "goals left")}"))}");
            int proofLine = d.Lines().ToList().FindIndex(l => l.Contains(":= by", StringComparison.Ordinal));
            d.Reveal(Math.Max(0, proofLine + 1), 4);
            await WaitFor(() => vm.Info.HasGoals || vm.Info.Status.Length > 0, 60);
            await Task.Delay(2500);
            vm.BottomTab = MainViewModel.ProblemsPanel;
            Snap(Path.GetFileNameWithoutExtension(file).ToLowerInvariant());
        }

        Note("re-checking every declaration with Tenet, an independent Lean kernel");
        vm.BottomTab = MainViewModel.TenetPanel;
        await Watch(vm.VerifyCommand.ExecuteAsync(null), "tenet", TimeSpan.FromMinutes(2));
        Check(await WaitFor(() => vm.Verification.Report is not null, 60), "Tenet reports");
        if (vm.Verification.Report is VerificationReport r)
        {
            Note($"Tenet: {r.ModulesChecked} modules checked ({r.ModulesLoaded} loaded, Lean {r.LeanVersion}) in {r.Elapsed:hh\\:mm\\:ss}: "
                 + $"{r.Verified} verified, {r.Conditional} resting on sorry or an axiom, {r.Rejected} rejected");
            foreach (DeclarationVerdict v in r.Declarations.Where(x => x.Status != VerificationStatus.Verified).Take(40))
            {
                Note($"  {v.Status} {v.Name} ({v.Module}:{v.Line}) {string.Join(", ", v.Assumptions)} {v.Message}");
            }
            Check(r.Rejected == 0, "Tenet rejects nothing");
        }
        Snap("tenet-report");

        if (vm.TenetBuild is TenetWorkspace ws)
        {
            string[] standard = ["propext", "Classical.choice", "Quot.sound"];
            // A benchmark's config.json (as Comparator reads it): which module states the theorems, which proves
            // them, and the axioms the proof may use.
            string? challenge = null, solution = null;
            string[] permitted = standard;
            var names = new List<string>(theorems);
            string config = Path.Combine(project, "config.json");
            if (File.Exists(config))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config));
                var root = doc.RootElement;
                challenge = root.TryGetProperty("challenge_module", out var c) ? c.GetString() : null;
                solution = root.TryGetProperty("solution_module", out var so) ? so.GetString() : null;
                if (root.TryGetProperty("permitted_axioms", out var pa))
                {
                    permitted = pa.EnumerateArray().Select(x => x.GetString()!).ToArray();
                }
                if (root.TryGetProperty("theorem_names", out var tn))
                {
                    names.AddRange(tn.EnumerateArray().Select(x => x.GetString()!).Where(n => !names.Contains(n)));
                }
                Note($"config.json: challenge {challenge}, solution {solution}, theorems [{string.Join(", ", names)}], permitted axioms [{string.Join(", ", permitted)}]");
            }
            foreach (string theorem in names)
            {
                IReadOnlyList<string> declaredIn = ws.ModulesDeclaring(theorem);
                if (declaredIn.Count > 0)
                {
                    Note($"{theorem} is declared in {string.Join(" and ", declaredIn)}: each is checked in its own module");
                }
                foreach (string? module in declaredIn.Count > 0 ? declaredIn.Cast<string?>() : [null])
                {
                    string label = module is null ? theorem : $"{theorem} (in {module})";
                    DeclarationDetails? det = ws.Details(theorem, module);
                    Check(det is not null, $"{label} is in the build");
                    if (det is null)
                    {
                        continue;
                    }
                    IReadOnlyList<string> axioms = ws.AxiomsOf(theorem, module);
                    Note($"axioms of {label}: [{string.Join(", ", axioms)}]");
                    if (module is not null && module == challenge)
                    {
                        Check(axioms.Contains("sorryAx"), $"{label} is the open statement: it rests on sorry, as a challenge should");
                        continue;
                    }
                    string[] allowed = module is not null && module == solution ? permitted : standard;
                    Check(axioms.Count > 0 && axioms.All(allowed.Contains), $"{label} rests only on [{string.Join(", ", allowed)}]");
                    Check(ws.WhyNotProved(theorem, default, module).Count == 0, $"{label}: nothing it depends on rests on sorry or a project axiom");
                }
                if (challenge is not null && solution is not null && declaredIn.Contains(challenge) && declaredIn.Contains(solution))
                {
                    string? stated = ws.Details(theorem, challenge)?.Type, proved = ws.Details(theorem, solution)?.Type;
                    Note($"statement in {challenge}: {stated}");
                    Note($"statement in {solution}: {proved}");
                    Check(stated is not null && stated == proved, $"{solution} proves exactly the statement {challenge} makes");
                }
                else if (ws.Details(theorem, declaredIn.FirstOrDefault()) is DeclarationDetails d)
                {
                    Note($"{theorem} ({d.Module}): {d.Type}");
                }
            }
            if (vm.Verification.Report is VerificationReport rep)
            {
                foreach (DeclarationVerdict v in rep.Declarations.Where(v => names.Contains(v.Name)))
                {
                    Note($"Tenet verdict for {v.Name} in {v.Module}: {v.Status}{(v.Assumptions.Count > 0 ? " (" + string.Join(", ", v.Assumptions) + ")" : "")}");
                }
            }
            if (names.Count > 0)
            {
                vm.SidebarTab = MainViewModel.LibraryTab;
                foreach (string theorem in names.Take(2))
                {
                    vm.Navigator.Query = theorem;
                    if (await WaitFor(() => vm.Navigator.Results.Any(x => x.Name == theorem), 60))
                    {
                        vm.Navigator.Selected = vm.Navigator.Results.Where(x => x.Name == theorem).OrderBy(x => x.Module == solution ? 0 : 1).First();
                        await WaitFor(() => vm.Navigator.AxiomSummary.Length > 0, 120);
                        Note($"Library, {vm.Navigator.Selected.Name} in {vm.Navigator.Selected.Module}: {vm.Navigator.AxiomSummary}");
                    }
                    await Task.Delay(1000);
                    Snap("library-" + theorem.Split('.')[0].ToLowerInvariant());
                }
            }
        }

        Note(failures == 0 ? "VALIDATED: every check passed" : $"{failures} check(s) failed");
        await vm.DisposeAsync();
        return failures;
    }
}
