using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Proofs;

namespace LeanStudio.App.ViewModels;

/// <summary>A declaration's cost in the Profiler panel.</summary>
/// <param name="Timing">The declaration's cost, from Lean's profiler.</param>
/// <param name="Slowest">The costliest declaration's value in the same list, which gets the full-width bar.</param>
/// <param name="Path">The file it is in, to go to it.</param>
/// <param name="ShowFile">The list spans files (a project profile), so each row names its file.</param>
/// <param name="Change">The change from the baseline, as <c>−120 ms (−35%)</c>; empty without one.</param>
public sealed record TimingItem(DeclarationTiming Timing, double Slowest, string Path, bool ShowFile = false, string Change = "")
{
    /// <summary>Where it is: <c>line 12</c>, or <c>File.lean:12</c> in a project profile.</summary>
    public string Where => ShowFile ? $"{System.IO.Path.GetFileName(Path)}:{Timing.Line + 1}" : $"line {Timing.Line + 1}";
    /// <summary>Its cost, formatted in the profile's unit.</summary>
    public string Time => Timing.Time;
    /// <summary>The declaration's first line.</summary>
    public string Declaration => Timing.Declaration;
    /// <summary>The step inside the declaration that costs most, and its cost; empty when the profile names none.</summary>
    public string HotSpot => Timing.HotSpot is string h ? $"slowest part: {h} ({DeclarationTiming.Format(Timing.HotSpotValue, Timing.Unit)})" : "";
    /// <summary>With several runs, how much its time varied, as <c>± 12 ms</c>.</summary>
    public string Spread => Timing.SpreadText;
    /// <summary>In heartbeats, its share of Lean's default <c>maxHeartbeats</c>; empty in time.</summary>
    public string OfLimit => Timing.Unit == ProfileUnit.Heartbeats ? $"{Timing.Value / DeclarationHeartbeats.DefaultLimit:P0} of limit" : "";
    /// <summary>In heartbeats, it uses half the default limit or more: a small change could push it over.</summary>
    public bool NearLimit => Timing.Unit == ProfileUnit.Heartbeats && Timing.Value >= DeclarationHeartbeats.DefaultLimit / 2;
    /// <summary>It costs more than it did in the baseline (or is new there).</summary>
    public bool IsSlower => Change.StartsWith('+') || Change == "new";
    /// <summary>It costs less than it did in the baseline.</summary>
    public bool IsFaster => Change.StartsWith('−');
    /// <summary>The cost is hot (see <see cref="DeclarationTiming.Heat"/>).</summary>
    public bool IsHot => Timing.Heat == 2;
    /// <summary>The cost is warm.</summary>
    public bool IsWarm => Timing.Heat == 1;
    /// <summary>The bar's width in pixels: up to 120, in proportion to the costliest, and at least 2.</summary>
    public double BarWidth => Math.Max(2, 120 * Timing.Value / Math.Max(Slowest, 1e-9));
}

/// <summary>A labelled bar in one of the Profiler panel's lists: a category, one of Lean's profiler lines, a hot step, a counter.</summary>
/// <param name="Name">What the bar is for.</param>
/// <param name="Value">Its figure, formatted.</param>
/// <param name="Width">The bar's width in pixels.</param>
/// <param name="Note">A second, dimmer label: a share, a count, a kind of work.</param>
/// <param name="Color">The bar's colour: the kind of work it is.</param>
public sealed record ProfileBar(string Name, string Value, double Width, string Note, IBrush Color);

/// <summary>A tactic line's cost in the Profiler panel.</summary>
/// <param name="Path">The file.</param>
/// <param name="Line">The 0-based line.</param>
/// <param name="Code">The line's text, trimmed.</param>
/// <param name="Cost">Its cost, formatted.</param>
/// <param name="Width">The bar's width in pixels.</param>
/// <param name="Heat">0 cool, 1 warm, 2 hot.</param>
public sealed record LineCostItem(string Path, int Line, string Code, string Cost, double Width, int Heat)
{
    /// <summary>The 1-based line, as <c>line 12</c>.</summary>
    public string Where => $"line {Line + 1}";
    /// <summary>Hot.</summary>
    public bool IsHot => Heat == 2;
    /// <summary>Warm.</summary>
    public bool IsWarm => Heat == 1;
}

