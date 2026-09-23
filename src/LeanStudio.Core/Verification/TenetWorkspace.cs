using System.Diagnostics;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;
using Tenet.Kernel;
using Tenet.Olean;
using TenetName = Tenet.Kernel.Name;

namespace LeanStudio.Core.Verification;

/// <summary>Why a declaration does or does not stand on its own.</summary>
public enum VerificationStatus
{
    /// <summary>Tenet checked it and it needs nothing beyond propext, Classical.choice and Quot.sound.</summary>
    Verified,
    /// <summary>Tenet checked it, but it rests on sorry or on an axiom the project introduces.</summary>
    RestsOnAssumption,
    /// <summary>Tenet's kernel rejected it.</summary>
    Rejected,
}

public sealed record DeclarationVerdict(
    string Name,
    string Module,
    VerificationStatus Status,
    IReadOnlyList<string> Assumptions,
    string? Message,
    int? Line);

public sealed record VerificationReport(
    string LeanVersion,
    int ModulesChecked,
    int ModulesLoaded,
    TimeSpan Elapsed,
    IReadOnlyList<DeclarationVerdict> Declarations)
{
    public int Verified => Declarations.Count(d => d.Status == VerificationStatus.Verified);
    public int Conditional => Declarations.Count(d => d.Status == VerificationStatus.RestsOnAssumption);
    public int Rejected => Declarations.Count(d => d.Status == VerificationStatus.Rejected);
}

public sealed record VerificationProgress(string Module, int ModuleIndex, int ModuleCount, int Done, int Total);

public sealed record DeclarationSummary(string Name, string Module);

/// <summary>One declaration on the way from a theorem down to what it rests on.</summary>
public sealed record TrailLink(string Name, string Module, string? SourceFile, int? Line)
{
    public bool IsSorry => Name == "sorryAx";
    public string Display => IsSorry ? "sorry" : Name;
}

/// <summary>
/// Why a declaration is not fully proved: the chain of declarations from it to one assumption (sorry, or an axiom
/// the project adds), shortest first. The last declaration before the assumption is where to go and fix it.
/// </summary>
public sealed record AssumptionTrail(string Assumption, IReadOnlyList<TrailLink> Path)
{
    public bool IsSorry => Assumption == "sorryAx";

    /// <summary>The declaration that uses the assumption itself.</summary>
    public TrailLink? Culprit => Path.Count >= 2 ? Path[^2] : null;

    public string Summary => string.Join("  →  ", Path.Select(l => l.Display));
}

public sealed record DeclarationDetails(
    string Name,
    string Kind,
    string Module,
    IReadOnlyList<string> LevelParams,
    string Type,
    string? Value,
    string? DocString,
    string? Deprecation,
    int? Line,
    int? Column,
    string? SourceFile,
    IReadOnlyList<string> Uses,
    bool IsUnsafe);

/// <summary>
/// A project's compiled modules, and everything they import, opened with Tenet. Two jobs use it: re-checking what
/// Lean built with an independent kernel, and the declaration navigator, which reads statements, docstrings,
/// dependencies and axioms straight out of the .olean files.
///
/// Opening maps the files; nothing is decoded until asked for, so opening a Mathlib project is quick and its
/// memory is the operating system's page cache rather than this process's heap.
/// </summary>
public sealed class TenetWorkspace : IDisposable
{
    private static readonly HashSet<TenetName> StandardAxioms =
        [TenetName.Of("propext"), TenetName.Of("Classical", "choice"), TenetName.Of("Quot", "sound")];

    private readonly OleanChecker _checker;
    private readonly LeanSearchPath _search;
    private readonly object _lock = new();
    private List<DeclarationSummary>? _all;

    private TenetWorkspace(LeanProject project, OleanChecker checker, LeanSearchPath search, List<TenetName> own, string leanVersion)
    {
        Project = project;
        _checker = checker;
        _search = search;
        OwnModules = own;
        LeanVersion = leanVersion;
    }

    public LeanProject Project { get; }

    /// <summary>The project's own modules (those built under its .lake/build), not its dependencies.</summary>
    public IReadOnlyList<TenetName> OwnModules { get; }

    public string LeanVersion { get; }

    public int ModuleCount
    {
        get
        {
            lock (_lock)
            {
                return _checker.Modules.Count;
            }
        }
    }

