using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LeanStudio.Core.Git;
using LeanStudio.Core.Projects;

namespace LeanStudio.Core.Proofs;

/// <summary>A profile as saved: what it measured, when, at which commit, and each declaration's figure.</summary>
/// <param name="File">The file, relative to the project.</param>
/// <param name="Unit">What the values count.</param>
/// <param name="Commit">The commit checked out when it was taken (short hash), or null outside git.</param>
/// <param name="Dirty">The file had changes not in that commit.</param>
/// <param name="Toolchain">The project's <c>lean-toolchain</c>: figures from different Leans are not comparable.</param>
/// <param name="Taken">When, in UTC.</param>
/// <param name="Runs">How many runs the figures are the median of.</param>
/// <param name="Declarations">Each declaration: its name, 0-based line, value and hot spot.</param>
public sealed record SavedProfile(string File, ProfileUnit Unit, string? Commit, bool Dirty, string Toolchain, DateTime Taken, int Runs,
    IReadOnlyList<SavedDeclaration> Declarations)
{
    /// <summary>The sum of the declarations' values.</summary>
    public double Total => Declarations.Sum(d => d.Value);

    /// <summary>A short description for a list: <c>Sep 28 18:40 · abc1234 · 481 ms</c>.</summary>
    public string Describe() =>
        $"{Taken.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.InvariantCulture)} · {(Commit ?? "no commit")}{(Dirty ? " (edited)" : "")} · {DeclarationTiming.Format(Total, Unit)}";

    /// <summary>As a report, to compare with (no traces are kept).</summary>
    public ProfileReport ToReport(string path) =>
        new(Declarations.Select(d => new DeclarationTiming(d.Line, d.Declaration, d.Value, d.HotSpot, 0) { Unit = Unit }).ToList(), Unit) { Path = path, Runs = Runs };
}

/// <summary>One declaration of a <see cref="SavedProfile"/>.</summary>
/// <param name="Declaration">Its first line.</param>
/// <param name="Line">Its 0-based line.</param>
/// <param name="Value">Its cost, in the profile's unit.</param>
/// <param name="HotSpot">The step that cost the most, or null.</param>
public sealed record SavedDeclaration(string Declaration, int Line, double Value, string? HotSpot);

/// <summary>
/// Profiles kept on disk, per file, under the project's <c>.lake/leanstudio/profiles</c> (build output, so git
/// ignores it): every profile taken is saved there with the commit it was taken at, so a later one can be compared
/// with any of them, and they outlast the session.
/// </summary>
public static class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The folder a file's profiles are kept in.</summary>
    public static string FolderFor(LeanProject project, string path)
    {
        string rel = Path.GetRelativePath(project.Root, Path.GetFullPath(path));
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            rel = Path.GetFileName(path);
        }
        return Path.Combine(project.Root, ".lake", "leanstudio", "profiles", rel.Replace('\\', '/').Replace('/', '~'));
    }

    /// <summary>
    /// Save a whole-file profile (a profile of one declaration is not saved: it says nothing about the others).
    /// Returns the saved profile, or null when it was not saved. At most 50 are kept per file; the oldest go.
    /// </summary>
    /// <param name="project">The project the file is in.</param>
    /// <param name="report">The profile.</param>
    /// <param name="text">The text profiled, when it may differ from the file on disk (unsaved edits).</param>
    /// <param name="ct">Cancels the save.</param>
    public static async Task<SavedProfile?> SaveAsync(LeanProject project, ProfileReport report, string? text = null, CancellationToken ct = default)
    {
        if (report.OnlyLine is not null || report.Path.Length == 0 || !File.Exists(report.Path))
        {
            return null;
        }
        GitRepository? git = GitRepository.Find(report.Path);
        string? commit = git is null ? null : await git.ShortHashAsync("HEAD", ct).ConfigureAwait(false);
        bool dirty = false;
        if (git is not null && commit is not null)
        {
            string? committed = await git.FileAtAsync("HEAD", report.Path, ct).ConfigureAwait(false);
            text ??= await File.ReadAllTextAsync(report.Path, ct).ConfigureAwait(false);
            dirty = committed is null || committed.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd()
                != text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        }
        var saved = new SavedProfile(Path.GetRelativePath(project.Root, report.Path).Replace('\\', '/'), report.Unit, commit, dirty, Toolchain(project),
            DateTime.UtcNow, report.Runs, report.Declarations.Select(d => new SavedDeclaration(d.Declaration, d.Line, d.Value, d.HotSpot)).ToList());
        string folder = FolderFor(project, report.Path);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"{saved.Taken:yyyyMMdd'T'HHmmssfff}-{saved.Unit.ToString().ToLowerInvariant()}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(saved, Json), ct).ConfigureAwait(false);
        foreach (string old in Directory.GetFiles(folder, "*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(50))
        {
            File.Delete(old);
        }
        return saved;
    }

    /// <summary>The profiles saved for a file, newest first (unreadable ones are skipped).</summary>
    public static IReadOnlyList<SavedProfile> List(LeanProject project, string path)
    {
        string folder = FolderFor(project, path);
        if (!Directory.Exists(folder))
        {
            return [];
        }
        var list = new List<SavedProfile>();
        foreach (string f in Directory.GetFiles(folder, "*.json").OrderByDescending(f => f, StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize<SavedProfile>(File.ReadAllText(f), Json) is SavedProfile p)
                {
                    list.Add(p);
                }
            }
            catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
            {
            }
        }
        return list;
    }

    /// <summary>The project's toolchain, as its <c>lean-toolchain</c> file says, or empty.</summary>
    public static string Toolchain(LeanProject project)
    {
        string f = Path.Combine(project.Root, "lean-toolchain");
        return File.Exists(f) ? File.ReadAllText(f).Trim() : "";
    }
}