/// <summary>One file of a project profile.</summary>
/// <param name="Path">The file.</param>
/// <param name="Report">What its profile found.</param>
/// <param name="Error">Why Lean could not profile it, or null.</param>
/// <param name="Width">The bar's width in pixels.</param>
public sealed record ProfiledFile(string Path, ProfileReport Report, string? Error, double Width)
{
    /// <summary>The file's name.</summary>
    public string Name => System.IO.Path.GetFileName(Path);
    /// <summary>Its total, formatted, or why there is none.</summary>
    public string Total => Error is null ? DeclarationTiming.Format(Report.Total, Report.Unit) : "failed";
    /// <summary>How many declarations it has that took measurable time, or the first line of the error.</summary>
    public string Detail => Error is string e ? e.Split('\n')[0] : $"{Report.Declarations.Count} declaration{(Report.Declarations.Count == 1 ? "" : "s")}"
        + (Report.Declarations.FirstOrDefault() is DeclarationTiming top ? $" · costliest {top.Name} ({top.Time})" : "");
    /// <summary>Lean could not profile it.</summary>
    public bool Failed => Error is not null;
}

/// <summary>
/// The profiler: Lean's own profilers run over the active file, one declaration, or the whole project, and what
/// they found shown in the Profiler panel (each declaration's cost, a flame graph of its trace, where the time goes
/// across the file, Lean's categories, the cost of each tactic line, and what each declaration made Lean do) and
/// in the editor (a time on each declaration and its costly tactic lines, tinted warmer the slower). A profile can
/// be pinned as a baseline, so the next one shows what each change did.
/// </summary>
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _profileCts;
    private IReadOnlyList<(string Path, ProfileReport Report, string? Error)>? _projectProfile;

    /// <summary>The declarations of the last profile (or the costliest of a project profile), costliest first.</summary>
    public ObservableList<TimingItem> TimingItems { get; } = new();

    /// <summary>Lean's categories for the profile shown, as bars.</summary>
    public ObservableList<ProfileBar> ProfileCategories { get; } = new();

    /// <summary>Where the cost goes, bottom up: steps summed by what they are, for the selection or the whole file.</summary>
    public ObservableList<ProfileBar> HotSteps { get; } = new();

    /// <summary>Lean's own profiler lines for the selected declaration (tactics by name, instance problems, type checking).</summary>
    public ObservableList<ProfileBar> ProfileLines { get; } = new();

    /// <summary>What the selection (or the whole file) made Lean do: simp lemmas, instances, unfoldings.</summary>
    public ObservableList<ProfileBar> ProfileCounterBars { get; } = new();

    /// <summary>The costliest tactic lines of the selection (or the whole file).</summary>
    public ObservableList<LineCostItem> LineCosts { get; } = new();

    /// <summary>The files of the last project profile, costliest first.</summary>
    public ObservableList<ProfiledFile> ProfiledFiles { get; } = new();

    /// <summary>The profile shown in the panel (the active file's, or the one picked from a project profile).</summary>
    [ObservableProperty]
    private ProfileReport? _profile;

    /// <summary>The profile the next ones are compared with, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBaseline))]
    private ProfileReport? _profileBaseline;

    /// <summary>A baseline is pinned.</summary>
    public bool HasBaseline => ProfileBaseline is not null;

    /// <summary>The declaration picked in the list; the details show it, or the whole file when null.</summary>
    [ObservableProperty]
    private TimingItem? _selectedTiming;

    /// <summary>The flame graph's tree: the selected declaration's trace, or every declaration's under the file.</summary>
    [ObservableProperty]
    private ProfileNode? _flameRoot;

    /// <summary>What the profile shown counts.</summary>
    [ObservableProperty]
    private ProfileUnit _profileUnit;

    /// <summary>What the details are about: a declaration, or the whole file.</summary>
    [ObservableProperty]
    private string _profileDetailTitle = "";

    /// <summary>What the panel says above its list: how to profile, progress, or the total.</summary>
    [ObservableProperty]
    private string _timingStatus = "Lean ▸ Profile File runs Lean's profilers over the file: how long each declaration takes to check, the steps inside it as a flame graph, the tactic lines the time is spent on, and what it makes Lean do.";

    /// <summary>A profile is running; another is not started until it finishes (or is stopped).</summary>
    [ObservableProperty]
    private bool _isProfiling;

    /// <summary>Profile in heartbeats (deterministic, the unit of <c>maxHeartbeats</c>) rather than time.</summary>
    [ObservableProperty]
    private bool _profileHeartbeats;

    /// <summary>How many runs a time profile takes the median of: 0 for 1, 1 for 3, 2 for 5.</summary>
    [ObservableProperty]
    private int _profileRunsIndex;

    /// <summary>Also count the simp lemmas, instances and unfoldings of each declaration (one more run of Lean).</summary>
    [ObservableProperty]
    private bool _profileCounters = true;

    /// <summary>Which of the details tabs is showing.</summary>
    [ObservableProperty]
    private int _profileDetailTab;

    /// <summary>The runs a time profile takes the median of.</summary>
    public int ProfileRuns => ProfileRunsIndex switch { 1 => 3, 2 => 5, _ => 1 };

    partial void OnSelectedTimingChanged(TimingItem? value) => ShowProfileDetail();

    private ProfileOptions CurrentProfileOptions(int? line = null) =>
        new(ProfileHeartbeats ? ProfileUnit.Heartbeats : ProfileUnit.Seconds, ProfileRuns, ProfileCounters, line);

    /// <summary>
    /// Run the active Lean file's current text through Lean's profilers (separate <c>lean</c> processes, not the
    /// server) and show what they found in the Profiler panel, and each declaration's time in the editor until the
    /// next edit. Works without a project too, using the file's folder.
    /// </summary>
    [RelayCommand]
    public Task ProfileFileAsync() => ProfileAsync(null);

    /// <summary>Profile only the declaration at the cursor: the file is cut after it, so it is quicker to repeat.</summary>
    [RelayCommand]
    public Task ProfileDeclarationAsync() => ActiveDocument is { IsLean: true } d ? ProfileAsync(d.CaretLine) : Task.CompletedTask;

    private async Task ProfileAsync(int? line)
    {
        if (ActiveDocument is not { IsLean: true } d || IsProfiling)
        {
            return;
        }
        BottomTab = TimingPanel;
        IsProfiling = true;
        _profileCts = new CancellationTokenSource();
        string text = d.Document.Text;
        string name = Path.GetFileName(d.Path);
        ProfileOptions options = CurrentProfileOptions(line);
        if (line is int l)
        {
            int owner = Profiler.OwnerOf(text.Split('\n'), l);
            name = Profiler.DeclarationName(text.Split('\n')[owner]) ?? $"line {owner + 1}";
        }
        var progress = new Progress<string>(what => TimingStatus = $"Profiling {name}: {what}…");
        TimingStatus = $"Profiling {name}…";
        try
        {
            var (report, error) = await Profiler.RunAsync(ProjectFor(d), d.Path, text, options, progress, _profileCts.Token);
            if (error is not null)
            {
                TimingStatus = "Lean could not profile this file. " + error.Split('\n')[0];
                Log("Profile: " + error);
                return;
            }
            _projectProfile = null;
            ProfiledFiles.Reset([]);
            if (d.Document.Text == text)
            {
                d.Timings = line is null
                    ? report.Declarations
                    : [.. d.Timings.Where(t => t.Line != report.OnlyLine && t.Unit == report.Unit), .. report.Declarations];
            }
            ShowProfile(report);
            if (line is not null && TimingItems.Count > 0)
            {
                SelectedTiming = TimingItems[0];
            }
            Log("Profile: " + TimingStatus);
        }
        catch (OperationCanceledException)
        {
            TimingStatus = "Profiling stopped.";
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TimingStatus = "Could not run Lean: " + e.Message;
        }
        finally
        {
            IsProfiling = false;
            _profileCts.Dispose();
            _profileCts = null;
        }
    }

    /// <summary>
    /// Profile every Lean file of the project as saved, one after another, then list the files by cost and the
    /// costliest declarations across all of them. Asks first when there are more than 25 files.
    /// </summary>
    [RelayCommand]
    public async Task ProfileProjectAsync()
    {
        if (Project is not LeanProject project || IsProfiling)
        {
            return;
        }
        List<string> files = project.SourceFiles()
            .Where(f => Path.GetFileName(f) != "lakefile.lean")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            TimingStatus = "The project has no Lean files to profile.";
            return;
        }
        if (files.Count > 25 && !await _dialogs.ConfirmAsync("Profile the whole project?",
                $"This runs Lean over each of the project's {files.Count} files, one after another, which takes as long as checking them all. You can stop it at any time."))
        {
            return;
        }
        BottomTab = TimingPanel;
        IsProfiling = true;
        _profileCts = new CancellationTokenSource();
        var progress = new Progress<(int Done, int Total, string Path)>(p =>
            TimingStatus = $"Profiling the project: {p.Done + 1} of {p.Total}, {Path.GetRelativePath(project.Root, p.Path)}…");
        try
        {
            var results = await Profiler.RunProjectAsync(project, files, CurrentProfileOptions() with { Counters = false }, progress, _profileCts.Token);
            ShowProjectProfile(project, results);
        }
        catch (OperationCanceledException)
        {
            TimingStatus = "Profiling stopped.";
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TimingStatus = "Could not run Lean: " + e.Message;
        }
        finally
        {
            IsProfiling = false;
            _profileCts.Dispose();
            _profileCts = null;
        }
    }

    /// <summary>Stop the profile that is running (its Lean process is killed).</summary>
    [RelayCommand]
    private void StopProfiling() => _profileCts?.Cancel();

    /// <summary>Pin the profile shown as the baseline: the next profiles of the same file show each declaration's change from it.</summary>
    [RelayCommand]
    private void SetProfileBaseline()
    {
        if (Profile is not ProfileReport p)
        {
            return;
        }
        ProfileBaseline = p;
        TimingStatus = $"Baseline set: {Path.GetFileName(p.Path)} at {DeclarationTiming.Format(p.Total, p.Unit)}. Change the file and profile it again to see what the change did.";
    }

    /// <summary>Forget the baseline.</summary>
    [RelayCommand]
    private void ClearProfileBaseline()
    {
        ProfileBaseline = null;
        if (Profile is ProfileReport p)
        {
            ShowProfile(p);
        }
    }

    /// <summary>Copy the profile shown as Markdown (with the change from the baseline, if there is one), for an issue or a chat.</summary>
    [RelayCommand]
    private async Task CopyProfileReportAsync()
    {
        if (Profile is ProfileReport p)
        {
            await _dialogs.CopyTextAsync(Profiler.ToMarkdown(p, BaselineFor(p)));
            TimingStatus = "Copied the profile as Markdown.";
        }
    }

    /// <summary>
    /// Save the active file's trace profile in the Firefox Profiler's format, show the file, and open the Firefox
    /// Profiler's site, where it can be loaded for its call tree, flame graph and timeline.
    /// </summary>
    [RelayCommand]
    private async Task ExportFirefoxProfileAsync()
    {
        if (ActiveDocument is not { IsLean: true } d || IsProfiling)
        {
            return;
        }
        string? path = await _dialogs.SaveFileAsync("Save profile for the Firefox Profiler", Path.GetFileNameWithoutExtension(d.Path) + ".profile.json", Path.GetDirectoryName(d.Path));
        if (path is null)
        {
            return;
        }
        IsProfiling = true;
        _profileCts = new CancellationTokenSource();
        TimingStatus = $"Profiling {Path.GetFileName(d.Path)} for the Firefox Profiler…";
        try
        {
            string? error = await Profiler.ExportFirefoxAsync(ProjectFor(d), d.Path, d.Document.Text, path, ProfileHeartbeats ? ProfileUnit.Heartbeats : ProfileUnit.Seconds, _profileCts.Token);
            if (error is not null)
            {
                TimingStatus = "Lean could not write the profile. " + error.Split('\n')[0];
                Log("Profile export: " + error);
                return;
            }
            TimingStatus = $"Saved {Path.GetFileName(path)}. Load it at profiler.firefox.com (Load a profile from file) for its call tree and timeline.";
            await _dialogs.RevealAsync(path);
            await _dialogs.LaunchAsync(new Uri("https://profiler.firefox.com/"));
        }
        catch (OperationCanceledException)
        {
            TimingStatus = "Profiling stopped.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            TimingStatus = "Could not save the profile: " + e.Message;
        }
        finally
        {
            IsProfiling = false;
            _profileCts.Dispose();
            _profileCts = null;
        }
    }

    /// <summary>Go to a profiled declaration.</summary>
    /// <param name="t">The declaration; null does nothing.</param>
    [RelayCommand]
    private async Task OpenTimingAsync(TimingItem? t)
    {
        if (t is not null)
        {
            await OpenFileAsync(t.Path, t.Timing.Line, 0);
        }
    }

    /// <summary>Go to a costly tactic line.</summary>
    /// <param name="l">The line; null does nothing.</param>
    [RelayCommand]
    private async Task OpenLineCostAsync(LineCostItem? l)
    {
        if (l is not null)
        {
            await OpenFileAsync(l.Path, l.Line, 0);
        }
    }

    /// <summary>Show one file of the last project profile in the panel: its declarations, categories and details.</summary>
    /// <param name="f">The file; null does nothing.</param>
    [RelayCommand]
    private void ShowProfiledFile(ProfiledFile? f)
    {
        if (f is null || f.Failed)
        {
            return;
        }
        ShowProfile(f.Report, keepFiles: true);
    }

    /// <summary>Show the details of the whole profile rather than one declaration.</summary>
    [RelayCommand]
    private void ShowWholeProfile() => SelectedTiming = null;

    /// <summary>Go back from one file of the last project profile to the whole project.</summary>
    [RelayCommand]
    private void ShowWholeProjectProfile()
    {
        if (_projectProfile is { } results && Project is LeanProject project)
        {
            ShowProjectProfile(project, results);
        }
    }

    /// <summary>The baseline to compare <paramref name="p"/> with: one of the same file and unit, or null.</summary>
    private ProfileReport? BaselineFor(ProfileReport p) =>
        ProfileBaseline is ProfileReport b && b.Path == p.Path && b.Unit == p.Unit && !ReferenceEquals(b, p) ? b : null;

    /// <summary>Show a file's profile in the panel.</summary>
    private void ShowProfile(ProfileReport report, bool keepFiles = false)
    {
        Profile = report;
        ProfileUnit = report.Unit;
        ProfileReport? baseline = BaselineFor(report);
        Dictionary<string, TimingChange> changes = baseline is null ? [] : Profiler.Compare(baseline, report).ToDictionary(c => c.Name);
        double slowest = report.Declarations.Count == 0 ? 1 : report.Declarations.Max(t => t.Value);
        TimingItems.Reset(report.Declarations.Select(t =>
            new TimingItem(t, slowest, report.Path, Change: changes.TryGetValue(t.Name, out TimingChange? c) ? c.Describe(report.Unit) : "")));
        string file = Path.GetFileName(report.Path);
        string unit = report.Unit == ProfileUnit.Heartbeats ? " (heartbeats, in maxHeartbeats units: the same on every run)" : "";
        string runs = report.Runs > 1 ? $", the median of {report.Runs} runs" : "";
        string imports = report.ImportSeconds > 0 ? $" Loading the imports took {DeclarationTiming.Format(report.ImportSeconds)} more." : "";
        string errors = report.Errors > 0 ? $" The file has {report.Errors} error{(report.Errors == 1 ? "" : "s")}: what follows one may not have been fully checked." : "";
        string compared = baseline is null ? ""
            : $" Baseline {DeclarationTiming.Format(baseline.Total, baseline.Unit)} → now {DeclarationTiming.Format(report.Total, report.Unit)} ({new TimingChange("", baseline.Total, report.Total).Describe(report.Unit)}).";
        string gone = changes.Values.Where(c => c.After is null).Select(c => c.Name).Take(5) is var g && g.Any() ? $" No longer measurable: {string.Join(", ", g)}." : "";
        TimingStatus = report.Declarations.Count == 0
            ? (report.OnlyLine is not null ? "That declaration" : "Nothing in this file") + $" takes Lean more than {(report.Unit == ProfileUnit.Heartbeats ? "20 heartbeats" : "a few milliseconds")} to check.{imports}{errors}"
            : report.OnlyLine is not null
                ? $"{file}, {report.Declarations[0].Name} alone: {report.Declarations[0].Time}{unit}{runs}. Only the lines above it were checked with it.{compared}{errors}"
                : $"{file}: {DeclarationTiming.Format(report.Total, report.Unit)} across {report.Declarations.Count} declaration{(report.Declarations.Count == 1 ? "" : "s")}{unit}{runs}, costliest first.{compared}{gone}{imports}{errors}";
        double catMax = report.Categories.Where(c => c.Name is not ("import" or "initialization")).Select(c => c.Seconds).DefaultIfEmpty(1).Max();
        double catTotal = report.Categories.Where(c => c.Name is not ("import" or "initialization")).Sum(c => c.Seconds);
        ProfileCategories.Reset(report.Categories.Where(c => c.Name is not ("import" or "initialization") && c.Seconds >= 0.0005)
            .Select(c => new ProfileBar(c.Name, DeclarationTiming.Format(c.Seconds), Bar(c.Seconds, catMax), catTotal > 0 ? $"{c.Seconds / catTotal:P0}" : "", Views.FlameGraph.BrushOf(CategoryKind(c.Name)))));
        if (!keepFiles)
        {
            ProfiledFiles.Reset([]);
        }
        if (ProfileDetailTab is FilesDetailTab or BuildDetailTab && !keepFiles)
        {
            ProfileDetailTab = 0; // a file's profile opens on its flame graph
        }
        SelectedTiming = null;
        ShowProfileDetail();
    }

    /// <summary>Show a project profile: the files by cost, and the costliest declarations of all of them.</summary>
    private void ShowProjectProfile(LeanProject project, IReadOnlyList<(string Path, ProfileReport Report, string? Error)> results)
    {
        _projectProfile = results;
        double maxFile = results.Select(r => r.Report.Total).DefaultIfEmpty(1).Max();
        ProfiledFiles.Reset(results.OrderByDescending(r => r.Error is null).ThenByDescending(r => r.Report.Total)
            .Select(r => new ProfiledFile(r.Path, r.Report, r.Error, Bar(r.Report.Total, maxFile))));
        ProfileUnit unit = results.FirstOrDefault().Report?.Unit ?? ProfileUnit.Seconds;
        var all = results.Where(r => r.Error is null).SelectMany(r => r.Report.Declarations.Select(d => (r.Path, d))).OrderByDescending(x => x.d.Value).Take(200).ToList();
        double slowest = all.Count == 0 ? 1 : all[0].d.Value;
        // The categories summed over every file; the list, the costliest declarations of the whole project.
        var categories = results.SelectMany(r => r.Report.Categories).GroupBy(c => c.Name).Select(g => new ProfileCategory(g.Key, g.Sum(c => c.Seconds))).OrderByDescending(c => c.Seconds).ToList();
        ShowProfile(new ProfileReport(all.Select(x => x.d).ToList(), unit) { Path = project.Root, Categories = categories }, keepFiles: true);
        TimingItems.Reset(all.Select(x => new TimingItem(x.d, slowest, x.Path, ShowFile: true)));
        ShowProfileDetail();
        int failed = results.Count(r => r.Error is not null);
        double total = results.Sum(r => r.Report.Total);
        TimingStatus = $"The project: {DeclarationTiming.Format(total, unit)} over {results.Count - failed} file{(results.Count - failed == 1 ? "" : "s")}, the costliest declarations of all of them first."
            + (failed > 0 ? $" Lean could not check {failed} file{(failed == 1 ? "" : "s")} (build the project first)." : "")
            + " Pick a file under Files to see it alone.";
        ProfileDetailTab = FilesDetailTab;
        Log("Profile: " + TimingStatus);
    }

    /// <summary>The details tabs listing a project profile's files, and the last build's modules.</summary>
    public const int FilesDetailTab = 5, BuildDetailTab = 6;

    /// <summary>Fill the details (flame graph, hot steps, Lean's lines, counters, tactic lines) for the selection, or the whole file.</summary>
    private void ShowProfileDetail()
    {
        if (Profile is not ProfileReport report)
        {
            FlameRoot = null;
            return;
        }
        DeclarationTiming? sel = SelectedTiming?.Timing;
        IReadOnlyList<TimingItem> items = SelectedTiming is TimingItem only ? [only] : TimingItems.ToList();
        IReadOnlyList<DeclarationTiming> scope = items.Select(i => i.Timing).ToList();
        var scoped = new ProfileReport(scope, report.Unit);
        ProfileUnit u = report.Unit;
        ProfileDetailTitle = sel is not null ? $"{sel.Name} ({SelectedTiming!.Where})"
            : TimingItems.FirstOrDefault()?.ShowFile == true ? "The whole project" : "The whole file";
        FlameRoot = sel is null
            ? new ProfileNode("file", Path.GetFileName(report.Path), report.Total, false,
                report.Declarations.OrderBy(d => d.Line).Select(d => new ProfileNode("declaration", d.Name, d.Value, false, d.Trace)).ToList())
            : new ProfileNode("declaration", sel.Name, sel.Value, false, sel.Trace);

        var hot = scoped.HotSteps(60);
        double hotMax = hot.Select(h => h.Self).DefaultIfEmpty(1).Max();
        HotSteps.Reset(hot.Select(h => new ProfileBar(h.Label.Length == 0 ? h.Category : h.Label, DeclarationTiming.Format(h.Self, u), Bar(h.Self, hotMax),
            $"{ProfileNode.KindOf(h.Category)}{(h.Count > 1 ? $" · {h.Count}×" : "")}", Views.FlameGraph.BrushOf(ProfileNode.KindOf(h.Category)))));

        var lines = scope.SelectMany(d => d.Steps).GroupBy(s => s.What)
            .Select(g => new ProfileStep(g.Key, g.Sum(s => s.Seconds), g.Sum(s => s.Count))).OrderByDescending(s => s.Seconds).Take(60).ToList();
        double linesMax = lines.Select(s => s.Seconds).DefaultIfEmpty(1).Max();
        ProfileLines.Reset(lines.Select(s => new ProfileBar(s.What, DeclarationTiming.Format(s.Seconds), Bar(s.Seconds, linesMax), s.Count > 1 ? $"{s.Count}×" : "",
            Views.FlameGraph.BrushOf(CategoryKind(s.Category)))));

        var counters = scoped.Counters(80);
        double countMax = counters.Select(c => (double)c.Count).DefaultIfEmpty(1).Max();
        ProfileCounterBars.Reset(counters.Select(c => new ProfileBar(c.Name, c.Detail, Bar(c.Count, countMax), c.Kind, Views.FlameGraph.BrushOf(CounterKind(c.Kind)))));

        var texts = new Dictionary<string, string[]>();
        string Code(string file, int line)
        {
            if (!texts.TryGetValue(file, out string[]? t))
            {
                t = Documents.FirstOrDefault(d => d.Path == file)?.Document.Text.Split('\n')
                    ?? (File.Exists(file) ? File.ReadAllText(file).Split('\n') : []);
                texts[file] = t;
            }
            return line < t.Length ? t[line].Trim() : "";
        }
        var costs = items.SelectMany(i => i.Timing.LineCosts.Select(kv => (i.Path, Line: kv.Key, Cost: kv.Value))).OrderByDescending(x => x.Cost).Take(40).ToList();
        double costMax = costs.Select(c => c.Cost).DefaultIfEmpty(1).Max();
        LineCosts.Reset(costs.Select(c => new LineCostItem(c.Path, c.Line, Code(c.Path, c.Line), DeclarationTiming.Format(c.Cost, u),
            Bar(c.Cost, costMax), DeclarationTiming.HeatOf(c.Cost, u))));
    }

    private static double Bar(double value, double max) => Math.Max(2, 120 * value / Math.Max(max, 1e-9));

    /// <summary>The flame graph's kind of work for one of Lean's profiling categories, for its colour.</summary>
    private static string CategoryKind(string category) => category switch
    {
        "typeclass inference" => "instances",
        "simp" or "congr simp thm" => "simp",
        "type checking" => "kernel",
        "tactic execution" or "elaboration" => "elaboration",
        "parsing" => "meta",
        _ when category.StartsWith("compilation", StringComparison.Ordinal) || category == "interpretation" => "compiler",
        _ when category.StartsWith("tactic", StringComparison.Ordinal) => "elaboration",
        _ => "other",
    };

    /// <summary>The flame graph's kind of work for a kind of counter, for its colour.</summary>
    private static string CounterKind(string kind) => kind switch
    {
        _ when kind.StartsWith("simp", StringComparison.Ordinal) => "simp",
        _ when kind.StartsWith("instances", StringComparison.Ordinal) => "instances",
        "unfolded by the kernel" => "kernel",
        "unification heuristics" => "unification",
        _ => "reduction",
    };
}
