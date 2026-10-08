using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Verification;
using LeanStudio.Core.Workflow;

namespace LeanStudio.Core.Toolchains;

/// <summary>What to try upgrading to.</summary>
/// <param name="To">
/// The toolchain to try (<c>leanprover/lean4:v4.35.0</c>, <c>v4.35.0</c> or <c>4.35.0</c>). <see langword="null"/>:
/// for a project with dependencies, whatever updating them brings (Mathlib's toolchain); otherwise the newest stable Lean.
/// </param>
/// <param name="UpdateDependencies">Run <c>lake update</c> in the trial, so a project with dependencies moves with them.</param>
public sealed record UpgradeOptions(string? To = null, bool UpdateDependencies = true);

/// <summary>A name the new version no longer has, and the names it does have that are likely what it became.</summary>
/// <param name="Missing">The name Lean could not find.</param>
/// <param name="File">The project file that uses it.</param>
/// <param name="Line">The 0-based line.</param>
/// <param name="Column">The 0-based column.</param>
/// <param name="Candidates">The likeliest replacements, best first; empty when nothing is close.</param>
/// <param name="SameStatement">The first candidate states exactly what the missing name stated: almost certainly a rename.</param>
public sealed record NameSuggestion(string Missing, string File, int Line, int Column, IReadOnlyList<string> Candidates, bool SameStatement);

/// <summary>What trying an upgrade found.</summary>
/// <param name="FromToolchain">The project's toolchain now.</param>
/// <param name="ToToolchain">The toolchain tried.</param>
/// <param name="TrialRoot">The copy of the project the upgrade was tried in.</param>
/// <param name="UpdatedDependencies">Dependencies were updated in the trial.</param>
/// <param name="Mathlib">Mathlib's commit before and after, when the project uses it.</param>
/// <param name="Built">The build in the trial ran to the end (with or without errors).</param>
/// <param name="Errors">The build's errors, at the project's own files.</param>
/// <param name="Deprecated">Uses of names the new version deprecates, with what Lean says to use instead, at the project's own files.</param>
/// <param name="Suggestions">For each name the new version no longer has, what it likely became.</param>
/// <param name="Problem">Why the trial could not run, or <see langword="null"/>.</param>
public sealed record UpgradeReport(
    string? FromToolchain,
    string ToToolchain,
    string TrialRoot,
    bool UpdatedDependencies,
    (string? Before, string? After)? Mathlib,
    bool Built,
    IReadOnlyList<BuildMessage> Errors,
    IReadOnlyList<DeprecatedUse> Deprecated,
    IReadOnlyList<NameSuggestion> Suggestions,
    string? Problem)
{
    /// <summary>The project builds on the new version without an error.</summary>
    public bool Clean => Problem is null && Built && Errors.Count == 0;
}

/// <summary>
/// Trying a newer Lean (or newer dependencies) without touching the project: a copy of it, under
/// <c>.lake/leanstudio/upgrade</c>, is moved to the new toolchain, its dependencies updated, and built. What broke comes
/// back by the project's own files and lines: the errors, the names the new version deprecates (with Lean's
/// replacement, to rename in one go), and for each name it no longer has, the names it does have that are likely
/// what it became, best of all one that states exactly the same thing. <see cref="AdoptAsync"/> then moves the project
/// itself.
/// </summary>
public static partial class UpgradeTrial
{
    /// <summary>Where the trial copy of <paramref name="project"/> goes.</summary>
    public static string TrialFolder(LeanProject project) => Path.Combine(project.Root, ".lake", "leanstudio", "upgrade");

    /// <summary>Where <see cref="AdoptAsync"/> keeps the files it replaces; the same folder the dependency update backs up to, so one Undo serves both.</summary>
    public static string BackupFolder(LeanProject project) => Path.Combine(project.Root, ".lake", "leanstudio-update-backup");

