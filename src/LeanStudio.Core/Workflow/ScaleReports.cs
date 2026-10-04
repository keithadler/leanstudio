using System.Globalization;
using System.Text;
using LeanStudio.Core.Editing;
using LeanStudio.Core.Git;
using LeanStudio.Core.Projects;

namespace LeanStudio.Core.Workflow;

/// <summary>
/// The features for a project of a great theorem's size, read out as text: what to prove next, what blocks the most, how to
/// share out the work, how long a <c>sorry</c> has stood, how long until they run out, where a file could split, the longest
/// proofs, the build's critical path, layer rules and locked statements. The app's Output and the MCP tools say the same thing.
/// </summary>
public static class ScaleReports
{
    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Where(string root, Decl d) => $"{Rel(root, d.Path)}:{d.Line + 1}";

    /// <summary>The declarations of every Lean file under <paramref name="root"/>, as a graph. Files that cannot be read are left out.</summary>
    public static DeclGraph ReadGraph(string root, CancellationToken ct = default)
    {
        var files = new List<(string, string)>();
        foreach (string f in ProjectSearch.Files(root, leanOnly: true).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                files.Add((f, File.ReadAllText(f)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a file that cannot be read has no declarations to find
            }
        }
        return DeclGraph.Build(files);
    }

    /// <summary>The theorem and lemma statements of every Lean file under <paramref name="root"/>.</summary>
    public static IReadOnlyList<TheoremStatement> ReadGraphStatements(string root, CancellationToken ct = default)
    {
        var all = new List<TheoremStatement>();
        foreach (string f in ProjectSearch.Files(root, leanOnly: true).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                all.AddRange(DuplicateStatements.Statements(f, File.ReadAllText(f)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a file that cannot be read has no statements to check
            }
        }
        return all;
    }

    /// <summary>The sorries that can be worked on now, those that unblock the most first.</summary>
    public static string NextUp(DeclGraph g, string root, int max = 25)
    {
        IReadOnlyList<(Decl Decl, int Blocking)> next = g.NextUp();
        int total = g.Declarations.Count(d => d.HasSorry);
        if (total == 0)
        {
            return "No sorries: nothing is left to prove.";
        }
        var sb = new StringBuilder($"{next.Count} of the {total} sorries can be proved now: every theorem they use is already fully proved. The ones that unblock the most come first.\n");
        foreach ((Decl d, int blocking) in next.Take(max))
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {d.Name}  ({Where(root, d)}): unblocks {blocking}\n");
        }
        if (next.Count > max)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  … and {next.Count - max} more\n");
        }
        if (next.Count < total)
        {
            sb.Append(CultureInfo.InvariantCulture, $"The other {total - next.Count} use something that still has a sorry in it, so they wait.");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The sorries that hold up the most other declarations, started or not.</summary>
    public static string MostBlocking(DeclGraph g, string root, int count = 15)
    {
        IReadOnlyList<(Decl Decl, int Blocking, bool Ready)> most = g.MostBlocking(count);
        if (most.Count == 0)
        {
            return "No sorries.";
        }
        var sb = new StringBuilder("The sorries that hold up the most (proving one moves every theorem that rests on it, however far up, a step closer):\n");
        foreach ((Decl d, int blocking, bool ready) in most)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {blocking,5} held up  {d.Name}  ({Where(root, d)}){(ready ? "  ← can start now" : "  (waits on another sorry)")}\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The ready work shared among <paramref name="people"/>, so nobody waits on anybody.</summary>
    public static string Packages(DeclGraph g, string root, int people)
    {
        IReadOnlyList<WorkPackage> packages = g.WorkPackages(people);
        if (packages.Count == 0)
        {
            return "Nothing can be started: there are no sorries, or every one waits on another.";
        }
        var sb = new StringBuilder($"The work that can start now, in {packages.Count} share{(packages.Count == 1 ? "" : "s")} for {people} {(people == 1 ? "person" : "people")}. No share waits on another, and each stays in few files:\n");
        for (int i = 0; i < packages.Count; i++)
        {
            WorkPackage p = packages[i];
            sb.Append(CultureInfo.InvariantCulture, $"\nPerson {i + 1}: {p.Declarations.Count} sorr{(p.Declarations.Count == 1 ? "y" : "ies")}, weight {p.Weight}\n");
            foreach (IGrouping<string, Decl> file in p.Declarations.GroupBy(d => d.Path, StringComparer.Ordinal))
            {
                sb.Append("  ").Append(Rel(root, file.Key)).Append(": ").Append(string.Join(", ", file.Select(d => d.Name))).Append('\n');
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The longest proofs: the ones most likely to want splitting into lemmas.</summary>
    public static string LongProofs(DeclGraph g, string root, int count = 15, int minLines = 0)
    {
        IReadOnlyList<Decl> longest = g.Longest(count, minLines);
        if (longest.Count == 0)
        {
            return "No declarations that long.";
        }
        var sb = new StringBuilder("The longest declarations (lines of code, comments and blanks not counted):\n");
        foreach (Decl d in longest)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {d.ProofLines,5}  {d.Name}  ({Where(root, d)})\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Where a file could be split: groups of its declarations that do not use each other.</summary>
    public static string Split(string path, string text, int minLines = 100)
    {
        DeclGraph g = DeclGraph.Build([(path, text)]);
        IReadOnlyList<IReadOnlyList<Decl>> groups = g.IndependentGroups(minLines);
        string name = Path.GetFileName(path);
        if (g.Declarations.Count == 0)
        {
            return $"{name}: no declarations.";
        }
        if (groups.Count < 2)
        {
            return $"{name}: its declarations all rest on each other (or the parts are under {minLines} lines): there is no clean place to split it.";
        }
        var sb = new StringBuilder($"{name} holds {groups.Count} groups of declarations that do not use each other, so each could be a file of its own:\n");
        int i = 1;
        foreach (IReadOnlyList<Decl> group in groups)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  Part {i++}: {group.Count} declaration{(group.Count == 1 ? "" : "s")}, {group.Sum(d => d.ProofLines)} lines: {string.Join(", ", group.Take(6).Select(d => d.Name))}{(group.Count > 6 ? ", …" : "")}\n");
        }
        sb.Append("Check first what each part needs from the top of the file: its `variable`s, `open`s and `namespace`s go with it.");
        return sb.ToString();
    }

    /// <summary>How long each sorry has stood, the oldest first.</summary>
    public static string Age(IReadOnlyList<SorryAgeEntry> entries, string root, int max = 20)
    {
        if (entries.Count == 0)
        {
            return "No sorries to age (or none in files Git tracks).";
        }
        var sb = new StringBuilder($"The sorries that have stood longest ({entries.Count} in all, from git blame):\n");
        foreach (SorryAgeEntry e in entries.Take(max))
        {
            string age = e.Date is null ? "not committed yet" : e.DaysOld >= 730 ? $"{e.DaysOld / 365} years" : e.DaysOld >= 60 ? $"{e.DaysOld / 30} months" : $"{e.DaysOld} days";
            sb.Append(CultureInfo.InvariantCulture, $"  {age,-14} {e.Declaration ?? "?"}  ({Rel(root, e.Path)}:{e.Line + 1})  {e.Author}: {e.Summary}\n");
        }
        if (entries.Count > max)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  … and {entries.Count - max} more");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The import graph of <paramref name="project"/> as module → the project's own modules it imports, and each module's cost:
    /// its lines of code, which stand in for build time until a profile says otherwise.
    /// </summary>
    public static (Dictionary<string, IReadOnlyList<string>> Imports, Dictionary<string, double> Cost) ReadImports(LeanProject project, CancellationToken ct = default)
    {
        ImportGraph graph = ImportGraph.Build(project);
        var imports = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var cost = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string module, string file) in graph.Files)
        {
            ct.ThrowIfCancellationRequested();
            imports[module] = [.. graph.ImportsOf(module).Select(e => e.Imported).Where(graph.Files.ContainsKey).Distinct()];
            try
            {
                cost[module] = File.ReadLines(file).Count(l => l.Trim().Length > 0);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                cost[module] = 1;
            }
        }
        return (imports, cost);
    }

    /// <summary>The build's critical path, with each module's size in lines as its cost.</summary>
    public static string CriticalPathText(LeanProject project, CancellationToken ct = default)
    {
        (Dictionary<string, IReadOnlyList<string>> imports, Dictionary<string, double> cost) = ReadImports(project, ct);
        double Cost(string m) => cost.GetValueOrDefault(m, 1);
        return CriticalPath.ToText(CriticalPath.Find(imports, Cost), Cost, "lines");
    }

    /// <summary>The imports that go against the project's layers (<c>.leanstudio/layers.json</c>).</summary>
    public static string Layers(LeanProject project)
    {
        string file = LayerRules.FileFor(project.Root);
        if (!File.Exists(file))
        {
            return "No layers are written down. Create .leanstudio/layers.json:\n{ \"layers\": [[\"Proj.Basic\"], [\"Proj.Mid\"], [\"Proj.Main\"]] }\nlowest layer first, each a list of module prefixes. A module may then import from its own layer and the ones below.";
        }
        if (LayerRules.Parse(File.ReadAllText(file)) is not LayerRules rules)
        {
            return ".leanstudio/layers.json is not in the form { \"layers\": [[\"A\"], [\"B\"]] }.";
        }
        ImportGraph graph = ImportGraph.Build(project);
        IReadOnlyList<LayerViolation> violations = rules.Check(graph.Files.Keys.SelectMany(graph.ImportsOf));
        if (violations.Count == 0)
        {
            return $"Layers: no import reaches up, across {rules.Layers.Count} layers.";
        }
        var sb = new StringBuilder($"Layers: {violations.Count} import{(violations.Count == 1 ? "" : "s")} reach up from a lower layer into a higher one:\n");
        foreach (LayerViolation v in violations.Take(50))
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {Rel(project.Root, v.File)}:{v.Line + 1}  {v.Module} (layer {v.FromLayer + 1}) imports {v.Imported} (layer {v.ToLayer + 1})\n");
        }
        if (violations.Count > 50)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  … and {violations.Count - 50} more");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>What became of the locked statements of the project at <paramref name="root"/>, given its current theorem statements.</summary>
    public static string Locks(string root, IEnumerable<TheoremStatement> current)
    {
        IReadOnlyList<LockedStatement> locks = StatementLock.Read(root);
        if (locks.Count == 0)
        {
            return "No statements are locked. Put the cursor in the theorem that must never change by accident (the main theorem) and Lock This Statement.";
        }
        IReadOnlyList<LockResult> results = StatementLock.Check(locks, current);
        var sb = new StringBuilder();
        foreach (LockResult r in results.Where(r => r.Status != LockStatus.Same))
        {
            sb.Append(r.Status == LockStatus.Missing ? $"  GONE     {r.Name}: it was `{r.Was}`\n" : $"  CHANGED  {r.Name}\n    locked: {r.Was}\n    now:    {r.Now}\n");
        }
        int same = results.Count(r => r.Status == LockStatus.Same);
        sb.Append(CultureInfo.InvariantCulture, $"Locked statements: {same} of {results.Count} unchanged.");
        if (same < results.Count)
        {
            sb.Append(" Check what changed before building on it: a weaker statement still compiles.");
        }
        return sb.ToString();
    }
}