    /// <summary>
    /// Open a project's built modules and their import closure. A project that has not been built yet opens the
    /// toolchain's own library instead (Init, Std, Lean), so the navigator still has something to show.
    /// </summary>
    public static TenetWorkspace Open(LeanProject project)
    {
        var search = new LeanSearchPath();
        search.AddFromEnvironment();
        var own = new List<(TenetName Module, string Path)>();
        if (Directory.Exists(project.BuildLibDirectory))
        {
            foreach (string f in Directory.EnumerateFiles(project.BuildLibDirectory, "*.olean", SearchOption.AllDirectories))
            {
                // Module-system files split into Foo.olean, Foo.olean.private, Foo.olean.server; the first is the module.
                if (f.EndsWith(".olean", StringComparison.Ordinal))
                {
                    own.Add((TenetName.Parse(Path.GetRelativePath(project.BuildLibDirectory, f)[..^".olean".Length]
                        .Replace(Path.DirectorySeparatorChar, '.').Replace('/', '.')), f));
                }
            }
        }

        string? toolchainLib = project.Toolchain is string tc ? Path.Combine(Elan.ToolchainDirectory(tc), "lib", "lean") : null;
        var targets = new List<(TenetName, string)>(own);
        if (own.Count > 0)
        {
            search.AddAroundOleanFile(own[0].Path);
        }
        if (toolchainLib is not null && Directory.Exists(toolchainLib))
        {
            search.Add(toolchainLib);
        }
        if (own.Count == 0)
        {
            foreach (string root in new[] { "Init", "Std", "Lean" })
            {
                string? f = search.Find(TenetName.Of(root));
                if (f is not null)
                {
                    targets.Add((TenetName.Of(root), f));
                }
            }
        }
        var checker = new OleanChecker(search);
        try
        {
            checker.Load(targets);
            string version = checker.Modules.Values.FirstOrDefault()?.LeanVersion ?? "";
            if (toolchainLib is null && version.Length > 0)
            {
                search.AddToolchainFor(version);
                checker.Load(targets);
            }
            return new TenetWorkspace(project, checker, search, own.Select(o => o.Module).ToList(), version);
        }
        catch
        {
            checker.Dispose();
            throw;
        }
    }

    // ---- navigator ----