    /// <summary>A toolchain as Lean names it: <c>v4.35.0</c> and <c>4.35.0</c> become <c>leanprover/lean4:v4.35.0</c>.</summary>
    public static string Normalize(string toolchain)
    {
        string t = toolchain.Trim();
        if (t.Contains(':', StringComparison.Ordinal) || t is "stable" or "nightly")
        {
            return t is "stable" or "nightly" ? LeanReleases.Channel + t : t;
        }
        return LeanReleases.Channel + (char.IsDigit(t[0]) ? "v" + t : t);
    }

    /// <summary>
    /// Copy the project to <see cref="TrialFolder"/>, move the copy to the new version, build it, and report what
    /// broke. The project itself is not changed. <paramref name="latestStable"/> gives the newest stable Lean's tag
    /// when no toolchain is named (it may be null when the network is not to be used).
    /// </summary>
    public static async Task<UpgradeReport> RunAsync(
        LeanProject project,
        UpgradeOptions options,
        Func<CancellationToken, Task<string?>>? latestStable,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        string trialRoot = TrialFolder(project);
        string? from = project.Toolchain;
        bool hasDependencies = LeanReleases.HasDependencies(project);
        bool update = options.UpdateDependencies && hasDependencies;
        UpgradeReport Fail(string to, string problem) => new(from, to, trialRoot, update, null, false, [], [], [], problem);

        string? to = options.To is string named ? Normalize(named) : null;
        if (to is null && !update)
        {
            string? tag = null;
            try
            {
                tag = latestStable is null ? null : await latestStable(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                return Fail("?", "Could not find the newest Lean on GitHub: " + e.Message);
            }
            if (tag is null)
            {
                return Fail("?", "Could not find the newest stable Lean (are update checks off?). Name one with --to.");
            }
            to = LeanReleases.Channel + tag;
        }
        if (to is not null && to == from && !update)
        {
            return Fail(to, $"The project already uses {to}.");
        }

        progress?.Report($"Copying the project to {Path.GetRelativePath(project.Root, trialRoot)}…");
        CopyProject(project.Root, trialRoot, ct);
        var trial = new LeanProject(trialRoot);
        if (to is not null)
        {
            trial.SetToolchain(to);
        }
        string? mathlibBefore = project.DependsOnMathlib ? DependencyBump.ManifestRev(project, "mathlib") : null;
        if (update)
        {
            if (to is not null)
            {
                await InstallAsync(to, progress, ct).ConfigureAwait(false);
            }
            progress?.Report("Updating the dependencies (lake update)…");
            var u = await Lake.UpdateAsync(trial, line => progress?.Report(line), ct).ConfigureAwait(false);
            if (!u.Success)
            {
                return Fail(to ?? from ?? "?", "lake update failed: " + LastLines(u.Output));
            }
            // The project has to build with the Lean its dependencies were built with; Mathlib's says which.
            if (to is null)
            {
                foreach (string package in new[] { "mathlib", "batteries" })
                {
                    string pin = Path.Combine(trial.PackagesDirectory, package, LeanProject.ToolchainFile);
                    if (File.Exists(pin) && File.ReadAllText(pin).Trim() is { Length: > 0 } tc)
                    {
                        to = tc;
                        break;
                    }
                }
                to ??= from ?? "?";
                trial.SetToolchain(to);
            }
        }
        await InstallAsync(to!, progress, ct).ConfigureAwait(false);
        if (trial.DependsOnMathlib)
        {
            progress?.Report("Fetching Mathlib's build cache…");
            await Lake.GetCacheAsync(trial, line => progress?.Report(line), ct).ConfigureAwait(false);
        }
        progress?.Report($"Building with {to}…");
        var build = await Lake.BuildAsync(trial, onLine: line => progress?.Report(line), ct: ct).ConfigureAwait(false);
        IReadOnlyList<BuildMessage> messages = LakeOutput.Parse(build.Output, trialRoot)
            .Select(m => m with { Path = Back(project.Root, trialRoot, m.Path) })
            .ToList();
        // A build that stops before checking any file (a toolchain that will not run, a dependency that will not build).
        bool built = build.Success || messages.Any(m => m.IsError) || build.Output.Contains("Some required targets logged failures", StringComparison.Ordinal);
        if (!built)
        {
            return Fail(to!, "The build did not get as far as the project's files: " + LastLines(build.Output));
        }
        List<BuildMessage> errors = messages.Where(m => m.IsError).ToList();
        IReadOnlyList<DeprecatedUse> deprecated = DependencyBump.DeprecatedUses(messages);
        progress?.Report("Looking for what the missing names became…");
        IReadOnlyList<NameSuggestion> suggestions = SuggestNames(project, trial, errors, ct);
        return new UpgradeReport(from, to!, trialRoot, update,
            mathlibBefore is null && !trial.DependsOnMathlib ? null : (mathlibBefore, DependencyBump.ManifestRev(trial, "mathlib")),
            true, errors, deprecated, suggestions, null);
    }

    private static async Task InstallAsync(string toolchain, IProgress<string>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(Elan.ToolchainDirectory(toolchain)))
        {
            progress?.Report($"Installing {toolchain}…");
            await Elan.InstallAsync(toolchain, line => progress?.Report(line), ct).ConfigureAwait(false);
        }
    }

