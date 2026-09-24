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

/// <summary>What verification concluded about one declaration of the project.</summary>
/// <param name="Name">The declaration's full name. Generated helpers appear under their own names only when rejected.</param>
/// <param name="Module">The module that defines it.</param>
/// <param name="Status">Whether it is verified, rests on an assumption, or was rejected.</param>
/// <param name="Assumptions">
/// For <see cref="VerificationStatus.RestsOnAssumption"/>, the axioms beyond Lean's standard three it rests on
/// (<c>sorryAx</c> for sorry), sorted; otherwise empty.
/// </param>
/// <param name="Message">For <see cref="VerificationStatus.Rejected"/>, the kernel's error; otherwise <see langword="null"/>.</param>
/// <param name="Line">The 1-based line of its keyword in the source file, or <see langword="null"/> when unknown.</param>
public sealed record DeclarationVerdict(
    string Name,
    string Module,
    VerificationStatus Status,
    IReadOnlyList<string> Assumptions,
    string? Message,
    int? Line);

/// <summary>The outcome of <see cref="TenetWorkspace.VerifyAsync"/>.</summary>
/// <param name="LeanVersion">The Lean version the modules were built with.</param>
/// <param name="ModulesChecked">How many of the project's modules were re-checked.</param>
/// <param name="ModulesLoaded">How many modules were open, the checked ones' imports included.</param>
/// <param name="Elapsed">How long verification took.</param>
/// <param name="Declarations">A verdict for every user-facing declaration in the checked modules, plus rejected generated ones.</param>
public sealed record VerificationReport(
    string LeanVersion,
    int ModulesChecked,
    int ModulesLoaded,
    TimeSpan Elapsed,
    IReadOnlyList<DeclarationVerdict> Declarations)
{
    /// <summary>How many declarations are <see cref="VerificationStatus.Verified"/>.</summary>
    public int Verified => Declarations.Count(d => d.Status == VerificationStatus.Verified);
    /// <summary>How many declarations rest on an assumption (<see cref="VerificationStatus.RestsOnAssumption"/>).</summary>
    public int Conditional => Declarations.Count(d => d.Status == VerificationStatus.RestsOnAssumption);
    /// <summary>How many declarations were <see cref="VerificationStatus.Rejected"/>.</summary>
    public int Rejected => Declarations.Count(d => d.Status == VerificationStatus.Rejected);
}

/// <summary>
/// How far verification has got, reported after each declaration (or group of mutually defined ones) is checked.
/// Reports may come from several worker threads.
/// </summary>
/// <param name="Module">The module being checked.</param>
/// <param name="ModuleIndex">Its position among the modules to check, 1-based.</param>
/// <param name="ModuleCount">How many modules are to be checked.</param>
/// <param name="Done">How many declaration groups of this module have been checked.</param>
/// <param name="Total">How many declaration groups this module has.</param>
public sealed record VerificationProgress(string Module, int ModuleIndex, int ModuleCount, int Done, int Total);

/// <summary>A declaration found by <see cref="TenetWorkspace.Search"/>: its full name and the module that defines it.</summary>
/// <param name="Name">The declaration's full name.</param>
/// <param name="Module">The module that defines it.</param>
public sealed record DeclarationSummary(string Name, string Module);

/// <summary>Whether a declaration on the project map is fully proved, and if not, what it rests on.</summary>
public enum MapStatus
{
    /// <summary>Rests on nothing beyond Lean's standard axioms.</summary>
    Proved,
    /// <summary>Uses sorry, itself or through something it uses.</summary>
    RestsOnSorry,
    /// <summary>Uses an axiom the project introduces (and no sorry).</summary>
    RestsOnAxiom,
    /// <summary>An axiom the project introduces.</summary>
    Axiom,
}