    /// <summary>Every module that is open, sorted by name.</summary>
    public IReadOnlyList<string> Modules()
    {
        lock (_lock)
        {
            return _checker.Modules.Keys.Select(k => k.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToList();
        }
    }

    public IReadOnlyList<string> DeclarationsIn(string module)
    {
        lock (_lock)
        {
            return _checker.Modules.TryGetValue(TenetName.Parse(module), out OleanModule? m)
                ? m.ConstantNames.Select(n => n.ToString()).Where(IsUserFacing).OrderBy(s => s, StringComparer.Ordinal).ToList()
                : [];
        }
    }

    /// <summary>
    /// Declarations whose name contains every space-separated part of the query, case-insensitively, shortest
    /// names first (the likeliest intended match). Names Lean generates for itself are left out.
    /// </summary>
    public IReadOnlyList<DeclarationSummary> Search(string query, int limit = 400, CancellationToken ct = default)
    {
        List<DeclarationSummary> all = AllDeclarations(ct);
        string[] parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return [];
        }
        var hits = new List<DeclarationSummary>();
        foreach (DeclarationSummary d in all)
        {
            ct.ThrowIfCancellationRequested();
            if (parts.All(p => d.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                hits.Add(d);
            }
        }
        return hits
            .OrderBy(d => d.Name.EndsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(d => d.Name.Length)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    private List<DeclarationSummary> AllDeclarations(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_all is not null)
            {
                return _all;
            }
            var list = new List<DeclarationSummary>();
            foreach ((TenetName module, OleanModule m) in _checker.Modules)
            {
                ct.ThrowIfCancellationRequested();
                string mod = module.ToString();
                foreach (TenetName n in m.ConstantNames)
                {
                    string s = n.ToString();
                    if (IsUserFacing(s))
                    {
                        list.Add(new DeclarationSummary(s, mod));
                    }
                }
            }
            _all = list;
            return list;
        }
    }

    /// <summary>Hide the names Lean generates (match_1, proof_2, _private..., instance helpers), which nobody searches for.</summary>
    public static bool IsUserFacing(string name) =>
        !name.StartsWith("_private", StringComparison.Ordinal)
        && !name.Contains("._", StringComparison.Ordinal)
        && !name.Contains(".match_", StringComparison.Ordinal)
        && !name.Contains(".proof_", StringComparison.Ordinal)
        && !name.Contains(".eq_", StringComparison.Ordinal)
        && !name.EndsWith(".sizeOf_spec", StringComparison.Ordinal)
        && !name.Contains("._cstage", StringComparison.Ordinal);

    public DeclarationDetails? Details(string name)
    {
        TenetName n = TenetName.Parse(name);
        lock (_lock)
        {
            ConstantInfo? c = _checker.Resolve(n);
            if (c is null)
            {
                return null;
            }
            OleanModule? owner = _checker.Modules.Values.FirstOrDefault(m => m.Contains(n));
            string module = owner is null ? "" : _checker.Modules.First(kv => ReferenceEquals(kv.Value, owner)).Key.ToString();
            SourceRange? range = owner?.SourceRangeOf(n);
            Deprecation? dep = owner?.DeprecationOf(n);
            string? value = c is TheoremInfo ? null : c.Value is Expr v ? Print(v) : null;
            return new DeclarationDetails(
                name,
                c.KindName,
                module,
                c.LevelParams.Select(l => l.ToString()).ToList(),
                Print(c.Type),
                value,
                owner?.DocStringOf(n),
                dep is null ? null : dep.NewName is TenetName nn ? $"deprecated: use {nn}" + (dep.Since is null ? "" : $" (since {dep.Since})") : "deprecated" + (dep.Text is null ? "" : ": " + dep.Text),
                range?.Line,
                range?.Column,
                SourceFileOf(module),
                Replay.UsedConstants(c).Select(u => u.ToString()).Where(IsUserFacing).OrderBy(s => s, StringComparer.Ordinal).ToList(),
                c.IsUnsafe);
        }
    }

    private static string Print(Expr e)
    {
        string s = ExprPrinter.Print(e);
        return s.Length > 4000 ? s[..4000] + " …" : s;
    }

    /// <summary>Every axiom a declaration depends on, transitively (Lean's <c>#print axioms</c>, computed by Tenet).</summary>
    public IReadOnlyList<string> AxiomsOf(string name)
    {
        lock (_lock)
        {
            (SortedSet<TenetName> axioms, _) = Replay.AxiomsOf(_checker.Resolve, TenetName.Parse(name));
            return axioms.Select(a => a.ToString()).ToList();
        }
    }

    /// <summary>
    /// For each assumption (sorry, or an axiom beyond Lean's standard three) a declaration rests on, the shortest
    /// chain of declarations that leads to it, found breadth-first through what each one uses. Names Lean made
    /// (<c>foo._proof_1</c>, <c>foo.match_1</c>, private names) are shown as the declaration they belong to.
    /// </summary>
    public IReadOnlyList<AssumptionTrail> WhyNotProved(string name, CancellationToken ct = default)
    {
        TenetName start = TenetName.Parse(name);
        lock (_lock)
        {
            if (_checker.Resolve(start) is null)
            {
                return [];
            }
            (SortedSet<TenetName> axioms, _) = Replay.AxiomsOf(_checker.Resolve, start);
            var wanted = new HashSet<TenetName>(axioms.Where(a => !StandardAxioms.Contains(a)));
            if (wanted.Count == 0)
            {
                return [];
            }
            var parent = new Dictionary<TenetName, TenetName?> { [start] = null };
            var queue = new Queue<TenetName>();
            queue.Enqueue(start);
            var found = new List<TenetName>();
            while (queue.Count > 0 && found.Count < wanted.Count)
            {
                ct.ThrowIfCancellationRequested();
                TenetName n = queue.Dequeue();
                if (_checker.Resolve(n) is not ConstantInfo c)
                {
                    continue;
                }
                if (wanted.Contains(n) && c is AxiomInfo)
                {
                    found.Add(n);
                    continue;
                }
                foreach (TenetName u in Replay.UsedConstants(c))
                {
                    if (parent.TryAdd(u, n))
                    {
                        queue.Enqueue(u);
                    }
                }
            }
            var trails = new List<AssumptionTrail>();
            foreach (TenetName ax in found)
            {
                var chain = new List<TenetName>();
                for (TenetName? k = ax; k is not null; k = parent[k])
                {
                    chain.Add(k);
                }
                chain.Reverse();
                var links = new List<TrailLink>();
                foreach (TenetName k in chain)
                {
                    TrailLink link = LinkFor(k);
                    if (links.Count == 0 || links[^1].Name != link.Name)
                    {
                        links.Add(link);
                    }
                }
                trails.Add(new AssumptionTrail(ax.ToString(), links));
            }
            return trails.OrderBy(t => t.IsSorry ? 0 : 1).ThenBy(t => t.Path.Count).ToList();
        }
    }

    /// <summary>The user-facing declaration a name belongs to, and where it is written.</summary>
    private TrailLink LinkFor(TenetName n)
    {
        string s = UserFacingOwner(n.ToString());
        TenetName owner = TenetName.Parse(s);
        TenetName lookup = _checker.Resolve(owner) is not null ? owner : n;
        OleanModule? file = _checker.Modules.Values.FirstOrDefault(m => m.Contains(lookup));
        string module = file is null ? "" : _checker.Modules.First(kv => ReferenceEquals(kv.Value, file)).Key.ToString();
        string? source = SourceFileOf(module);
        int? line = file?.SourceRangeOf(lookup)?.Line is int l
            ? source is not null ? Proofs.ProofSteps.DeclarationLine(File.ReadAllLines(source), l) : l
            : null;
        return new TrailLink(s, module, source, line);
    }

    /// <summary><c>foo._proof_1</c> → <c>foo</c>; <c>_private.Mod.0.foo.match_1</c> → <c>foo</c>.</summary>
    public static string UserFacingOwner(string name)
    {
        string s = name;
        if (s.StartsWith("_private.", StringComparison.Ordinal))
        {
            int zero = s.IndexOf(".0.", StringComparison.Ordinal);
            if (zero > 0)
            {
                s = s[(zero + 3)..];
            }
        }
        string[] parts = s.Split('.');
        int keep = parts.Length;
        for (int i = 1; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p.StartsWith('_') || p.StartsWith("match_", StringComparison.Ordinal) || p.StartsWith("proof_", StringComparison.Ordinal)
                || p.StartsWith("eq_", StringComparison.Ordinal) || p == "sizeOf_spec")
            {
                keep = i;
                break;
            }
        }
        return string.Join('.', parts.Take(keep));
    }