/// <summary>What a regression check allows.</summary>
/// <param name="Against">The revision to compare with: a branch, tag or commit.</param>
/// <param name="MaxRegression">A declaration fails when it costs more than this fraction extra (0.1 is 10% more)…</param>
/// <param name="MinDelta">…and at least this much more (in thousands of heartbeats), so tiny ones do not fail on noise.</param>
/// <param name="MaxShareOfLimit">A declaration fails when it uses more than this share of its <c>maxHeartbeats</c> limit.</param>
/// <param name="AllFiles">Check every file of the project, not only the ones changed since <paramref name="Against"/>.</param>
public sealed record CheckOptions(string Against = "main", double MaxRegression = 0.1, double MinDelta = 1000, double MaxShareOfLimit = 0.5, bool AllFiles = false);

/// <summary>One declaration of a regression check.</summary>
/// <param name="Path">Its file.</param>
/// <param name="Line">Its 0-based line now.</param>
/// <param name="Name">Its name.</param>
/// <param name="Before">Its heartbeats at the revision compared with, or null (new, or under the threshold).</param>
/// <param name="After">Its heartbeats now, or null (gone, or now under the threshold).</param>
/// <param name="Limit">Its <c>maxHeartbeats</c> (from <c>set_option maxHeartbeats</c>, or the default), 0 for none.</param>
/// <param name="Problem">Why it fails the check, or null when it passes.</param>
public sealed record CheckedDeclaration(string Path, int Line, string Name, double? Before, double? After, double Limit, string? Problem)
{
    /// <summary>The change, as <see cref="TimingChange"/> describes it.</summary>
    public string Change => new TimingChange(Name, Before, After).Describe(ProfileUnit.Heartbeats);

    /// <summary>Its share of its limit now, or null without one.</summary>
    public double? ShareOfLimit => Limit > 0 && After is double a ? a / Limit : null;
}

/// <summary>What a regression check found.</summary>
/// <param name="Against">The revision compared with, as given.</param>
/// <param name="Commit">Its short hash.</param>
/// <param name="Files">The files checked, each with its error if Lean could not check it.</param>
/// <param name="Declarations">Every declaration of those files that changed or fails, failures first.</param>
public sealed record CheckReport(string Against, string Commit, IReadOnlyList<(string Path, string? Error)> Files, IReadOnlyList<CheckedDeclaration> Declarations)
{
    /// <summary>The declarations that fail.</summary>
    public IReadOnlyList<CheckedDeclaration> Failures => Declarations.Where(d => d.Problem is not null).ToList();