    private static string LastLines(string output) =>
        string.Join(" ", output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("trace:", StringComparison.Ordinal)).TakeLast(3));

    /// <summary>A path in the trial copy, as the same file in the project.</summary>
    private static string Back(string projectRoot, string trialRoot, string path)
    {
        foreach (string root in new[] { trialRoot, Lint.RealPath(trialRoot) })
        {
            string rel = Path.GetRelativePath(root, path);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
            {
                return Path.Combine(projectRoot, rel);
            }
        }
        return path;
    }

    /// <summary>
    /// Copy a project's own files (its sources, lakefile, manifest and toolchain file) into <paramref name="to"/>,
    /// which is emptied first. Its build (<c>.lake</c>), git folder and anything else hidden at the top are left out.
    /// </summary>
    public static void CopyProject(string from, string to, CancellationToken ct = default)
    {
        if (Directory.Exists(to))
        {
            // Git's object files are read-only, which Windows refuses to delete otherwise. A link is removed, never followed.
            foreach (string f in Directory.EnumerateFiles(to, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(to, recursive: true);
        }
        Directory.CreateDirectory(to);
        string fullTo = Path.GetFullPath(to);
        var stack = new Stack<string>([from]);
        while (stack.TryPop(out string? dir))
        {
            ct.ThrowIfCancellationRequested();
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                bool top = dir == from;
                if ((top && (name.StartsWith('.') || name is "build" or "lake-packages")) || Path.GetFullPath(sub) == fullTo
                    || new DirectoryInfo(sub).LinkTarget is not null)
                {
                    continue;
                }
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, sub)));
                stack.Push(sub);
            }
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
            }
        }
    }

    [GeneratedRegex(@"[Uu]nknown (?:identifier|constant)\s+[`'‘](?<name>[^`'’\s]+)[`'’]")]
    private static partial Regex UnknownName();

    /// <summary>
    /// For each name an error says the new version does not have, the names it does have that are likely what it
    /// became: ones whose name shares the missing name's words, closest first, and above all one whose statement is the
    /// missing name's statement as the project's current build has it (a rename, not a change).
    /// </summary>
    /// <param name="project">The project, whose current build (if any) says what each missing name stated.</param>
    /// <param name="trial">The upgraded copy, built as far as it goes.</param>
    /// <param name="errors">The trial's errors, at the project's paths.</param>
    /// <param name="ct">Cancels the search.</param>
    public static IReadOnlyList<NameSuggestion> SuggestNames(LeanProject project, LeanProject trial, IReadOnlyList<BuildMessage> errors, CancellationToken ct = default)
    {
        var missing = errors
            .Select(e => (e, m: UnknownName().Match(e.Message)))
            .Where(x => x.m.Success)
            .Select(x => (x.e, Name: x.m.Groups["name"].Value))
            .ToList();
        if (missing.Count == 0)
        {
            return [];
        }
        // What the failing files import, so names in modules nothing that built imports can be found too.
        var imports = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in missing.Select(x => x.e.Path).Distinct())
        {
            string inTrial = Path.Combine(trial.Root, Path.GetRelativePath(project.Root, file));
            if (File.Exists(inTrial))
            {
                imports.UnionWith(ImportCheck.ImportLines(File.ReadAllText(inTrial)).Keys);
            }
        }
        using TenetWorkspace now = TenetWorkspace.Open(trial, imports);
        TenetWorkspace? before = null;
        try
        {
            if (Directory.Exists(project.BuildLibDirectory))
            {
                before = TenetWorkspace.Open(project, imports);
            }
        }
        catch (Exception e) when (e is IOException or Tenet.Olean.OleanFormatException)
        {
            before = null;
        }
        try
        {
            var cache = new Dictionary<string, (IReadOnlyList<string>, bool)>(StringComparer.Ordinal);
            var list = new List<NameSuggestion>();
            foreach ((BuildMessage e, string name) in missing)
            {
                ct.ThrowIfCancellationRequested();
                if (!cache.TryGetValue(name, out var found))
                {
                    found = cache[name] = Candidates(name, now, before, ct);
                }
                list.Add(new NameSuggestion(name, e.Path, e.Line, e.Column, found.Item1, found.Item2));
            }
            return list;
        }
        finally
        {
            before?.Dispose();
        }
    }

    private static (IReadOnlyList<string> Names, bool SameStatement) Candidates(string missing, TenetWorkspace now, TenetWorkspace? before, CancellationToken ct)
    {
        string last = missing.Split('.')[^1];
        string[] words = last.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<DeclarationSummary>();
        // All the words first; then fewer, longest kept, until something matches.
        foreach (string[] query in Enumerable.Range(0, Math.Max(1, words.Length)).Select(drop => words.OrderByDescending(w => w.Length).Take(Math.Max(1, words.Length - drop)).ToArray()))
        {
            hits = now.Search(string.Join(' ', query), 400, ct).Where(h => h.Name != missing).ToList();
            if (hits.Count > 0)
            {
                break;
            }
        }
        string? oldType = null;
        try
        {
            oldType = before?.Details(missing)?.Type;
        }
        catch (InvalidOperationException)
        {
            oldType = null;
        }
        var ranked = hits
            .Select(h => (h.Name, Distance: Levenshtein(h.Name, missing)))
            .OrderBy(x => x.Distance).ThenBy(x => x.Name.Length).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(40)
            .ToList();
        if (oldType is not null)
        {
            foreach ((string name, _) in ranked)
            {
                string? type = null;
                try
                {
                    type = now.Details(name)?.Type;
                }
                catch (InvalidOperationException)
                {
                }
                if (type == oldType)
                {
                    return ([name, .. ranked.Where(r => r.Name != name).Take(2).Select(r => r.Name)], true);
                }
            }
        }
        int limit = Math.Max(3, missing.Length / 3);
        return (ranked.Where(r => r.Distance <= limit).Take(3).Select(r => r.Name).ToList(), false);
    }

    /// <summary>The edit distance between two names.</summary>
    public static int Levenshtein(string a, string b)
    {
        var row = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            row[j] = j;
        }
        for (int i = 1; i <= a.Length; i++)
        {
            int diag = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int up = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diag + (a[i - 1] == b[j - 1] ? 0 : 1));
                diag = up;
            }
        }
        return row[b.Length];
    }

    /// <summary>
    /// Move the project to what the trial used: its <c>lean-toolchain</c> and <c>lake-manifest.json</c>, after backing
    /// up the project's own to <see cref="BackupFolder"/> (the dependency update's Undo puts them back). With
    /// <paramref name="renameDeprecated"/>, uses of deprecated names are renamed on disk as Lean says to. Returns the
    /// files renamed in. The project still has to be built (and Mathlib's cache fetched) afterwards.
    /// </summary>
    public static async Task<IReadOnlyList<string>> AdoptAsync(LeanProject project, UpgradeReport report, bool renameDeprecated, CancellationToken ct = default)
    {
        if (!report.Built || report.Problem is not null)
        {
            throw new InvalidOperationException("The trial did not build, so there is nothing to adopt.");
        }
        string backup = BackupFolder(project);
        Directory.CreateDirectory(backup);
        foreach (string f in new[] { project.ManifestPath, project.ToolchainPath }.Where(File.Exists))
        {
            File.Copy(f, Path.Combine(backup, Path.GetFileName(f)), overwrite: true);
        }
        var trial = new LeanProject(report.TrialRoot);
        project.SetToolchain(report.ToToolchain);
        if (File.Exists(trial.ManifestPath))
        {
            File.Copy(trial.ManifestPath, project.ManifestPath, overwrite: true);
        }
        var renamed = new List<string>();
        if (renameDeprecated)
        {
            foreach (IGrouping<string, DeprecatedUse> inFile in report.Deprecated.GroupBy(u => u.File))
            {
                if (!File.Exists(inFile.Key))
                {
                    continue;
                }
                string text = await File.ReadAllTextAsync(inFile.Key, ct).ConfigureAwait(false);
                string after = DependencyBump.Rename(text, inFile);
                if (after != text)
                {
                    await File.WriteAllTextAsync(inFile.Key, after, ct).ConfigureAwait(false);
                    renamed.Add(inFile.Key);
                }
            }
        }
        return renamed;
    }

    /// <summary>The report as Markdown: what was tried, whether it builds, and what to change, file by file.</summary>
    public static string ToMarkdown(UpgradeReport r, string projectRoot, int limit = 40)
    {
        var sb = new StringBuilder();
        string Where(string file, int line) => Path.GetRelativePath(projectRoot, file).Replace('\\', '/') + ":" + (line + 1).ToString(CultureInfo.InvariantCulture);
        sb.Append(CultureInfo.InvariantCulture, $"### Trying {r.ToToolchain}\n\n");
        if (r.Problem is not null)
        {
            sb.Append(r.Problem).Append('\n');
            return sb.ToString();
        }
        sb.Append(CultureInfo.InvariantCulture, $"From {r.FromToolchain ?? "no pinned toolchain"}");
        if (r.Mathlib is { } mathlib && mathlib.Before != mathlib.After)
        {
            sb.Append(CultureInfo.InvariantCulture, $", Mathlib {Short(mathlib.Before)} → {Short(mathlib.After)}");
        }
        sb.Append(". The project itself was not changed; the trial is in `").Append(Path.GetRelativePath(projectRoot, r.TrialRoot).Replace('\\', '/')).Append("`.\n\n");
        if (r.Clean)
        {
            sb.Append("✅ The project builds on it with no errors.");
            sb.Append(r.Deprecated.Count > 0 ? $" {Plural(r.Deprecated.Count, "use")} of deprecated names can be renamed as Lean says.\n" : "\n");
        }
        else
        {
            int files = r.Errors.Select(e => e.Path).Distinct().Count();
            sb.Append(CultureInfo.InvariantCulture, $"❌ {Plural(r.Errors.Count, "error")} in {Plural(files, "file")}.\n");
        }
        if (r.Suggestions.Count > 0)
        {
            sb.Append("\n**Names it no longer has, and what they likely became**\n\n| Where | Missing | Try |\n|---|---|---|\n");
            foreach (NameSuggestion s in r.Suggestions.Take(limit))
            {
                string tries = s.Candidates.Count == 0 ? "nothing close" : string.Join(", ", s.Candidates.Select(c => $"`{c}`"))
                    + (s.SameStatement ? " (same statement)" : "");
                sb.Append(CultureInfo.InvariantCulture, $"| {Where(s.File, s.Line)} | `{s.Missing}` | {tries} |\n");
            }
        }
        if (r.Deprecated.Count > 0)
        {
            sb.Append("\n**Deprecated, with Lean's replacement** (renamed for you when you adopt it)\n\n");
            foreach (var g in r.Deprecated.GroupBy(d => (d.Old, d.New)).Take(limit))
            {
                sb.Append(CultureInfo.InvariantCulture, $"- `{g.Key.Old}` → `{g.Key.New}` ({Plural(g.Count(), "use")})\n");
            }
        }
        var other = r.Errors.Where(e => !r.Suggestions.Any(s => s.File == e.Path && s.Line == e.Line && s.Column == e.Column)).ToList();
        if (other.Count > 0)
        {
            sb.Append("\n**Other errors**\n\n");
            foreach (BuildMessage e in other.Take(limit))
            {
                sb.Append(CultureInfo.InvariantCulture, $"- {Where(e.Path, e.Line)}: {e.Message.Split('\n')[0]}\n");
            }
            if (other.Count > limit)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- …and {other.Count - limit} more\n");
            }
        }
        return sb.ToString();
    }

    private static string Short(string? rev) => rev is null ? "?" : rev.Length > 8 ? rev[..8] : rev;

    private static string Plural(int n, string word) => n.ToString("N0", CultureInfo.InvariantCulture) + " " + word + (n == 1 ? "" : "s");

    /// <summary>What <c>leanstudio --try-upgrade --help</c> prints.</summary>
    public const string Usage = """
        leanstudio --try-upgrade [options]

        Tries a newer Lean, or newer dependencies, on a copy of the project under .lake/leanstudio/upgrade, and
        reports what broke, file by file: the errors, the names the new version deprecates (with Lean's
        replacement), and for each name it no longer has, the names it does have that it likely became. The project
        is not changed unless --adopt is given.

          --project DIR    the project (default: the current folder)
          --to TOOLCHAIN   the Lean to try (leanprover/lean4:v4.35.0, v4.35.0 or 4.35.0). Default: for a project
                           with dependencies, what updating them brings (Mathlib's Lean); otherwise the newest
                           stable Lean, looked up on GitHub
          --no-update      do not update the dependencies (lake update), only the toolchain
          --adopt          when the trial builds, move the project to it (lean-toolchain, lake-manifest.json; the old
                           ones are kept in .lake/leanstudio-update-backup) and rename deprecated names
          --markdown FILE  also write the report as Markdown ($GITHUB_STEP_SUMMARY is used when set)

        Exit code: 0 builds cleanly on the new version, 1 something broke, 2 could not run.

        """;

    /// <summary>
    /// Run the trial from the command line (<c>leanstudio --try-upgrade …</c>): the report as Markdown to
    /// <paramref name="output"/>, progress to <paramref name="error"/>; 0 clean, 1 broke, 2 could not run.
    /// </summary>
    public static async Task<int> RunCommandLineAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        List<string> list = args.ToList();
        string? Value(string name)
        {
            int i = list.IndexOf(name);
            return i >= 0 && i + 1 < list.Count ? list[i + 1] : null;
        }
        if (list.Contains("--help") || list.Contains("-h"))
        {
            await output.WriteAsync(Usage).ConfigureAwait(false);
            return 0;
        }
        string dir = Path.GetFullPath(Value("--project") ?? Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(dir) || !File.Exists(Path.Combine(dir, LeanProject.ToolchainFile)))
        {
            await error.WriteLineAsync($"{dir} is not a Lean project (it has no lean-toolchain).").ConfigureAwait(false);
            return 2;
        }
        var project = new LeanProject(dir);
        using var http = new HttpClient();
        UpgradeReport report = await RunAsync(project, new UpgradeOptions(Value("--to"), !list.Contains("--no-update")),
            c => LeanReleases.LatestStableTagAsync(http, c), new SyncProgress(error.WriteLine), ct).ConfigureAwait(false);
        string markdown = ToMarkdown(report, dir);
        await output.WriteAsync(markdown).ConfigureAwait(false);
        foreach (string? file in new[] { Value("--markdown"), Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") }.Distinct())
        {
            if (!string.IsNullOrEmpty(file))
            {
                await File.AppendAllTextAsync(file, markdown + "\n", ct).ConfigureAwait(false);
            }
        }
        if (report.Problem is not null)
        {
            return 2;
        }
        if (list.Contains("--adopt"))
        {
            IReadOnlyList<string> renamed = await AdoptAsync(project, report, renameDeprecated: true, ct).ConfigureAwait(false);
            await output.WriteLineAsync($"\nAdopted {report.ToToolchain}" + (renamed.Count > 0 ? $", and renamed deprecated names in {Plural(renamed.Count, "file")}." : ".")
                + " Build the project to finish.").ConfigureAwait(false);
        }
        return report.Clean ? 0 : 1;
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
