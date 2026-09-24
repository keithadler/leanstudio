using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Toolchains;
using LeanStudio.Core.Workflow;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

/// <summary>A module the last build compiled, and how long it took, for the Timing panel.</summary>
/// <param name="Name">The module.</param>
/// <param name="Took">How long Lake says it took.</param>
/// <param name="Path">Its source file, or null when it isn't in the project.</param>
/// <param name="Slowest">The slowest module's time, to scale the bar.</param>
public sealed record ModuleTiming(string Name, TimeSpan Took, string? Path, TimeSpan Slowest)
{
    /// <summary>How long it took, as a person reads it.</summary>
    public string Time => TaskProgress.Format(Took);

    /// <summary>The bar's width in the panel.</summary>
    public double BarWidth => Slowest <= TimeSpan.Zero ? 0 : Math.Max(2, 120 * Took / Slowest);

    /// <summary>A minute or more.</summary>
    public bool IsWarm => Took >= TimeSpan.FromMinutes(1) && !IsHot;

    /// <summary>Five minutes or more.</summary>
    public bool IsHot => Took >= TimeSpan.FromMinutes(5);
}

/// <summary>
/// What a build is doing, beyond the bar: the modules being compiled now (Lake names a module only when it
/// finishes, so they are read from the running <c>lean</c> processes), marks in the file tree (✓ built, ⋯ being
/// compiled, ◐ sorry, ✗ errors), and the slowest modules afterwards in the Timing panel.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, string> _buildMarks = new(StringComparer.Ordinal);
    private DispatcherTimer? _compileWatch;
    private int _finishedSeen;

    /// <summary>The modules being compiled now, and for how long, for the progress banner; empty otherwise.</summary>
    [ObservableProperty]
    private string _busyNow = "";

    /// <summary>The Timing panel's modules from the last build, slowest first.</summary>
    public ObservableList<ModuleTiming> ModuleTimings { get; } = new();

    /// <summary>What the Timing panel says over the modules: how many were compiled, and in how long.</summary>
    [ObservableProperty]
    private string _moduleTimingStatus = "";

    /// <summary>The build's mark for a file or folder (see <see cref="FileNode.BuildMark"/>).</summary>
    public string BuildMarkOf(string path)
    {
        if (_buildMarks.Count == 0)
        {
            return "";
        }
        if (_buildMarks.TryGetValue(path, out string? mark))
        {
            return mark;
        }
        if (!Directory.Exists(path))
        {
            return "";
        }
        // A folder: the worst mark that needs attention inside it.
        string prefix = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string worst = "";
        foreach ((string p, string m) in _buildMarks)
        {
            if (p.StartsWith(prefix, StringComparison.Ordinal) && Rank(m) > Rank(worst))
            {
                worst = m;
            }
        }
        return worst;
    }

    private static int Rank(string mark) => mark switch { "✗" => 3, "⋯" => 2, "◐" => 1, _ => 0 };

    /// <summary>The source file of a module the build names, or null when it isn't one of the project's files.</summary>
    private string? FileOfModule(string target)
    {
        if (Project is null)
        {
            return null;
        }
        string module = target.Split(':')[0];
        string file = Path.Combine([Project.Root, .. module.Split('.')]) + ".lean";
        return File.Exists(file) ? file : null;
    }

    private void BeginBuildView()
    {
        _buildMarks.Clear();
        _finishedSeen = 0;
        BusyNow = "";
        RefreshMarks();
        if (Project is null || RemoteTargets.For(Project.Root) is not null)
        {
            return; // a remote build's processes run on the other machine
        }
        _compileWatch?.Stop();
        _compileWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _compileWatch.Tick += async (_, _) => await WatchCompilesAsync();
        _compileWatch.Start();
    }

    private void StopWatchingCompiles()
    {
        _compileWatch?.Stop();
        _compileWatch = null;
        BusyNow = "";
        CompilingNow.Clear();
    }

    private bool _watching;

    private async Task WatchCompilesAsync()
    {
        if (_watching || Project is not { } project)
        {
            return;
        }
        _watching = true;
        try
        {
            IReadOnlyList<LeanWorker> now = await LeanProcesses.CompilingAsync(project.Root);
            if (_compileWatch is null)
            {
                return; // the build ended while ps ran
            }
            TakeFinished();
            foreach (string p in _buildMarks.Where(x => x.Value == "⋯").Select(x => x.Key).ToList())
            {
                _buildMarks.Remove(p);
            }
            foreach (LeanWorker w in now)
            {
                _buildMarks.TryAdd(w.File, "⋯");
            }
            CompilingNow.Reset(now.Select(w => new DashboardLine(project.ModuleNameOf(w.File) ?? Path.GetFileName(w.File), TaskProgress.Format(w.Running))));
            BusyNow = now.Count == 0 ? "" : "Compiling now: " + string.Join(", ", now.Take(4).Select(w =>
                $"{project.ModuleNameOf(w.File) ?? Path.GetFileName(w.File)} ({TaskProgress.Format(w.Running)})"))
                + (now.Count > 4 ? $" and {now.Count - 4} more" : "");
            RefreshMarks();
        }
        finally
        {
            _watching = false;
        }
    }

    /// <summary>Mark the modules the build has finished since last time.</summary>
    private void TakeFinished()
    {
        if (_progressReader?.Progress is not TaskProgress p || p.Finished.Count == _finishedSeen)
        {
            return;
        }
        _finishedSeen = p.Finished.Count;
        foreach (string target in p.Finished)
        {
            if (FileOfModule(target) is string file && (!_buildMarks.TryGetValue(file, out string? m) || m == "⋯"))
            {
                _buildMarks[file] = "✓";
            }
        }
    }

    /// <summary>The build ended: its errors and sorries in the tree, and its slowest modules in the Timing panel.</summary>
    private void EndBuildView()
    {
        TakeFinished();
        foreach (string p in _buildMarks.Where(x => x.Value == "⋯").Select(x => x.Key).ToList())
        {
            _buildMarks.Remove(p);
        }
        foreach (IGrouping<string, BuildMessage> file in _buildMessages.GroupBy(m => m.Path))
        {
            if (file.Any(m => m.IsError))
            {
                _buildMarks[file.Key] = "✗";
            }
            else if (file.Any(m => m.Message.Contains("sorry", StringComparison.Ordinal)))
            {
                _buildMarks[file.Key] = "◐";
            }
        }
        RefreshMarks();
        if (_progressReader?.Progress is TaskProgress progress && progress.Timings.Count > 0)
        {
            var slowest = progress.Timings.OrderByDescending(t => t.Took).Take(30).ToList();
            ModuleTimings.Reset(slowest.Select(t => new ModuleTiming(t.Name, t.Took, FileOfModule(t.Name), slowest[0].Took)));
            TimeSpan total = progress.Timings.Aggregate(TimeSpan.Zero, (a, t) => a + t.Took);
            ModuleTimingStatus = $"The last build compiled {progress.Timings.Count:N0} module{(progress.Timings.Count == 1 ? "" : "s")} ({TaskProgress.Format(total)} of Lean's time in all). The slowest:";
        }
    }

    /// <summary>
    /// After a build, check again the open files Lean checked against a half-built project: those opened while it ran,
    /// and those whose imports it says are out of date. Otherwise they keep errors the build has since fixed.
    /// </summary>
    private async Task RecheckAfterBuildAsync(IReadOnlyCollection<DocumentViewModel> openedDuringBuild)
    {
        if (_server is not { State: LeanServerState.Running } s)
        {
            return;
        }
        var stale = Documents.Where(d => d.IsLean && (openedDuringBuild.Contains(d)
            || d.Diagnostics.Any(x => x.Message.StartsWith("Imports are out of date", StringComparison.Ordinal)))).ToList();
        if (stale.Count == 0)
        {
            return;
        }
        Log($"Checking {string.Join(", ", stale.Select(d => Path.GetFileName(d.Path)))} again against the new build.");
        foreach (DocumentViewModel d in stale)
        {
            await s.CloseAsync(d.Uri);
            d.Diagnostics = [];
            await s.OpenAsync(d.Uri, d.Document.Text);
        }
        UpdateImportsStale();
    }

    private void RefreshMarks()
    {
        foreach (FileNode n in Files)
        {
            n.RefreshMarks();
        }
    }

    /// <summary>Open a module from the Timing panel.</summary>
    [RelayCommand]
    private async Task OpenModuleTimingAsync(ModuleTiming? t)
    {
        if (t?.Path is string path)
        {
            await OpenFileAsync(path);
        }
    }
}