    /// <summary>Nothing fails and every file was checked.</summary>
    public bool Passed => Failures.Count == 0 && Files.All(f => f.Error is null);
}

/// <summary>
/// The profiler's regression check, for CI or before a push: every Lean file changed since a revision is profiled
/// in heartbeats as it is now and as it was then (with today's toolchain and imports, so only the file's own change
/// counts), and each declaration is compared. Heartbeats are the same on every run and machine, so a failure is a
/// real change, not noise. A declaration fails when it got more than a set share costlier, or uses more than a set
/// share of its <c>maxHeartbeats</c> limit.
/// </summary>
public static partial class ProfileCheck
{
    /// <summary>Lean's default <c>maxHeartbeats</c>, in its units.</summary>
    public const double DefaultLimit = 200_000;

    [GeneratedRegex(@"^\s*set_option\s+maxHeartbeats\s+(\d+)(\s+in\b)?")]
    private static partial Regex MaxHeartbeats();

    /// <summary>
    /// The <c>maxHeartbeats</c> that applies to the declaration at the 0-based line <paramref name="owner"/>: a
    /// <c>set_option maxHeartbeats N in</c> just above it (or at the start of its line), else the last file-wide
    /// <c>set_option maxHeartbeats N</c> above it, else the default. 0 means no limit.
    /// </summary>
    public static double LimitAt(IReadOnlyList<string> lines, int owner)
    {
        double limit = DefaultLimit;
        for (int i = 0; i < Math.Min(owner, lines.Count); i++)
        {
            if (MaxHeartbeats().Match(lines[i]) is { Success: true } m && !m.Groups[2].Success)
            {
                limit = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }
        // `set_option … in` right above the declaration, among its doc comment and attributes.
        for (int i = Math.Min(owner, lines.Count - 1); i >= 0 && i >= owner - 12; i--)
        {
            string t = lines[i].Trim();
            if (MaxHeartbeats().Match(lines[i]) is { Success: true } m && m.Groups[2].Success)
            {
                return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            if (i < owner && t.Length == 0)
            {
                break;
            }
        }
        return limit;
    }

    /// <summary>Run the check over <paramref name="project"/>.</summary>
    /// <param name="project">The project; it must be in a git repository and built.</param>
    /// <param name="options">What to compare with and what fails.</param>
    /// <param name="progress">Told each file as it starts: how many are done, of how many, and its path.</param>
    /// <param name="ct">Cancels the check (the running Lean process is killed).</param>
    /// <exception cref="InvalidOperationException">The project is not in git, or the revision does not exist.</exception>
    public static async Task<CheckReport> RunAsync(LeanProject project, CheckOptions options, IProgress<(int Done, int Total, string Path)>? progress = null, CancellationToken ct = default)
    {
        GitRepository git = GitRepository.Find(project.Root) ?? throw new InvalidOperationException("The project is not in a git repository, so there is nothing to compare with.");
        string commit = await git.ShortHashAsync(options.Against, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"git has no revision named {options.Against}.");
        IEnumerable<string> candidates = options.AllFiles ? project.SourceFiles() : await git.ChangedSinceAsync(options.Against, ct).ConfigureAwait(false);
        string root = Path.GetFullPath(project.Root) + Path.DirectorySeparatorChar;
        List<string> files = candidates
            .Where(f => f.EndsWith(".lean", StringComparison.Ordinal) && Path.GetFullPath(f).StartsWith(root, StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != "lakefile.lean" && !Path.GetRelativePath(project.Root, f).Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('.')))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        var options1 = new ProfileOptions(ProfileUnit.Heartbeats);
        var checkedFiles = new List<(string, string?)>();
        var declarations = new List<CheckedDeclaration>();
        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i];
            progress?.Report((i, files.Count, path));
            string now = (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Replace("\r\n", "\n", StringComparison.Ordinal);
            (ProfileReport after, string? error) = await Profiler.RunAsync(project, path, now, options1, null, ct).ConfigureAwait(false);
            if (error is not null)
            {
                checkedFiles.Add((path, error));
                continue;
            }
            ProfileReport? before = null;
            if (await git.FileAtAsync(commit, path, ct).ConfigureAwait(false) is string then)
            {
                (ProfileReport b, string? berr) = await Profiler.RunAsync(project, path, then.Replace("\r\n", "\n", StringComparison.Ordinal), options1, null, ct).ConfigureAwait(false);
                before = berr is null ? b : null;
            }
            checkedFiles.Add((path, null));
            declarations.AddRange(Judge(path, now.Split('\n'), before, after, options));
        }
        return new CheckReport(options.Against, commit, checkedFiles,
            declarations.OrderByDescending(d => d.Problem is not null).ThenByDescending(d => (d.After ?? 0) - (d.Before ?? 0)).ToList());
    }

    /// <summary>Compare one file's profiles and judge each declaration against the options.</summary>
    public static IEnumerable<CheckedDeclaration> Judge(string path, IReadOnlyList<string> lines, ProfileReport? before, ProfileReport after, CheckOptions options)
    {
        Dictionary<string, DeclarationTiming> now = after.Declarations.GroupBy(d => d.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (TimingChange c in before is null ? after.Declarations.Select(d => new TimingChange(d.Name, null, d.Value)) : Profiler.Compare(before, after))
        {
            int line = now.TryGetValue(c.Name, out DeclarationTiming? t) ? t.Line : -1;
            double limit = line >= 0 ? LimitAt(lines, line) : DefaultLimit;
            string? problem = null;
            if (c.After is double a && limit > 0 && a > limit * options.MaxShareOfLimit)
            {
                problem = $"uses {Percent(a / limit)} of its maxHeartbeats ({limit.ToString("N0", CultureInfo.InvariantCulture)})";
            }
            else if (c.Before is double b && c.Delta >= options.MinDelta && c.Delta > b * options.MaxRegression)
            {
                problem = $"{Percent(b > 0 ? c.Delta / b : 1)} more heartbeats than at {options.Against}";
            }
            if (problem is null && Math.Abs(c.Delta) < 1)
            {
                continue; // unchanged and fine: not worth a row
            }
            yield return new CheckedDeclaration(path, line, c.Name, c.Before, c.After, limit, problem);
        }
    }

    /// <summary>What <c>leanstudio --profile-check --help</c> prints.</summary>
    public const string Usage = """
        leanstudio --profile-check [options]

        Profiles every Lean file changed since a revision, in heartbeats, as it is now and as it was then, and fails
        when a declaration got costlier or is near its maxHeartbeats. Heartbeats are the same on every run, so a
        failure is a real change. The project must be built (lake build) and in git.

          --project DIR         the project (default: the current folder)
          --against REV         the branch, tag or commit to compare with (default: main)
          --max-regression PCT  fail a declaration that costs more than PCT% extra (default: 10)
          --min-delta N         ...and at least N more heartbeats, so tiny ones do not fail (default: 1000)
          --max-share PCT       fail a declaration using more than PCT% of its maxHeartbeats (default: 50)
          --all                 check every file of the project, not only the changed ones
          --markdown FILE       also write the report as Markdown to FILE ($GITHUB_STEP_SUMMARY is used when set)

        Exit code: 0 passed, 1 failed, 2 could not run.

        """;

    /// <summary>
    /// Run the check from the command line (<c>leanstudio --profile-check …</c>): parse the arguments, print the
    /// report as Markdown to <paramref name="output"/> and progress to <paramref name="error"/>, and return the
    /// exit code: 0 passed, 1 failed, 2 could not run.
    /// </summary>
    public static async Task<int> RunCommandLineAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        string? Value(string name)
        {
            int i = args.ToList().IndexOf(name);
            return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
        }
        if (args.Contains("--help"))
        {
            await output.WriteAsync(Usage).ConfigureAwait(false);
            return 0;
        }
        double Percent(string name, double fallback) =>
            Value(name) is string v ? double.Parse(v.TrimEnd('%'), CultureInfo.InvariantCulture) / 100 : fallback;
        CheckOptions options;
        try
        {
            options = new CheckOptions(
                Value("--against") ?? "main",
                Percent("--max-regression", 0.1),
                Value("--min-delta") is string d ? double.Parse(d, CultureInfo.InvariantCulture) : 1000,
                Percent("--max-share", 0.5),
                args.Contains("--all"));
        }
        catch (FormatException)
        {
            await error.WriteLineAsync("A number was expected. " + Usage).ConfigureAwait(false);
            return 2;
        }
        string dir = Path.GetFullPath(Value("--project") ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(dir))
        {
            await error.WriteLineAsync($"No folder {dir}.").ConfigureAwait(false);
            return 2;
        }
        var project = new LeanProject(dir);
        CheckReport report;
        try
        {
            var progress = new Progress<(int Done, int Total, string Path)>(p =>
                error.WriteLine($"[{p.Done + 1}/{p.Total}] {Path.GetRelativePath(dir, p.Path)}"));
            report = await RunAsync(project, options, progress, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException e)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }
        string markdown = ToMarkdown(report, dir);
        await output.WriteAsync(markdown).ConfigureAwait(false);
        foreach (string? file in new[] { Value("--markdown"), Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") }.Distinct())
        {
            if (!string.IsNullOrEmpty(file))
            {
                await File.AppendAllTextAsync(file, markdown + "\n", ct).ConfigureAwait(false);
            }
        }
        return report.Passed ? 0 : 1;
    }

    /// <summary>A fraction as a whole percentage, <c>60%</c>, the same in every culture.</summary>
    private static string Percent(double f) => (f * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    /// <summary>The report as Markdown, for a pull request comment or a CI summary.</summary>
    public static string ToMarkdown(CheckReport report, string projectRoot)
    {
        var sb = new StringBuilder();
        int failed = report.Failures.Count;
        sb.Append(CultureInfo.InvariantCulture, $"### Lean heartbeat check against `{report.Against}` ({report.Commit})\n\n");
        sb.Append(report.Passed
            ? $"Passed: {report.Files.Count} file{(report.Files.Count == 1 ? "" : "s")} checked, no declaration regressed or is near its limit.\n"
            : $"**Failed**: {failed} declaration{(failed == 1 ? "" : "s")} over the limits"
              + (report.Files.Any(f => f.Error is not null) ? $", {report.Files.Count(f => f.Error is not null)} file(s) Lean could not check" : "") + ".\n");
        if (report.Declarations.Count > 0)
        {
            sb.Append("\n| Declaration | Where | Before | Now | Change | Of limit | |\n|---|---|---:|---:|---:|---:|---|\n");
            foreach (CheckedDeclaration d in report.Declarations.Take(60))
            {
                string where = Path.GetRelativePath(projectRoot, d.Path).Replace('\\', '/') + (d.Line >= 0 ? $":{d.Line + 1}" : "");
                string Hb(double? v) => v is double x ? x.ToString("N0", CultureInfo.InvariantCulture) : "";
                sb.Append(CultureInfo.InvariantCulture,
                    $"| `{d.Name.Replace("|", "\\|", StringComparison.Ordinal)}` | {where} | {Hb(d.Before)} | {Hb(d.After)} | {d.Change} | {(d.ShareOfLimit is double s ? Percent(s) : "")} | {(d.Problem is string p ? "❌ " + p : "")} |\n");
            }
        }
        foreach ((string path, string? error) in report.Files.Where(f => f.Error is not null))
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n`{Path.GetRelativePath(projectRoot, path).Replace('\\', '/')}`: Lean could not check it: {error!.Split('\n')[0]}\n");
        }
        sb.Append("\nHeartbeats in `maxHeartbeats` units, the same on every run; each file was compared with its text at that revision, with today's toolchain and imports.\n");
        return sb.ToString();
    }
}
