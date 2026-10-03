using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Editing;
using LeanStudio.Core.Git;
using LeanStudio.Core.Workflow;

namespace LeanStudio.App.ViewModels;

/// <summary>For a formalization of a great theorem's size: what to prove next, what blocks what, how long it will take (Lean ▸ Big Projects).</summary>
public sealed partial class MainViewModel
{
    /// <summary>Run <paramref name="report"/> over the open project off the UI thread and write what it returns to Output.</summary>
    private async Task ScaleReportAsync(string status, Func<string, CancellationToken, string> report)
    {
        if (Project is null)
        {
            Log("Open a project first.");
            return;
        }
        string root = Project.Root;
        ProStatus = status;
        try
        {
            string text = await Task.Run(() => report(root, CancellationToken.None));
            foreach (string line in text.Split('\n'))
            {
                Log(line);
            }
        }
        finally
        {
            ProStatus = "";
        }
    }

    /// <summary>The sorries that can be proved now (every theorem they use is already proved), those that unblock the most first.</summary>
    [RelayCommand]
    public Task ShowNextUpAsync() => ScaleReportAsync("Working out what can be proved next…", (root, ct) => ScaleReports.NextUp(ScaleReports.ReadGraph(root, ct), root));

    /// <summary>The sorries that hold up the most other theorems.</summary>
    [RelayCommand]
    public Task ShowMostBlockingAsync() => ScaleReportAsync("Working out what blocks the most…", (root, ct) => ScaleReports.MostBlocking(ScaleReports.ReadGraph(root, ct), root));

    /// <summary>The work that can start now, shared among a number of people so that nobody's share waits on anybody else's.</summary>
    /// <param name="people">How many people, as text (the menu passes it); 5 when it is not a number.</param>
    [RelayCommand]
    public Task ShowWorkPackagesAsync(string? people)
    {
        int n = int.TryParse(people, NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p > 0 ? p : 5;
        return ScaleReportAsync("Sharing out the work…", (root, ct) => ScaleReports.Packages(ScaleReports.ReadGraph(root, ct), root, n));
    }

    /// <summary>How long each sorry has stood, from <c>git blame</c>: the oldest first.</summary>
    [RelayCommand]
    public async Task ShowSorryAgeAsync()
    {
        if (Project is null || GitRepository.Find(Project.Root) is not GitRepository repo)
        {
            Log("Sorry age: open a project that is in a Git repository.");
            return;
        }
        string root = Project.Root;
        ProStatus = "Reading git blame for every sorry…";
        try
        {
            IReadOnlyList<Marker> sorries = await Task.Run(() => Core.Workflow.Markers.Scan(root));
            IReadOnlyList<SorryAgeEntry> ages = await SorryAge.ReadAsync(repo, sorries, DateOnly.FromDateTime(DateTime.Today));
            foreach (string line in ScaleReports.Age(ages, root).Split('\n'))
            {
                Log(line);
            }
        }
        finally
        {
            ProStatus = "";
        }
    }

    /// <summary>When the sorries might run out, from how fast they have been proved over the last commits.</summary>
    [RelayCommand]
    public async Task ShowForecastAsync()
    {
        if (Project is null || GitRepository.Find(Project.Root) is not GitRepository repo)
        {
            Log("Forecast: open a project that is in a Git repository.");
            return;
        }
        ProStatus = "Reading the history of the sorries…";
        try
        {
            IReadOnlyList<SorryPoint> points = await SorryHistory.ReadAsync(repo, 60);
            Log(points.Count == 0 ? "Forecast: no history to read." : Forecast.From(points, 50).Message);
        }
        finally
        {
            ProStatus = "";
        }
    }

    /// <summary>The build's critical path: the longest chain of modules that must be built in turn, and what that says about speeding the build up.</summary>
    [RelayCommand]
    public Task ShowCriticalPathAsync()
    {
        Core.Projects.LeanProject? project = Project;
        return project is null ? ScaleReportAsync("", (_, _) => "") : ScaleReportAsync("Reading the import graph…", (_, ct) => ScaleReports.CriticalPathText(project, ct));
    }

    /// <summary>Where the active file could be split: groups of its declarations that do not use each other.</summary>
    [RelayCommand]
    public void ShowSplitAdvice()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        foreach (string line in ScaleReports.Split(d.Path, d.Document.Text).Split('\n'))
        {
            Log(line);
        }
    }

    /// <summary>The longest proofs of the project: the ones most likely to want splitting into lemmas.</summary>
    [RelayCommand]
    public Task ShowLongProofsAsync() => ScaleReportAsync("Measuring the proofs…", (root, ct) => ScaleReports.LongProofs(ScaleReports.ReadGraph(root, ct), root));

    /// <summary>
    /// Lock the statement of the theorem at the cursor, so that anything that later changes what it says (a refactor, a
    /// careless edit) is reported, however its proof or the names of its variables change. Kept in <c>.leanstudio/statement-locks.json</c>.
    /// </summary>
    [RelayCommand]
    public void LockStatement()
    {
        if (Project is null || ActiveDocument is not { IsLean: true } d)
        {
            Log("Lock: open a project and put the cursor in the theorem to lock.");
            return;
        }
        TheoremStatement? at = DuplicateStatements.Statements(d.Path, d.Document.Text).LastOrDefault(s => s.Line <= d.CaretLine);
        if (at is null)
        {
            Log("Lock: put the cursor in a theorem or lemma (its statement is read up to `:=`).");
            return;
        }
        StatementLock.Write(Project.Root, StatementLock.Lock(StatementLock.Read(Project.Root), at));
        Log($"Locked `{at.Name}`: {at.Statement}\nIt is reported from now on if this statement changes. Commit .leanstudio/statement-locks.json so that everyone is held to it.");
    }

    /// <summary>Check every locked statement against the project as it is now: unchanged, changed, or gone.</summary>
    [RelayCommand]
    public Task CheckLockedStatementsAsync() => ScaleReportAsync("Checking the locked statements…", (root, ct) =>
        ScaleReports.Locks(root, ScaleReports.ReadGraphStatements(root, ct)));

    /// <summary>The imports that reach up from a lower layer into a higher one, by the project's <c>.leanstudio/layers.json</c>.</summary>
    [RelayCommand]
    public Task CheckLayersAsync()
    {
        Core.Projects.LeanProject? project = Project;
        return project is null ? ScaleReportAsync("", (_, _) => "") : ScaleReportAsync("Checking the layers…", (_, _) => ScaleReports.Layers(project));
    }
}