/// <summary>A declaration of the project on the project map.</summary>
/// <param name="Name">The declaration's full name.</param>
/// <param name="Module">The module that defines it.</param>
/// <param name="Kind">Tenet's name for its kind: <c>theorem</c>, <c>def</c>, <c>axiom</c>, <c>opaque</c>, <c>inductive</c> or <c>mutual def</c>.</param>
/// <param name="Line">The 1-based line of its keyword in <paramref name="SourceFile"/>, or <see langword="null"/> when unknown.</param>
/// <param name="SourceFile">The <c>.lean</c> file it is written in, or <see langword="null"/> when it cannot be found.</param>
public sealed record MapNode(string Name, string Module, string Kind, int? Line, string? SourceFile)
{
    /// <summary>Whether it is proved, set by <see cref="TenetWorkspace.Map"/>.</summary>
    public MapStatus Status { get; set; }

    /// <summary>It uses sorry (or a project axiom) itself, rather than through a lemma.</summary>
    public bool IsSource { get; set; }

    /// <summary>How many of the project's declarations rest on this one, directly or not.</summary>
    public int UsedBy { get; set; }

    /// <summary>0 for declarations that use nothing else in the project; one more than the highest thing used, otherwise.</summary>
    public int Level { get; set; }
}

/// <summary>The project's own declarations and which uses which (From uses To, as indices into Nodes).</summary>
/// <param name="Nodes">The declarations, in the order their modules were read.</param>
/// <param name="Edges">One entry per use: <c>From</c> uses <c>To</c>, both indices into <paramref name="Nodes"/>.</param>
public sealed record ProjectMap(IReadOnlyList<MapNode> Nodes, IReadOnlyList<(int From, int To)> Edges)
{
    /// <summary>The sorries and axioms worth fixing first: those that the most declarations rest on.</summary>
    public IEnumerable<MapNode> Blockers => Nodes.Where(n => n.IsSource).OrderByDescending(n => n.UsedBy).ThenBy(n => n.Name, StringComparer.Ordinal);
}

/// <summary>One declaration on the way from a theorem down to what it rests on.</summary>
/// <param name="Name">The declaration's name, with generated names shown as the declaration they belong to.</param>
/// <param name="Module">The module that defines it; empty when unknown.</param>
/// <param name="SourceFile">The <c>.lean</c> file it is written in, or <see langword="null"/> when it cannot be found.</param>
/// <param name="Line">The 1-based line of its keyword, or <see langword="null"/> when unknown.</param>
public sealed record TrailLink(string Name, string Module, string? SourceFile, int? Line)
{
    /// <summary>This link is <c>sorry</c> itself (the axiom <c>sorryAx</c>).</summary>
    public bool IsSorry => Name == "sorryAx";

    /// <summary>The name to show: <c>sorry</c> for <c>sorryAx</c>, otherwise <see cref="Name"/>.</summary>
    public string Display => IsSorry ? "sorry" : Name;
}

/// <summary>
/// Why a declaration is not fully proved: the chain of declarations from it to one assumption (sorry, or an axiom
/// the project adds), shortest first. The last declaration before the assumption is where to go and fix it.
/// </summary>
/// <param name="Assumption">The axiom's full name; <c>sorryAx</c> for sorry.</param>
/// <param name="Path">
/// The chain, starting with the declaration asked about and ending with the assumption itself; consecutive links
/// that belong to the same declaration are merged.
/// </param>
public sealed record AssumptionTrail(string Assumption, IReadOnlyList<TrailLink> Path)
{
    /// <summary>The assumption is <c>sorry</c> rather than an axiom.</summary>
    public bool IsSorry => Assumption == "sorryAx";

    /// <summary>The declaration that uses the assumption itself.</summary>
    public TrailLink? Culprit => Path.Count >= 2 ? Path[^2] : null;

    /// <summary>The chain on one line, with arrows between the names.</summary>
    public string Summary => string.Join("  →  ", Path.Select(l => l.Display));
}