    /// <summary>Declarations that mention <paramref name="name"/> directly, among the project's own modules (or all, when asked).</summary>
    public IReadOnlyList<string> UsedBy(string name, bool everywhere = false, CancellationToken ct = default)
    {
        TenetName target = TenetName.Parse(name);
        var hits = new List<string>();
        lock (_lock)
        {
            IEnumerable<OleanModule> scope = everywhere || OwnModules.Count == 0
                ? _checker.Modules.Values
                : OwnModules.Where(_checker.Modules.ContainsKey).Select(m => _checker.Modules[m]);
            foreach (OleanModule m in scope)
            {
                foreach (TenetName n in m.ConstantNames)
                {
                    ct.ThrowIfCancellationRequested();
                    ConstantInfo? c = m.FindConstant(n);
                    if (c is not null && Replay.UsedConstants(c).Contains(target))
                    {
                        string s = n.ToString();
                        if (IsUserFacing(s))
                        {
                            hits.Add(s);
                        }
                    }
                }
            }
        }
        hits.Sort(StringComparer.Ordinal);
        return hits;
    }

    /// <summary>The .lean file a module was compiled from, if it can be found in the project, its packages, or the toolchain.</summary>
    public string? SourceFileOf(string module)
    {
        if (module.Length == 0)
        {
            return null;
        }
        string rel = Path.Combine(module.Split('.')) + ".lean";
        var roots = new List<string> { Project.Root };
        if (Directory.Exists(Project.PackagesDirectory))
        {
            roots.AddRange(Directory.GetDirectories(Project.PackagesDirectory));
        }
        if (Project.Toolchain is string tc)
        {
            roots.Add(Path.Combine(Elan.ToolchainDirectory(tc), "src", "lean"));
            roots.Add(Path.Combine(Elan.ToolchainDirectory(tc), "src", "lean", "lake"));
        }
        foreach (string r in roots)
        {
            string f = Path.Combine(r, rel);
            if (File.Exists(f))
            {
                return f;
            }
        }
        return null;
    }

    // ---- verification ----

