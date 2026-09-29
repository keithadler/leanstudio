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
/// <param name="Dependents">
/// Also check up to this many of the project's files that import a changed one (the nearest first): a new simp
/// lemma or instance often slows down the files that use it, not its own. Their "before" needs the revision built,
/// so the revision is checked out in a worktree and built there (sharing the dependencies). 0 checks only the changed
/// files, with no build: each is compared with its old text under today's imports.
/// </param>
public sealed record CheckOptions(string Against = "main", double MaxRegression = 0.1, double MinDelta = 1000, double MaxShareOfLimit = 0.5, bool AllFiles = false,
    int Dependents = 20);

/// <summary>One declaration of a regression check.</summary>
/// <param name="Path">Its file.</param>
/// <param name="Line">Its 0-based line now.</param>
/// <param name="Name">Its name.</param>
/// <param name="Before">Its heartbeats at the revision compared with, or null (new, or under the threshold).</param>
/// <param name="After">Its heartbeats now, or null (gone, or now under the threshold).</param>
/// <param name="Limit">Its <c>maxHeartbeats</c> (from <c>set_option maxHeartbeats</c>, or the default), 0 for none.</param>
/// <param name="Problem">Why it fails the check, or null when it passes.</param>
/// <param name="Dependent">Its file did not change: something it imports did.</param>
/// <param name="WasUnder">
/// It was there at the revision, but too cheap for Lean to report (under 20 heartbeats), so <paramref name="Before"/>
/// is null although it is not new.
/// </param>
public sealed record CheckedDeclaration(string Path, int Line, string Name, double? Before, double? After, double Limit, string? Problem, bool Dependent = false,
    bool WasUnder = false)
{
    /// <summary>The change, as <see cref="TimingChange"/> describes it (or from "under 20" when it was too cheap to measure).</summary>
    public string Change => WasUnder && After is double a
        ? $"+{a.ToString("N0", CultureInfo.InvariantCulture)} hb (was under {Profiler.HeartbeatThreshold / 1000})"
        : new TimingChange(Name, Before, After).Describe(ProfileUnit.Heartbeats);

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
    /// <summary>How many of <see cref="Files"/> were checked because they import a changed file.</summary>
    public int DependentFiles { get; init; }

    /// <summary>The revision was built in a worktree, so each "before" is the file as it was, with its imports as they were.</summary>
    public bool Built { get; init; }

    /// <summary>What to know about the figures, such as a change of toolchain between the two.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

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
    /// <param name="progress">Told what is happening, in words: the build of the revision, then each file.</param>
    /// <param name="ct">Cancels the check (the running Lean or Lake process is killed).</param>
    /// <exception cref="InvalidOperationException">The project is not in git, or the revision does not exist.</exception>
    public static async Task<CheckReport> RunAsync(LeanProject project, CheckOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        GitRepository git = GitRepository.Find(project.Root) ?? throw new InvalidOperationException("The project is not in a git repository, so there is nothing to compare with.");
        string commit = await git.ShortHashAsync(options.Against, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"git has no revision named {options.Against}.");
        IEnumerable<string> candidates = options.AllFiles ? project.SourceFiles() : await git.ChangedSinceAsync(options.Against, ct).ConfigureAwait(false);
        List<string> changed = candidates.Where(f => IsProjectSource(project, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        bool build = options.Dependents > 0 && project.IsLakeProject;
        List<string> dependents = build && !options.AllFiles && changed.Count > 0 ? NearestDependents(project, changed, options.Dependents) : [];
        List<string> files = [.. changed, .. dependents];
        var notes = new List<string>();
        var checkedFiles = new List<(string, string?)>();
        var declarations = new List<CheckedDeclaration>();
        OldTree? old = null;
        if (build && files.Count > 0)
        {
            progress?.Report($"building {options.Against} ({commit}) in a worktree, for the files as they were");
            old = await OldTree.PrepareAsync(git, project, commit, files, ct).ConfigureAwait(false);
            if (old.Toolchain != ProfileStore.Toolchain(project))
            {
                notes.Add($"The toolchain changed ({old.Toolchain} then, {ProfileStore.Toolchain(project)} now), so part of each change may be Lean's.");
            }
        }
        var profile = new ProfileOptions(ProfileUnit.Heartbeats);
        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i];
            bool dependent = i >= changed.Count;
            progress?.Report($"[{i + 1}/{files.Count}] {Path.GetRelativePath(project.Root, path)}{(dependent ? " (imports a changed file)" : "")}");
            string now = (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Replace("\r\n", "\n", StringComparison.Ordinal);
            (ProfileReport after, string? error) = await Profiler.RunAsync(project, path, now, profile, null, ct).ConfigureAwait(false);
            if (error is not null)
            {
                checkedFiles.Add((path, error));
                continue;
            }
            ProfileReport? before = null;
            string[]? oldLines = null;
            if (old is not null)
            {
                string then = old.PathOf(path);
                if (File.Exists(then))
                {
                    string thenText = (await File.ReadAllTextAsync(then, ct).ConfigureAwait(false)).Replace("\r\n", "\n", StringComparison.Ordinal);
                    oldLines = thenText.Split('\n');
                    if (old.BuildError is string berr)
                    {
                        checkedFiles.Add((path, $"could not build it at {options.Against}: {berr}"));
                        continue;
                    }
                    (ProfileReport b, string? perr) = await Profiler.RunAsync(old.Project, then, thenText, profile, null, ct).ConfigureAwait(false);
                    if (perr is not null)
                    {
                        checkedFiles.Add((path, $"Lean could not check it as it was at {options.Against}: {perr.Split('\n')[0]}"));
                        continue;
                    }
                    before = b;
                }
            }
            else if (await git.FileAtAsync(commit, path, ct).ConfigureAwait(false) is string then)
            {
                then = then.Replace("\r\n", "\n", StringComparison.Ordinal);
                (ProfileReport b, string? berr) = await Profiler.RunAsync(project, path, then, profile, null, ct).ConfigureAwait(false);
                before = berr is null ? b : null;
                oldLines = berr is null ? then.Split('\n') : null;
            }
            checkedFiles.Add((path, null));
            declarations.AddRange(Judge(path, now.Split('\n'), before, after, options, oldLines).Select(d => d with { Dependent = dependent }));
        }
        return new CheckReport(options.Against, commit, checkedFiles,
            declarations.OrderByDescending(d => d.Problem is not null).ThenByDescending(d => (d.After ?? 0) - (d.Before ?? 0)).ToList())
        {
            DependentFiles = dependents.Count,
            Built = old is not null,
            Notes = notes,
        };
    }

    /// <summary>A Lean source file of the project's own (not in a hidden folder such as <c>.lake</c>, not the lakefile).</summary>
    private static bool IsProjectSource(LeanProject project, string f)
    {
        string root = Path.GetFullPath(project.Root) + Path.DirectorySeparatorChar;
        return f.EndsWith(".lean", StringComparison.Ordinal) && Path.GetFullPath(f).StartsWith(root, StringComparison.Ordinal)
            && Path.GetFileName(f) != "lakefile.lean"
            && !Path.GetRelativePath(project.Root, f).Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('.'));
    }

    /// <summary>
    /// Up to <paramref name="max"/> of the project's files that import one of <paramref name="changed"/>, directly or
    /// not, and did not change themselves: the nearest first (direct importers before theirs), then by name.
    /// </summary>
    public static List<string> NearestDependents(LeanProject project, IReadOnlyList<string> changed, int max)
    {
        ImportGraph graph = ImportGraph.Build(project);
        var changedModules = changed.Select(project.ModuleNameOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(changedModules, StringComparer.Ordinal);
        var found = new List<string>();
        var level = changedModules.Order(StringComparer.Ordinal).ToList();
        while (level.Count > 0 && found.Count < max)
        {
            var next = level.SelectMany(m => graph.ImportedBy(m)).Select(e => e.Module).Where(seen.Add).Order(StringComparer.Ordinal).ToList();
            found.AddRange(next.Where(graph.Files.ContainsKey).Take(max - found.Count));
            level = next;
        }
        return found.Select(m => graph.Files[m]).Where(f => IsProjectSource(project, f)).ToList();
    }

    /// <summary>
    /// The revision compared with, checked out in a worktree under the project's <c>.lake</c> and built as far as
    /// the checked files need, sharing the project's dependencies. Kept for the next check of the same revision.
    /// </summary>
    private sealed class OldTree
    {
        private readonly string _projectRoot;
        private readonly string _oldRoot;

        private OldTree(string projectRoot, LeanProject old, string toolchain, string? buildError)
        {
            _projectRoot = projectRoot;
            _oldRoot = old.Root;
            Project = old;
            Toolchain = toolchain;
            BuildError = buildError;
        }

        /// <summary>The project as it was.</summary>
        public LeanProject Project { get; }

        /// <summary>Its <c>lean-toolchain</c>.</summary>
        public string Toolchain { get; }

        /// <summary>Why Lake could not build what the files need, or null.</summary>
        public string? BuildError { get; }

        /// <summary>Where a file of the project is in the old tree.</summary>
        public string PathOf(string path) => Path.Combine(_oldRoot, Path.GetRelativePath(_projectRoot, path));

        public static async Task<OldTree> PrepareAsync(GitRepository git, LeanProject project, string commit, IReadOnlyList<string> files, CancellationToken ct)
        {
            string checks = Path.Combine(project.Root, ".lake", "leanstudio", "check");
            string tree = Path.Combine(checks, commit);
            Directory.CreateDirectory(checks);
            foreach (string stale in Directory.GetDirectories(checks).Where(d => Path.GetFileName(d) != commit))
            {
                await git.RemoveWorktreeAsync(stale, ct).ConfigureAwait(false);
            }
            if (!File.Exists(Path.Combine(tree, ".git")))
            {
                if (Directory.Exists(tree))
                {
                    Directory.Delete(tree, true);
                }
                await git.AddWorktreeAsync(tree, commit, ct).ConfigureAwait(false);
            }
            var old = new LeanProject(Path.Combine(tree, Path.GetRelativePath(git.Root, project.Root)));
            // The dependencies: the project's own, when the revision pins the same ones (Mathlib is not built twice).
            string packages = Path.Combine(project.Root, ".lake", "packages"), oldPackages = Path.Combine(old.Root, ".lake", "packages");
            string manifest = Path.Combine(project.Root, "lake-manifest.json"), oldManifest = Path.Combine(old.Root, "lake-manifest.json");
            if (Directory.Exists(packages) && !Directory.Exists(oldPackages) && File.Exists(manifest) && File.Exists(oldManifest)
                && File.ReadAllText(manifest) == File.ReadAllText(oldManifest))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(oldPackages)!);
                try
                {
                    Directory.CreateSymbolicLink(oldPackages, packages);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Lake fetches them itself.
                }
            }
            else if (!Directory.Exists(oldPackages) && File.Exists(oldManifest) && File.ReadAllText(oldManifest).Contains("\"mathlib\"", StringComparison.Ordinal))
            {
                await Lake.GetCacheAsync(old, null, ct).ConfigureAwait(false);
            }
            // Build what the files import from the project, as it was (not the files themselves: they are profiled).
            var targets = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string f in files)
            {
                string then = Path.Combine(old.Root, Path.GetRelativePath(project.Root, f));
                if (File.Exists(then))
                {
                    foreach ((string imported, int _) in Workflow.ImportCheck.ImportLines(File.ReadAllText(then)))
                    {
                        if (File.Exists(Path.Combine([old.Root, .. imported.Split('.')]) + ".lean"))
                        {
                            targets.Add(imported);
                        }
                    }
                }
            }
            string? error = null;
            if (targets.Count > 0)
            {
                Processes.ProcessResult r = await Lake.BuildTargetsAsync(old, targets, null, ct).ConfigureAwait(false);
                if (!r.Success)
                {
                    string[] lines = r.Output.Split('\n');
                    error = string.Join(" ", lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(3).DefaultIfEmpty(lines.LastOrDefault(l => l.Trim().Length > 0) ?? "lake build failed")).Trim();
                }
            }
            return new OldTree(project.Root, old, ProfileStore.Toolchain(old), error);
        }
    }

    /// <summary>Compare one file's profiles and judge each declaration against the options.</summary>
    /// <param name="path">The file.</param>
    /// <param name="lines">Its text now, by line.</param>
    /// <param name="before">Its profile at the revision, or null when it did not exist.</param>
    /// <param name="after">Its profile now.</param>
    /// <param name="options">What fails.</param>
    /// <param name="oldLines">
    /// Its text at the revision, when known: a declaration there but missing from <paramref name="before"/> was too
    /// cheap to be reported, and is judged as having cost nothing, rather than as new.
    /// </param>
    public static IEnumerable<CheckedDeclaration> Judge(string path, IReadOnlyList<string> lines, ProfileReport? before, ProfileReport after, CheckOptions options,
        IReadOnlyList<string>? oldLines = null)
    {
        Dictionary<string, DeclarationTiming> now = after.Declarations.GroupBy(d => d.Name).ToDictionary(g => g.Key, g => g.First());
        HashSet<string> existed = oldLines is null ? [] : Named(oldLines);
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
            else if (c.Before is null && existed.Contains(c.Name) && c.Delta >= options.MinDelta)
            {
                problem = $"{c.Delta.ToString("N0", CultureInfo.InvariantCulture)} more heartbeats than at {options.Against}, where it was too cheap to measure";
            }
            if (problem is null && Math.Abs(c.Delta) < 1)
            {
                continue; // unchanged and fine: not worth a row
            }
            yield return new CheckedDeclaration(path, line, c.Name, c.Before, c.After, limit, problem, WasUnder: c.Before is null && existed.Contains(c.Name));
        }
    }

    /// <summary>The names the declarations of a text give themselves (see <see cref="Profiler.DeclarationName"/>).</summary>
    private static HashSet<string> Named(IReadOnlyList<string> lines) =>
        lines.Select(Profiler.DeclarationName).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>What <c>leanstudio --profile-check --help</c> prints.</summary>
    public const string Usage = """
        leanstudio --profile-check [options]

        Profiles every Lean file changed since a revision, and the files that import them, in heartbeats, as they
        are now and as they were then, and fails when a declaration got costlier or is near its maxHeartbeats.
        Heartbeats are the same on every run, so a failure is a real change. The project must be built (lake build)
        and in git.

          --project DIR         the project (default: the current folder)
          --against REV         the branch, tag or commit to compare with (default: main)
          --max-regression PCT  fail a declaration that costs more than PCT% extra (default: 10)
          --min-delta N         ...and at least N more heartbeats, so tiny ones do not fail (default: 1000)
          --max-share PCT       fail a declaration using more than PCT% of its maxHeartbeats (default: 50)
          --dependents N        also check up to N unchanged files that import a changed one, nearest first
                                (default: 20). For their "before", the revision is checked out in a worktree
                                under .lake and built there, sharing the dependencies. 0: only the changed
                                files, compared with their old text under today's imports, with no build.
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
                args.Contains("--all"),
                Value("--dependents") is string n ? int.Parse(n, CultureInfo.InvariantCulture) : 20);
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
            var progress = new Progress<string>(error.WriteLine);
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
                string where = Path.GetRelativePath(projectRoot, d.Path).Replace('\\', '/') + (d.Line >= 0 ? $":{d.Line + 1}" : "") + (d.Dependent ? " (unchanged; imports changed)" : "");
                string Hb(double? v) => v is double x ? x.ToString("N0", CultureInfo.InvariantCulture) : "";
                sb.Append(CultureInfo.InvariantCulture,
                    $"| `{d.Name.Replace("|", "\\|", StringComparison.Ordinal)}` | {where} | {Hb(d.Before)} | {Hb(d.After)} | {d.Change} | {(d.ShareOfLimit is double s ? Percent(s) : "")} | {(d.Problem is string p ? "❌ " + p : "")} |\n");
            }
        }
        foreach ((string path, string? error) in report.Files.Where(f => f.Error is not null))
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n`{Path.GetRelativePath(projectRoot, path).Replace('\\', '/')}`: Lean could not check it: {error!.Split('\n')[0]}\n");
        }
        foreach (string note in report.Notes)
        {
            sb.Append('\n').Append(note).Append('\n');
        }
        sb.Append(report.Built
            ? $"\nHeartbeats in `maxHeartbeats` units, the same on every run. Each file was compared with itself at that revision, built there with its imports as they were{(report.DependentFiles > 0 ? $"; {report.DependentFiles} of the files did not change but import one that did" : "")}.\n"
            : "\nHeartbeats in `maxHeartbeats` units, the same on every run; each changed file was compared with its text at that revision, under today's imports.\n");
        return sb.ToString();
    }
}