/// <summary>What the declaration navigator shows about one declaration, read from the <c>.olean</c> files.</summary>
/// <param name="Name">The declaration's full name.</param>
/// <param name="Kind">Tenet's name for its kind, e.g. <c>theorem</c>, <c>def</c>, <c>inductive</c>.</param>
/// <param name="Module">The module that defines it; empty when unknown.</param>
/// <param name="LevelParams">Its universe level parameters.</param>
/// <param name="Type">Its type, printed, cut off after 4000 characters.</param>
/// <param name="Value">Its value, printed and cut off likewise; <see langword="null"/> for a theorem (proofs are not shown) or a declaration without one.</param>
/// <param name="DocString">Its docstring, or <see langword="null"/> when it has none.</param>
/// <param name="Deprecation">A one-line deprecation notice, or <see langword="null"/> when it is not deprecated.</param>
/// <param name="Line">The 1-based line where Lean's recorded range starts (which may be a doc comment or attribute), or <see langword="null"/>.</param>
/// <param name="Column">The 0-based column of that start, in Unicode code points, or <see langword="null"/>.</param>
/// <param name="SourceFile">The <c>.lean</c> file it is written in, or <see langword="null"/> when it cannot be found.</param>
/// <param name="Uses">The user-facing constants it mentions directly, sorted.</param>
/// <param name="IsUnsafe">It is declared <c>unsafe</c>.</param>
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
///
/// Every public member is thread-safe: access to the modules is serialised with a lock, so a long call (such as
/// verification) blocks the others until it finishes. Dispose it to unmap the files.
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

    /// <summary>The project the workspace was opened for.</summary>
    public LeanProject Project { get; }

    /// <summary>The project's own modules (those built under its .lake/build), not its dependencies.</summary>
    public IReadOnlyList<TenetName> OwnModules { get; }

    /// <summary>The Lean version the modules were built with, from their headers; empty when no module was opened.</summary>
    public string LeanVersion { get; }

    /// <summary>How many modules are open, imports included.</summary>
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
    /// Synchronous: reads module headers from disk. The caller owns the result and must dispose it.
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
            // A module whose source was deleted leaves its build behind; it is no longer part of the project. (Only
            // when sources sit where their module names say, under the root: otherwise none would be found.)
            string SourceOf(string olean) => Path.Combine(project.Root, Path.GetRelativePath(project.BuildLibDirectory, olean)[..^".olean".Length] + ".lean");
            if (own.Any(o => File.Exists(SourceOf(o.Path))))
            {
                own.RemoveAll(o => !File.Exists(SourceOf(o.Path)));
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

    /// <summary>The user-facing declarations in an open module, sorted; empty when the module is not open.</summary>
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
    /// names first (the likeliest intended match). Names Lean generates for itself are left out. At most
    /// <paramref name="limit"/> results; an empty query gives none. The first call lists every declaration, which can
    /// take a while on a large project; later calls reuse that list.
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

    /// <summary>
    /// Everything the navigator shows about the declaration with full name <paramref name="name"/>, or
    /// <see langword="null"/> when no open module defines it. Decodes the declaration and prints its type and value.
    /// </summary>
    /// <param name="name">The declaration.</param>
    /// <param name="module">The module to read it in; needed only when several of the project's modules declare it.</param>
    public DeclarationDetails? Details(string name, string? module = null)
    {
        TenetName n = TenetName.Parse(name);
        lock (_lock)
        {
            TenetName? scope = ScopeFor(n, module);
            ConstantInfo? c = ResolverIn(scope)(n);
            if (c is null)
            {
                return null;
            }
            OleanModule? owner = scope is TenetName sm && OwnDuplicates().TryGetValue(n, out var declaredIn)
                ? _checker.Modules[declaredIn.Contains(sm) ? sm : declaredIn.First(d => ClosureOf(sm).Contains(d))]
                : _checker.Modules.Values.FirstOrDefault(m => m.Contains(n));
            string moduleName = owner is null ? "" : _checker.Modules.First(kv => ReferenceEquals(kv.Value, owner)).Key.ToString();
            SourceRange? range = owner?.SourceRangeOf(n);
            Deprecation? dep = owner?.DeprecationOf(n);
            string? value = c is TheoremInfo ? null : c.Value is Expr v ? Print(v) : null;
            return new DeclarationDetails(
                name,
                c.KindName,
                moduleName,
                c.LevelParams.Select(l => l.ToString()).ToList(),
                Print(c.Type),
                value,
                owner?.DocStringOf(n),
                dep is null ? null : dep.NewName is TenetName nn ? $"deprecated: use {nn}" + (dep.Since is null ? "" : $" (since {dep.Since})") : "deprecated" + (dep.Text is null ? "" : ": " + dep.Text),
                range?.Line,
                range?.Column,
                SourceFileOf(moduleName),
                Replay.UsedConstants(c).Select(u => u.ToString()).Where(IsUserFacing).OrderBy(s => s, StringComparer.Ordinal).ToList(),
                c.IsUnsafe);
        }
    }

    private static string Print(Expr e)
    {
        string s = ExprPrinter.Print(e);
        return s.Length > 4000 ? s[..4000] + " …" : s;
    }

    // ---- names the project declares more than once ----
    //
    // Two of the project's modules can declare the same name when neither imports the other: a challenge statement
    // and the file that proves it, say. Lean never sees both at once, but this workspace opens both. Such a name
    // means the declaration of the module it is read from, or of one it imports, never "whichever": reading it any
    // other way can report the proof as the statement, or lose both.

    private Dictionary<TenetName, List<TenetName>>? _ownDuplicates;
    private readonly Dictionary<TenetName, HashSet<TenetName>> _closures = new();

    /// <summary>The names declared by more than one of the project's own modules, with the modules that declare each.</summary>
    private Dictionary<TenetName, List<TenetName>> OwnDuplicates()
    {
        if (_ownDuplicates is null)
        {
            var declaredIn = new Dictionary<TenetName, List<TenetName>>();
            foreach (TenetName m in OwnModules.Where(_checker.Modules.ContainsKey))
            {
                foreach (TenetName n in _checker.Modules[m].ConstantNames)
                {
                    if (!declaredIn.TryGetValue(n, out List<TenetName>? l))
                    {
                        declaredIn[n] = l = [];
                    }
                    l.Add(m);
                }
            }
            _ownDuplicates = declaredIn.Where(kv => kv.Value.Count > 1).ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        return _ownDuplicates;
    }

    /// <summary>A module and everything it imports, transitively.</summary>
    private HashSet<TenetName> ClosureOf(TenetName module)
    {
        if (!_closures.TryGetValue(module, out HashSet<TenetName>? closure))
        {
            closure = [];
            var stack = new Stack<TenetName>([module]);
            while (stack.TryPop(out TenetName? m))
            {
                if (closure.Add(m) && _checker.Modules.TryGetValue(m, out var om))
                {
                    foreach (var imp in om.Imports)
                    {
                        stack.Push(imp.Module);
                    }
                }
            }
            _closures[module] = closure;
        }
        return closure;
    }

    /// <summary>Resolve names as <paramref name="scope"/> sees them: a name several project modules declare means the one it declares or imports.</summary>
    private Func<TenetName, ConstantInfo?> ResolverIn(TenetName? scope)
    {
        Dictionary<TenetName, List<TenetName>> duplicates = OwnDuplicates();
        if (duplicates.Count == 0)
        {
            return _checker.Resolve;
        }
        return n =>
        {
            if (!duplicates.TryGetValue(n, out List<TenetName>? declaredIn))
            {
                return _checker.Resolve(n);
            }
            if (scope is not TenetName s)
            {
                return null; // ambiguous, and no module to read it from
            }
            TenetName? pick = declaredIn.Contains(s) ? s : declaredIn.FirstOrDefault(m => ClosureOf(s).Contains(m));
            return pick is TenetName m ? _checker.Modules[m].FindConstant(n) : null;
        };
    }

    /// <summary>The project's modules that declare <paramref name="name"/>, when more than one does; empty otherwise.</summary>
    public IReadOnlyList<string> ModulesDeclaring(string name)
    {
        lock (_lock)
        {
            return OwnDuplicates().TryGetValue(TenetName.Parse(name), out List<TenetName>? l) ? l.Select(m => m.ToString()).ToList() : [];
        }
    }

    /// <summary>The module to read <paramref name="name"/> in: the one given, or, when the name is declared once, none is needed.</summary>
    /// <exception cref="InvalidOperationException">Several of the project's modules declare the name and none was given.</exception>
    private TenetName? ScopeFor(TenetName name, string? module)
    {
        if (module is not null)
        {
            return TenetName.Parse(module);
        }
        if (OwnDuplicates().TryGetValue(name, out List<TenetName>? declaredIn))
        {
            throw new InvalidOperationException($"{name} is declared in several modules ({string.Join(", ", declaredIn)}); say which one");
        }
        return null;
    }

    /// <summary>
    /// Every axiom a declaration depends on, transitively (Lean's <c>#print axioms</c>, computed by Tenet), sorted.
    /// Includes <c>sorryAx</c> when it uses sorry.
    /// </summary>
    /// <param name="name">The declaration.</param>
    /// <param name="module">The module to read it in; needed only when several of the project's modules declare it.</param>
    /// <exception cref="KeyNotFoundException">No open module declares it (so "no axioms" can't be mistaken for an answer).</exception>
    /// <exception cref="InvalidOperationException">Several of the project's modules declare it and <paramref name="module"/> is null.</exception>
    public IReadOnlyList<string> AxiomsOf(string name, string? module = null)
    {
        TenetName n = TenetName.Parse(name);
        lock (_lock)
        {
            Func<TenetName, ConstantInfo?> find = ResolverIn(ScopeFor(n, module));
            if (find(n) is null)
            {
                throw new KeyNotFoundException($"no open module declares {name}" + (module is null ? "" : " in the scope of " + module));
            }
            (SortedSet<TenetName> axioms, _) = Replay.AxiomsOf(find, n);
            return axioms.Select(a => a.ToString()).ToList();
        }
    }

    /// <summary>
    /// For each assumption (sorry, or an axiom beyond Lean's standard three) a declaration rests on, the shortest
    /// chain of declarations that leads to it, found breadth-first through what each one uses. Names Lean made
    /// (<c>foo._proof_1</c>, <c>foo.match_1</c>, private names) are shown as the declaration they belong to.
    /// Sorry comes first, then shorter trails. Empty when the declaration is unknown or fully proved.
    /// </summary>
    /// <param name="name">The declaration.</param>
    /// <param name="ct">Cancels the search.</param>
    /// <param name="module">The module to read it in; needed only when several of the project's modules declare it.</param>
    public IReadOnlyList<AssumptionTrail> WhyNotProved(string name, CancellationToken ct = default, string? module = null)
    {
        TenetName start = TenetName.Parse(name);
        lock (_lock)
        {
            Func<TenetName, ConstantInfo?> find = ResolverIn(ScopeFor(start, module));
            if (find(start) is null)
            {
                return [];
            }
            (SortedSet<TenetName> axioms, _) = Replay.AxiomsOf(find, start);
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
                if (find(n) is not ConstantInfo c)
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

    private static readonly HashSet<string> MapKinds = new(StringComparer.Ordinal) { "theorem", "def", "axiom", "opaque", "inductive", "mutual def" };

    /// <summary>
    /// The project map: every declaration in the project's own modules, what it uses among them, and whether it
    /// rests on sorry or a project axiom (propagated along uses; Lean's library is taken as sound). Names Lean
    /// generates are folded into the declaration they belong to. Reads the project's source files to find
    /// declaration lines. Empty when the project has not been built.
    /// </summary>
    public ProjectMap Map(CancellationToken ct = default)
    {
        TenetName sorry = TenetName.Of("sorryAx");
        lock (_lock)
        {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var nodes = new List<MapNode>();
            var uses = new List<HashSet<int>>();
            var direct = new List<(bool Sorry, bool Axiom)>();
            var constants = new List<(TenetName Module, OleanModule File, ConstantInfo Info)>();
            foreach (TenetName m in OwnModules.Where(_checker.Modules.ContainsKey))
            {
                OleanModule om = _checker.Modules[m];
                foreach (TenetName cn in om.ConstantNames)
                {
                    if (_checker.Resolve(cn) is ConstantInfo ci)
                    {
                        constants.Add((m, om, ci));
                    }
                }
            }
            var sources = new Dictionary<TenetName, string[]>();
            string[] SourceLines(TenetName m)
            {
                if (!sources.TryGetValue(m, out string[]? lines))
                {
                    string? f = SourceFileOf(m.ToString());
                    sources[m] = lines = f is null ? [] : File.ReadAllLines(f);
                }
                return lines;
            }
            foreach ((TenetName module, OleanModule file, ConstantInfo ci) in constants)
            {
                string name = ci.Name.ToString();
                if (!IsUserFacing(name) || !MapKinds.Contains(ci.KindName) || index.ContainsKey(name))
                {
                    continue;
                }
                int? line = file.SourceRangeOf(ci.Name)?.Line is int l ? Proofs.ProofSteps.DeclarationLine(SourceLines(module), l) : null;
                index[name] = nodes.Count;
                nodes.Add(new MapNode(name, module.ToString(), ci.KindName, line, SourceFileOf(module.ToString())));
                uses.Add([]);
                direct.Add((false, ci is AxiomInfo && !StandardAxioms.Contains(ci.Name)));
            }
            // Edges, and direct uses of sorry and axioms; generated names count for their owner.
            foreach ((_, _, ConstantInfo ci) in constants)
            {
                ct.ThrowIfCancellationRequested();
                if (!index.TryGetValue(UserFacingOwner(ci.Name.ToString()), out int from))
                {
                    continue;
                }
                foreach (TenetName u in Replay.UsedConstants(ci))
                {
                    if (u == sorry)
                    {
                        direct[from] = (true, direct[from].Axiom);
                    }
                    else if (!StandardAxioms.Contains(u) && _checker.Resolve(u) is AxiomInfo && !index.ContainsKey(u.ToString()))
                    {
                        direct[from] = (direct[from].Sorry, true);
                    }
                    if (index.TryGetValue(UserFacingOwner(u.ToString()), out int to) && to != from)
                    {
                        uses[from].Add(to);
                        if (nodes[to].Kind == "axiom" && direct[to].Axiom)
                        {
                            direct[from] = (direct[from].Sorry, true);
                        }
                    }
                }
            }
            // Status and level by walking uses (memoised; a cycle, which Lean does not allow, would stop at 0).
            var sorryIn = new bool?[nodes.Count];
            var axiomIn = new bool?[nodes.Count];
            var level = new int?[nodes.Count];
            (bool, bool, int) Walk(int i, HashSet<int> path)
            {
                if (sorryIn[i] is bool s0 && axiomIn[i] is bool a0 && level[i] is int l0)
                {
                    return (s0, a0, l0);
                }
                if (!path.Add(i))
                {
                    return (false, false, 0);
                }
                bool s = direct[i].Sorry, a = direct[i].Axiom;
                int lv = 0;
                foreach (int j in uses[i])
                {
                    (bool sj, bool aj, int lj) = Walk(j, path);
                    s |= sj;
                    a |= aj;
                    lv = Math.Max(lv, lj + 1);
                }
                path.Remove(i);
                (sorryIn[i], axiomIn[i], level[i]) = (s, a, lv);
                return (s, a, lv);
            }
            for (int i = 0; i < nodes.Count; i++)
            {
                (bool s, bool a, int lv) = Walk(i, []);
                MapNode n = nodes[i];
                n.Level = lv;
                n.Status = n.Kind == "axiom" ? MapStatus.Axiom : s ? MapStatus.RestsOnSorry : a ? MapStatus.RestsOnAxiom : MapStatus.Proved;
                // Where to fix things: the declarations that use sorry themselves, and the project's axioms.
                n.IsSource = direct[i].Sorry || n.Kind == "axiom";
            }
            // How many declarations rest on each one: reverse reachability.
            var usedBy = new List<int>[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                usedBy[i] = [];
            }
            for (int i = 0; i < nodes.Count; i++)
            {
                foreach (int j in uses[i])
                {
                    usedBy[j].Add(i);
                }
            }
            for (int i = 0; i < nodes.Count; i++)
            {
                var seen = new HashSet<int>();
                var stack = new Stack<int>(usedBy[i]);
                while (stack.Count > 0)
                {
                    int k = stack.Pop();
                    if (seen.Add(k))
                    {
                        foreach (int m in usedBy[k])
                        {
                            stack.Push(m);
                        }
                    }
                }
                nodes[i].UsedBy = seen.Count;
            }
            var edges = new List<(int, int)>();
            for (int i = 0; i < nodes.Count; i++)
            {
                edges.AddRange(uses[i].Select(j => (i, j)));
            }
            return new ProjectMap(nodes, edges);
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

    /// <summary>
    /// The user-facing declaration a generated name belongs to: <c>foo._proof_1</c> → <c>foo</c>;
    /// <c>_private.Mod.0.foo.match_1</c> → <c>foo</c>. Other names are returned unchanged.
    /// </summary>
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

    /// <summary>
    /// Declarations that mention <paramref name="name"/> directly, among the project's own modules (or all, when
    /// <paramref name="everywhere"/> or the project has no built modules), sorted. Only user-facing names are listed.
    /// </summary>
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

    /// <summary>
    /// The .lean file a module was compiled from, if it can be found in the project, its packages, or the toolchain's
    /// sources; <see langword="null"/> otherwise or for an empty module name.
    /// </summary>
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
    /// <param name="modules">
    /// The modules to check, by name; <see langword="null"/> for all of the project's own modules. Names that are not
    /// the project's own are ignored.
    /// </param>
    /// <param name="progress">Told after each declaration group is checked, possibly from several threads.</param>
    /// <param name="ct">Cancels the check; the task then ends as cancelled.</param>
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
                    // The module's own declaration, not whichever module's the name resolves to.
                    if (om.FindConstant(cn) is ConstantInfo ci)
                    {
                        own.Add((m, ci, om));
                    }
                }
            }
            // Names several modules declare are worked out per module below; the rest are shared.
            Dictionary<TenetName, List<TenetName>> duplicates = OwnDuplicates();
            List<ConstantInfo> scope = own.Where(o => !duplicates.ContainsKey(o.Info.Name)).Select(o => o.Info).ToList();

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
                else if (duplicates.ContainsKey(ci.Name))
                {
                    // Declared in several modules: its axioms as this module sees them.
                    var axs = Replay.AxiomsOf(ResolverIn(module), ci.Name).Axioms.Where(a => !StandardAxioms.Contains(a)).Select(a => a.ToString()).ToList();
                    verdicts.Add(new DeclarationVerdict(name, module.ToString(), axs.Count > 0 ? VerificationStatus.RestsOnAssumption : VerificationStatus.Verified, axs, null, line));
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

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            _checker.Dispose();
        }
    }
}