    /// <summary>
    /// Re-check the project's own modules with Tenet's kernel, then sort every declaration into verified, resting
    /// on an assumption (sorry or a project axiom), or rejected. Runs on a thread with a large stack, as Tenet's
    /// kernel recursion needs one on deep terms.
    /// </summary>
    public Task<VerificationReport> VerifyAsync(IReadOnlyList<string>? modules = null, IProgress<VerificationProgress>? progress = null, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<VerificationReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(Verify(modules, progress, ct));
            }
            catch (OperationCanceledException e)
            {
                tcs.SetCanceled(e.CancellationToken);
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        }, 512 * 1024 * 1024)
        {
            IsBackground = true,
            Name = "Tenet verification",
        };
        thread.Start();
        return tcs.Task;
    }

    private VerificationReport Verify(IReadOnlyList<string>? modules, IProgress<VerificationProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        List<TenetName> targets = modules is null
            ? OwnModules.ToList()
            : modules.Select(TenetName.Parse).Where(m => OwnModules.Contains(m)).ToList();
        if (targets.Count == 0)
        {
            return new VerificationReport(LeanVersion, 0, ModuleCount, sw.Elapsed, []);
        }
        OleanCheckResult result;
        lock (_lock)
        {
            result = _checker.Check(targets, new OleanCheckOptions
            {
                ContinueOnError = true,
                EvictBetweenModules = false,
                Progress = p =>
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new VerificationProgress(p.Module.ToString(), p.ModuleIndex, p.ModuleCount, p.Done, p.Total));
                },
            });
        }
        ct.ThrowIfCancellationRequested();

        var failures = result.Failures.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());
        var verdicts = new List<DeclarationVerdict>();
        lock (_lock)
        {
            var own = new List<(TenetName Module, ConstantInfo Info, OleanModule File)>();
            foreach (TenetName m in targets)
            {
                OleanModule om = _checker.Modules[m];
                foreach (TenetName cn in om.ConstantNames)
                {
                    if (_checker.Resolve(cn) is ConstantInfo ci)
                    {
                        own.Add((m, ci, om));
                    }
                }
            }
            List<ConstantInfo> scope = own.Select(o => o.Info).ToList();

            // Axioms beyond the standard three that anything in scope uses directly, then who rests on each.
            var assumptions = new HashSet<TenetName>();
            foreach (ConstantInfo ci in scope)
            {
                if (ci is AxiomInfo && !StandardAxioms.Contains(ci.Name))
                {
                    assumptions.Add(ci.Name);
                }
                foreach (TenetName u in Replay.UsedConstants(ci))
                {
                    if (!StandardAxioms.Contains(u) && _checker.Resolve(u) is AxiomInfo)
                    {
                        assumptions.Add(u);
                    }
                }
            }
            var restsOn = new Dictionary<TenetName, List<string>>();
            foreach (TenetName ax in assumptions)
            {
                ct.ThrowIfCancellationRequested();
                HashSet<TenetName> hit = Replay.DependentsOf(ax, scope);
                hit.Add(ax);
                foreach (TenetName h in hit)
                {
                    if (!restsOn.TryGetValue(h, out List<string>? l))
                    {
                        restsOn[h] = l = [];
                    }
                    l.Add(ax.ToString());
                }
            }

            var sources = new Dictionary<TenetName, string[]>();
            string[] SourceOf(TenetName m)
            {
                if (!sources.TryGetValue(m, out string[]? lines))
                {
                    string? f = SourceFileOf(m.ToString());
                    sources[m] = lines = f is null ? [] : File.ReadAllLines(f);
                }
                return lines;
            }
            foreach ((TenetName module, ConstantInfo ci, OleanModule file) in own)
            {
                string name = ci.Name.ToString();
                if (!IsUserFacing(name))
                {
                    continue;
                }
                int? line = file.SourceRangeOf(ci.Name)?.Line is int l ? Proofs.ProofSteps.DeclarationLine(SourceOf(module), l) : null;
                if (failures.TryGetValue(ci.Name, out OleanCheckFailure? f))
                {
                    verdicts.Add(new DeclarationVerdict(name, module.ToString(), VerificationStatus.Rejected, [], f.Message, line));
                }
                else if (restsOn.TryGetValue(ci.Name, out List<string>? axs))
                {
                    axs.Sort(StringComparer.Ordinal);
                    verdicts.Add(new DeclarationVerdict(name, module.ToString(), VerificationStatus.RestsOnAssumption, axs, null, line));
                }
                else
                {
                    verdicts.Add(new DeclarationVerdict(name, module.ToString(), VerificationStatus.Verified, [], null, line));
                }
            }
            // A failure in a generated helper is still a failure of the declaration it belongs to; surface it.
            foreach (OleanCheckFailure f in result.Failures)
            {
                string n = f.Name.ToString();
                if (!IsUserFacing(n))
                {
                    verdicts.Add(new DeclarationVerdict(n, f.Module.ToString(), VerificationStatus.Rejected, [], f.Message, null));
                }
            }
        }
        return new VerificationReport(result.LeanVersion, targets.Count, result.ModulesLoaded, sw.Elapsed, verdicts);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _checker.Dispose();
        }
    }
}
