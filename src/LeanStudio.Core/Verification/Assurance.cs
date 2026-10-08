using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeanStudio.Core.Git;
using LeanStudio.Core.Projects;

namespace LeanStudio.Core.Verification;

/// <summary>How much a declaration can be relied on, from best to worst.</summary>
public enum AssuranceLevel
{
    /// <summary>Checked by Tenet, resting on nothing beyond propext, Classical.choice and Quot.sound.</summary>
    Proved,
    /// <summary>Checked, but its proof believes the output of compiled code (<c>native_decide</c>, <c>Lean.ofReduceBool</c>).</summary>
    TrustsCompiledCode,
    /// <summary>Checked, but it rests on an axiom the project introduces.</summary>
    RestsOnAxiom,
    /// <summary>It rests on <c>sorry</c>: some proof it needs has not been written.</summary>
    RestsOnSorry,
    /// <summary>Tenet's kernel rejected it.</summary>
    Rejected,
}

/// <summary>What the assurance report says about one declaration.</summary>
/// <param name="Name">The declaration's full name.</param>
/// <param name="Module">The module that defines it.</param>
/// <param name="Level">The worst thing it rests on.</param>
/// <param name="Axioms">The axioms the project introduces that it rests on, sorted.</param>
/// <param name="CompiledCode">
/// What makes its proof trust compiled code: the tactic (<c>native_decide</c>, <c>bv_decide</c>) or the axiom
/// (<c>Lean.ofReduceBool</c>, <c>Lean.trustCompiler</c>), sorted.
/// </param>
/// <param name="Message">Tenet's message when it rejected the declaration or could not check it; otherwise <see langword="null"/>.</param>
/// <param name="Line">The 1-based line of its keyword in the source file, or <see langword="null"/> when unknown.</param>
/// <param name="SourceFile">The <c>.lean</c> file it is written in, or <see langword="null"/> when it cannot be found.</param>
public sealed record AssuredDeclaration(
    string Name,
    string Module,
    AssuranceLevel Level,
    IReadOnlyList<string> Axioms,
    IReadOnlyList<string> CompiledCode,
    string? Message,
    int? Line,
    string? SourceFile);

/// <summary>An axiom the project introduces, and how many of its declarations rest on it.</summary>
/// <param name="Name">The axiom's full name.</param>
/// <param name="Declarations">How many of the project's declarations rest on it.</param>
/// <param name="Allowed">It was named with <c>--allow-axiom</c>: a deliberate, documented assumption.</param>
public sealed record AxiomUse(string Name, int Declarations, bool Allowed);

/// <summary>What fails an assurance check: the categories that count, and the axioms that are accepted.</summary>
/// <param name="FailOn">
/// The categories that fail the check: <c>rejected</c>, <c>sorry</c>, <c>axiom</c>, <c>native</c>, the trust
/// marks <c>implemented_by</c>, <c>extern</c>, <c>unsafe</c>, <c>partial</c> and <c>opaque</c>, and <c>unstated</c>
/// (a definition no theorem's statement mentions).
/// </param>
/// <param name="AllowedAxioms">Axioms that do not count as <c>axiom</c>: deliberate assumptions the project documents.</param>
public sealed record AssurancePolicy(IReadOnlySet<string> FailOn, IReadOnlySet<string> AllowedAxioms)
{
    /// <summary>Every category a policy can name.</summary>
    public static readonly IReadOnlyList<string> Categories =
        ["rejected", "sorry", "axiom", "native", "implemented_by", "extern", "unsafe", "partial", "opaque", "unstated"];

    /// <summary>The default: fail on what Tenet rejects and on <c>sorry</c>, and report the rest.</summary>
    public static AssurancePolicy Default { get; } =
        new(new HashSet<string>(["rejected", "sorry"], StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    /// <summary>
    /// Read a comma-separated list of categories, where <c>all</c> means every one and <c>none</c> none.
    /// </summary>
    /// <exception cref="FormatException">A name that is not a category.</exception>
    public static IReadOnlySet<string> ParseCategories(string list)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string c = raw.ToLowerInvariant().Replace('-', '_');
            if (c == "all")
            {
                set.UnionWith(Categories);
            }
            else if (c == "none")
            {
                continue;
            }
            else if (Categories.Contains(c))
            {
                set.Add(c);
            }
            else
            {
                throw new FormatException($"'{raw}' is not one of: {string.Join(", ", Categories)}, all, none");
            }
        }
        return set;
    }
}

/// <summary>
/// A one-page answer to "can this be relied on?": what is proved, what rests on <c>sorry</c> or on axioms the
/// project adds, which proofs believe compiled code, what Tenet rejects, and every declaration that widens the trust
/// surface (<see cref="TrustMark"/>). Made from a Tenet verification and the build's attribute tables.
/// </summary>
/// <param name="Project">The project's folder name.</param>
/// <param name="Root">The project's folder, which source paths in the outputs are written relative to.</param>
/// <param name="Commit">The commit it was made at, or <see langword="null"/> outside git.</param>
/// <param name="Dirty">The working tree had changes that are not committed.</param>
/// <param name="LeanVersion">The Lean version the modules were built with.</param>
/// <param name="TenetVersion">The version of Tenet that checked them.</param>
/// <param name="Generated">When it was made.</param>
/// <param name="ModulesChecked">How many of the project's modules were re-checked.</param>
/// <param name="Elapsed">How long verification took.</param>
/// <param name="Declarations">Every declaration of the checked modules, worst first.</param>
/// <param name="Axioms">The axioms the project introduces, most used first.</param>
/// <param name="Marks">The declarations that widen the trust surface.</param>
/// <param name="Policy">What fails the check.</param>
/// <param name="Definitions">How many definitions the project has that something could be proved about (see <see cref="TenetWorkspace.Coverage"/>).</param>
/// <param name="Unstated">The definitions no theorem's statement mentions.</param>
/// <param name="UnitsReused">How many checking units passed before unchanged and were not checked again.</param>
/// <param name="UnitsChecked">How many checking units were checked this time.</param>
public sealed record AssuranceReport(
    string Project,
    string Root,
    string? Commit,
    bool Dirty,
    string LeanVersion,
    string TenetVersion,
    DateTimeOffset Generated,
    int ModulesChecked,
    TimeSpan Elapsed,
    IReadOnlyList<AssuredDeclaration> Declarations,
    IReadOnlyList<AxiomUse> Axioms,
    IReadOnlyList<TrustMark> Marks,
    AssurancePolicy Policy,
    int Definitions = 0,
    IReadOnlyList<DeclarationRef>? Unstated = null,
    int UnitsReused = 0,
    int UnitsChecked = 0)
{
    /// <summary>The definitions no theorem's statement mentions; empty when coverage was not computed.</summary>
    public IReadOnlyList<DeclarationRef> UnstatedDefinitions => Unstated ?? [];

    /// <summary>How many declarations are at <paramref name="level"/>.</summary>
    public int Count(AssuranceLevel level) => Declarations.Count(d => d.Level == level);

    /// <summary>How many declarations carry the trust mark <paramref name="kind"/>.</summary>
    public int Count(TrustKind kind) => Marks.Count(m => m.Kind == kind);

    /// <summary>The policy categories that this report breaks, each with how many items break it, in policy order.</summary>
    public IReadOnlyList<(string Category, int Count)> Failures
    {
        get
        {
            var list = new List<(string, int)>();
            foreach (string c in AssurancePolicy.Categories.Where(Policy.FailOn.Contains))
            {
                int n = c switch
                {
                    "rejected" => Count(AssuranceLevel.Rejected),
                    "sorry" => Count(AssuranceLevel.RestsOnSorry),
                    "axiom" => Declarations.Count(d => d.Axioms.Any(a => !Policy.AllowedAxioms.Contains(a))),
                    "native" => Declarations.Count(d => d.CompiledCode.Count > 0),
                    "unstated" => UnstatedDefinitions.Count,
                    _ => Marks.Count(m => Assurance.Category(m.Kind) == c),
                };
                if (n > 0)
                {
                    list.Add((c, n));
                }
            }
            return list;
        }
    }

    /// <summary>Nothing breaks the policy.</summary>
    public bool Passed => Failures.Count == 0;
}

/// <summary>Making an <see cref="AssuranceReport"/>, writing it out, and the <c>leanstudio --verify</c> command.</summary>
public static class Assurance
{
    /// <summary>The axioms in Lean's library that make a proof believe compiled code.</summary>
    private static readonly HashSet<string> CompiledCodeAxioms = new(StringComparer.Ordinal)
    {
        "Lean.ofReduceBool", "Lean.ofReduceNat", "Lean.trustCompiler",
    };

    /// <summary>
    /// What makes an assumption one of compiled code, or <see langword="null"/> when it is not: the tactic for the
    /// axiom <c>native_decide</c> and <c>bv_decide</c> add (<c>foo._native.native_decide.ax_1</c>, since Lean 4.24),
    /// or the axiom's own name for <c>Lean.ofReduceBool</c> and its kin.
    /// </summary>
    public static string? CompiledCodeOf(string axiom)
    {
        if (CompiledCodeAxioms.Contains(axiom))
        {
            return axiom;
        }
        string[] parts = axiom.Split('.');
        int i = Array.IndexOf(parts, "_native");
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : null;
    }

    /// <summary>The policy category of a trust mark: <c>implemented_by</c>, <c>extern</c>, <c>unsafe</c>, <c>partial</c> or <c>opaque</c>.</summary>
    public static string Category(TrustKind kind) => kind switch
    {
        TrustKind.ImplementedBy => "implemented_by",
        TrustKind.Extern => "extern",
        TrustKind.Unsafe => "unsafe",
        TrustKind.Partial => "partial",
        _ => "opaque",
    };

    /// <summary>How a trust mark is written in Lean.</summary>
    public static string Keyword(TrustKind kind) => kind switch
    {
        TrustKind.ImplementedBy => "@[implemented_by]",
        TrustKind.Extern => "@[extern]",
        TrustKind.Unsafe => "unsafe",
        TrustKind.Partial => "partial",
        _ => "opaque",
    };

    /// <summary>What a trust mark means for someone relying on the project, in one line.</summary>
    public static string Meaning(TrustKind kind) => kind switch
    {
        TrustKind.ImplementedBy => "the code that runs is a different definition from the one the proofs are about",
        TrustKind.Extern => "the code that runs is foreign (C) code, not the definition the proofs are about",
        TrustKind.Unsafe => "outside Lean's logic: nothing can be proved about it",
        TrustKind.Partial => "proofs see an opaque constant, never the recursive body that runs",
        _ => "proofs know its type and nothing about its value",
    };

    /// <summary>
    /// Sort a verification into an assurance report, with the project's trust marks. Tenet refusing a declaration
    /// because it calls compiled code (<c>Lean.reduceBool</c>, as <c>native_decide</c> did before Lean 4.24) counts as
    /// trusting compiled code, not as a rejection: the proof is not wrong, it is one an external checker cannot follow.
    /// </summary>
    public static AssuranceReport Build(
        LeanProject project,
        VerificationReport verification,
        IReadOnlyList<TrustMark> marks,
        AssurancePolicy policy,
        string? commit = null,
        bool dirty = false,
        Func<string, string?>? sourceFileOf = null,
        DateTimeOffset? now = null,
        IReadOnlyList<StatedDefinition>? coverage = null)
    {
        var declarations = new List<AssuredDeclaration>();
        var axiomUses = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (DeclarationVerdict v in verification.Declarations)
        {
            var axioms = new List<string>();
            var compiled = new SortedSet<string>(StringComparer.Ordinal);
            bool sorry = false;
            foreach (string a in v.Assumptions)
            {
                if (a == "sorryAx")
                {
                    sorry = true;
                }
                else if (CompiledCodeOf(a) is string c)
                {
                    compiled.Add(c);
                }
                else
                {
                    axioms.Add(a);
                    axiomUses[a] = axiomUses.GetValueOrDefault(a) + 1;
                }
            }
            bool nativeRefusal = v.Status == VerificationStatus.Rejected && v.Message is string m
                && (m.Contains("Lean.reduceBool", StringComparison.Ordinal) || m.Contains("Lean.reduceNat", StringComparison.Ordinal));
            if (nativeRefusal)
            {
                compiled.Add("Lean.ofReduceBool");
            }
            AssuranceLevel level =
                v.Status == VerificationStatus.Rejected && !nativeRefusal ? AssuranceLevel.Rejected
                : sorry ? AssuranceLevel.RestsOnSorry
                : axioms.Count > 0 ? AssuranceLevel.RestsOnAxiom
                : compiled.Count > 0 ? AssuranceLevel.TrustsCompiledCode
                : AssuranceLevel.Proved;
            declarations.Add(new AssuredDeclaration(
                v.Name, v.Module, level, axioms, compiled.ToList(),
                v.Status == VerificationStatus.Rejected ? v.Message : null,
                v.Line, sourceFileOf?.Invoke(v.Module)));
        }
        // An axiom is a declaration too; the count is of what rests on it.
        var axiomList = axiomUses
            .Select(kv => new AxiomUse(kv.Key, kv.Value - (declarations.Any(d => d.Name == kv.Key) ? 1 : 0), policy.AllowedAxioms.Contains(kv.Key)))
            .OrderByDescending(a => a.Declarations).ThenBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
        return new AssuranceReport(
            project.Name,
            project.Root,
            commit,
            dirty,
            verification.LeanVersion,
            TenetVersion,
            now ?? DateTimeOffset.Now,
            verification.ModulesChecked,
            verification.Elapsed,
            declarations
                .OrderByDescending(d => d.Level)
                .ThenBy(d => d.Module, StringComparer.Ordinal)
                .ThenBy(d => d.Line ?? int.MaxValue)
                .ThenBy(d => d.Name, StringComparer.Ordinal)
                .ToList(),
            axiomList,
            marks,
            policy,
            coverage?.Count ?? 0,
            coverage?.Where(c => c.Theorems.Count == 0).Select(c => c.Definition).ToList(),
            verification.UnitsReused,
            verification.UnitsChecked);
    }

    /// <summary>
    /// Verify a built project with Tenet and make its assurance report: the commit, the verification, the trust
    /// marks. The workspace is opened and disposed here.
    /// </summary>
    public static async Task<AssuranceReport> RunAsync(LeanProject project, AssurancePolicy policy, IProgress<VerificationProgress>? progress = null, CancellationToken ct = default, bool useCache = true)
    {
        using TenetWorkspace ws = TenetWorkspace.Open(project);
        return await RunAsync(ws, policy, progress, ct, useCache).ConfigureAwait(false);
    }

    /// <summary>Make the assurance report with a workspace that is already open (the window's, or the MCP server's).</summary>
    public static async Task<AssuranceReport> RunAsync(TenetWorkspace ws, AssurancePolicy policy, IProgress<VerificationProgress>? progress = null, CancellationToken ct = default, bool useCache = true)
    {
        if (ws.OwnModules.Count == 0)
        {
            throw new InvalidOperationException("Nothing is built yet: build the project first (lake build).");
        }
        VerificationReport verification = await ws.VerifyAsync(progress: progress, ct: ct, useCache: useCache).ConfigureAwait(false);
        IReadOnlyList<TrustMark> marks = ws.TrustSurface(ct);
        IReadOnlyList<StatedDefinition> coverage = ws.Coverage(ct);
        (string? commit, bool dirty) = await CommitOfAsync(ws.Project.Root, ct).ConfigureAwait(false);
        return Build(ws.Project, verification, marks, policy, commit, dirty, ws.SourceFileOf, coverage: coverage);
    }

    private static async Task<(string? Commit, bool Dirty)> CommitOfAsync(string root, CancellationToken ct)
    {
        if (GitRepository.Find(root) is not GitRepository repo)
        {
            return (null, false);
        }
        try
        {
            var head = await repo.RunAsync(["rev-parse", "--short=12", "HEAD"], ct: ct).ConfigureAwait(false);
            if (!head.Success)
            {
                return (null, false);
            }
            var status = await repo.RunAsync(["status", "--porcelain", "--untracked-files=no", "--", "."], ct: ct).ConfigureAwait(false);
            return (head.Output.Trim(), status.Success && status.Output.Trim().Length > 0);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            return (null, false);
        }
    }

    /// <summary>The version of the Tenet kernel built into Lean Studio.</summary>
    public static string TenetVersion { get; } =
        typeof(Tenet.Kernel.ConstantInfo).Assembly.GetName().Version is Version v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    // ---- writing it out ----

    private static string Plural(int n, string one, string? many = null) =>
        n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many ?? one + "s");

    private static string Where(AssuranceReport r, string? file, int? line) =>
        file is null ? "" : Path.GetRelativePath(r.Root, file).Replace('\\', '/') + (line is int l ? $":{l}" : "");

    /// <summary>The level in a few words.</summary>
    public static string Describe(AssuranceLevel level) => level switch
    {
        AssuranceLevel.Proved => "proved",
        AssuranceLevel.TrustsCompiledCode => "trusts compiled code",
        AssuranceLevel.RestsOnAxiom => "rests on a project axiom",
        AssuranceLevel.RestsOnSorry => "rests on sorry",
        _ => "rejected by Tenet",
    };

    /// <summary>The verdict in one sentence, which every format starts with.</summary>
    public static string Headline(AssuranceReport r)
    {
        int total = r.Declarations.Count;
        int proved = r.Count(AssuranceLevel.Proved);
        string share = total == 0 ? "" : $" ({(100.0 * proved / total).ToString("F1", CultureInfo.InvariantCulture)}%)";
        string verdict = r.Passed
            ? "Passed"
            : "Failed: " + string.Join(", ", r.Failures.Select(f => $"{f.Category} ({f.Count})"));
        return $"{verdict}. {Plural(proved, "declaration")} of {total.ToString("N0", CultureInfo.InvariantCulture)} proved outright{share}.";
    }

    /// <summary>The report as Markdown, for a pull request comment or a CI job summary. Long lists are cut at <paramref name="limit"/> rows.</summary>
    public static string ToMarkdown(AssuranceReport r, int limit = 50)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"### Assurance report: {r.Project}");
        if (r.Commit is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $" at `{r.Commit}`{(r.Dirty ? " (with uncommitted changes)" : "")}");
        }
        sb.Append("\n\n");
        sb.Append(r.Passed ? "✅ " : "❌ ").Append(Headline(r)).Append("\n\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"Re-checked by Tenet {r.TenetVersion}, an independent Lean kernel, in {r.Elapsed.TotalSeconds:F1}s: {Plural(r.ModulesChecked, "module")}, Lean {r.LeanVersion}");
        if (r.UnitsReused > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"; {r.UnitsChecked:N0} checked, and {r.UnitsReused:N0} unchanged since they last passed not checked again");
        }
        sb.Append(". ");
        sb.Append("Fails on: ").Append(r.Policy.FailOn.Count == 0 ? "nothing" : string.Join(", ", AssurancePolicy.Categories.Where(r.Policy.FailOn.Contains))).Append(".\n\n");

        sb.Append("| | Declarations | What it means |\n|---|---:|---|\n");
        void Row(AssuranceLevel level, string meaning)
        {
            int n = r.Count(level);
            sb.Append(CultureInfo.InvariantCulture, $"| {Capitalize(Describe(level))} | {n:N0} | {meaning} |\n");
        }
        Row(AssuranceLevel.Proved, "checked by a second kernel; needs only propext, Classical.choice, Quot.sound");
        Row(AssuranceLevel.TrustsCompiledCode, "the proof believes the output of compiled code (native_decide), which no kernel checks");
        Row(AssuranceLevel.RestsOnAxiom, "true if the project's own axioms are");
        Row(AssuranceLevel.RestsOnSorry, "unfinished: a proof it needs is missing");
        Row(AssuranceLevel.Rejected, "Tenet's kernel does not accept it");
        sb.Append('\n');

        if (r.Axioms.Count > 0)
        {
            sb.Append("**Axioms the project introduces**\n\n| Axiom | Rested on by | |\n|---|---:|---|\n");
            foreach (AxiomUse a in r.Axioms.Take(limit))
            {
                sb.Append(CultureInfo.InvariantCulture, $"| `{a.Name}` | {Plural(a.Declarations, "declaration")} | {(a.Allowed ? "allowed" : "")} |\n");
            }
            More(sb, r.Axioms.Count, limit);
            sb.Append('\n');
        }

        if (r.Marks.Count > 0)
        {
            sb.Append("**Trust surface**: code whose running behavior is not what the proofs are about\n\n| Declaration | Kind | Where | Meaning |\n|---|---|---|---|\n");
            foreach (TrustMark m in r.Marks.OrderBy(m => m.Kind).Take(limit))
            {
                sb.Append(CultureInfo.InvariantCulture, $"| `{m.Name}` | `{Keyword(m.Kind)}` | {Where(r, m.SourceFile, m.Line)} | {Meaning(m.Kind)} |\n");
            }
            More(sb, r.Marks.Count, limit);
            sb.Append('\n');
        }

        if (r.Definitions > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"**What the theorems are about**: {r.Definitions - r.UnstatedDefinitions.Count:N0} of {r.Definitions:N0} definitions have a theorem whose statement mentions them.");
            if (r.UnstatedDefinitions.Count > 0)
            {
                sb.Append(" No theorem mentions these, so nothing proved depends on them being right:\n\n| Definition | Where |\n|---|---|\n");
                foreach (DeclarationRef d in r.UnstatedDefinitions.Take(limit))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"| `{d.Name}` | {Where(r, d.SourceFile, d.Line)} |\n");
                }
                More(sb, r.UnstatedDefinitions.Count, limit);
            }
            sb.Append("\n\n");
        }

        List<AssuredDeclaration> notProved = r.Declarations.Where(d => d.Level != AssuranceLevel.Proved).ToList();
        if (notProved.Count > 0)
        {
            sb.Append("**Not proved outright**\n\n| Declaration | Status | Rests on | Where |\n|---|---|---|---|\n");
            foreach (AssuredDeclaration d in notProved.Take(limit))
            {
                sb.Append(CultureInfo.InvariantCulture, $"| `{d.Name}` | {Describe(d.Level)} | {RestsOn(d)} | {Where(r, d.SourceFile, d.Line)} |\n");
            }
            More(sb, notProved.Count, limit);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void More(StringBuilder sb, int count, int limit)
    {
        if (count > limit)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n…and {count - limit:N0} more (the JSON report lists them all).\n");
        }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string RestsOn(AssuredDeclaration d)
    {
        var parts = new List<string>();
        if (d.Level == AssuranceLevel.RestsOnSorry)
        {
            parts.Add("`sorry`");
        }
        parts.AddRange(d.Axioms.Select(a => $"`{a}`"));
        parts.AddRange(d.CompiledCode.Select(c => $"`{c}`"));
        if (d.Level == AssuranceLevel.Rejected && d.Message is string m)
        {
            parts.Add(m.Length > 120 ? m[..120].ReplaceLineEndings(" ") + "…" : m.ReplaceLineEndings(" "));
        }
        return string.Join(", ", parts).Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Every fact in the report as JSON, nothing cut: for tools, dashboards and archives.</summary>
    public static string ToJson(AssuranceReport r)
    {
        JsonObject Loc(string? file, int? line) => new()
        {
            ["file"] = file is null ? null : Path.GetRelativePath(r.Root, file).Replace('\\', '/'),
            ["line"] = line,
        };
        var json = new JsonObject
        {
            ["schema"] = "https://github.com/keithadler/leanstudio/assurance/v1",
            ["project"] = r.Project,
            ["commit"] = r.Commit,
            ["dirty"] = r.Dirty,
            ["lean"] = r.LeanVersion,
            ["tenet"] = r.TenetVersion,
            ["generated"] = r.Generated.ToString("o", CultureInfo.InvariantCulture),
            ["modulesChecked"] = r.ModulesChecked,
            ["seconds"] = Math.Round(r.Elapsed.TotalSeconds, 1),
            ["unitsChecked"] = r.UnitsChecked,
            ["unitsReused"] = r.UnitsReused,
            ["passed"] = r.Passed,
            ["headline"] = Headline(r),
            ["policy"] = new JsonObject
            {
                ["failOn"] = new JsonArray(AssurancePolicy.Categories.Where(r.Policy.FailOn.Contains).Select(c => (JsonNode?)c).ToArray()),
                ["allowedAxioms"] = new JsonArray(r.Policy.AllowedAxioms.Order(StringComparer.Ordinal).Select(c => (JsonNode?)c).ToArray()),
            },
            ["failures"] = new JsonObject(r.Failures.Select(f => KeyValuePair.Create(f.Category, (JsonNode?)f.Count))),
            ["counts"] = new JsonObject
            {
                ["declarations"] = r.Declarations.Count,
                ["proved"] = r.Count(AssuranceLevel.Proved),
                ["trustsCompiledCode"] = r.Count(AssuranceLevel.TrustsCompiledCode),
                ["restsOnAxiom"] = r.Count(AssuranceLevel.RestsOnAxiom),
                ["restsOnSorry"] = r.Count(AssuranceLevel.RestsOnSorry),
                ["rejected"] = r.Count(AssuranceLevel.Rejected),
                ["implementedBy"] = r.Count(TrustKind.ImplementedBy),
                ["extern"] = r.Count(TrustKind.Extern),
                ["unsafe"] = r.Count(TrustKind.Unsafe),
                ["partial"] = r.Count(TrustKind.Partial),
                ["opaque"] = r.Count(TrustKind.Opaque),
            },
            ["axioms"] = new JsonArray(r.Axioms.Select(a => (JsonNode?)new JsonObject
            {
                ["name"] = a.Name,
                ["declarations"] = a.Declarations,
                ["allowed"] = a.Allowed,
            }).ToArray()),
            ["coverage"] = new JsonObject
            {
                ["definitions"] = r.Definitions,
                ["stated"] = r.Definitions - r.UnstatedDefinitions.Count,
                ["unstated"] = new JsonArray(r.UnstatedDefinitions.Select(d => (JsonNode?)new JsonObject
                {
                    ["name"] = d.Name,
                    ["module"] = d.Module,
                    ["location"] = Loc(d.SourceFile, d.Line),
                }).ToArray()),
            },
            ["trustSurface"] = new JsonArray(r.Marks.Select(m => (JsonNode?)new JsonObject
            {
                ["name"] = m.Name,
                ["module"] = m.Module,
                ["kind"] = Category(m.Kind),
                ["location"] = Loc(m.SourceFile, m.Line),
            }).ToArray()),
            ["declarations"] = new JsonArray(r.Declarations.Select(d => (JsonNode?)new JsonObject
            {
                ["name"] = d.Name,
                ["module"] = d.Module,
                ["status"] = d.Level switch
                {
                    AssuranceLevel.Proved => "proved",
                    AssuranceLevel.TrustsCompiledCode => "trustsCompiledCode",
                    AssuranceLevel.RestsOnAxiom => "restsOnAxiom",
                    AssuranceLevel.RestsOnSorry => "restsOnSorry",
                    _ => "rejected",
                },
                ["axioms"] = new JsonArray(d.Axioms.Select(a => (JsonNode?)a).ToArray()),
                ["compiledCode"] = new JsonArray(d.CompiledCode.Select(a => (JsonNode?)a).ToArray()),
                ["message"] = d.Message,
                ["location"] = Loc(d.SourceFile, d.Line),
            }).ToArray()),
        };
        return json.ToJsonString(Indented) + "\n";
    }

    /// <summary>
    /// The report as SARIF 2.1.0, for GitHub code scanning: one result per declaration that is not proved outright
    /// and per trust mark, at its line. Those the policy fails on are errors; the rest are notes.
    /// </summary>
    public static string ToSarif(AssuranceReport r)
    {
        (string Id, string Name, string Text)[] rules =
        [
            ("rejected", "RejectedByTenet", "Tenet's independent kernel rejects this declaration."),
            ("sorry", "RestsOnSorry", "This declaration rests on sorry: a proof it needs is missing."),
            ("axiom", "RestsOnProjectAxiom", "This declaration rests on an axiom the project introduces."),
            ("native", "TrustsCompiledCode", "This proof believes the output of compiled code (native_decide, Lean.ofReduceBool), which no kernel checks."),
            ("implemented_by", "ImplementedBy", "@[implemented_by]: the code that runs is a different definition from the one the proofs are about."),
            ("extern", "Extern", "@[extern]: the code that runs is foreign code, not the definition the proofs are about."),
            ("unsafe", "Unsafe", "unsafe: outside Lean's logic, so nothing can be proved about it."),
            ("partial", "Partial", "partial: proofs see an opaque constant, never the recursive body that runs."),
            ("opaque", "Opaque", "opaque: proofs know its type and nothing about its value."),
            ("unstated", "Unstated", "No theorem's statement mentions this definition, so nothing proved depends on it being right."),
        ];
        var results = new JsonArray();
        void Add(string rule, string message, string? file, int? line, bool counts = true)
        {
            if (file is null)
            {
                return;
            }
            var region = new JsonObject { ["startLine"] = line ?? 1 };
            results.Add(new JsonObject
            {
                ["ruleId"] = rule,
                ["level"] = counts && r.Policy.FailOn.Contains(rule) ? "error" : "note",
                ["message"] = new JsonObject { ["text"] = message },
                ["locations"] = new JsonArray(new JsonObject
                {
                    ["physicalLocation"] = new JsonObject
                    {
                        ["artifactLocation"] = new JsonObject { ["uri"] = Path.GetRelativePath(r.Root, file).Replace('\\', '/'), ["uriBaseId"] = "%SRCROOT%" },
                        ["region"] = region,
                    },
                }),
            });
        }
        foreach (AssuredDeclaration d in r.Declarations)
        {
            switch (d.Level)
            {
                case AssuranceLevel.Rejected:
                    Add("rejected", $"{d.Name} is rejected by Tenet: {d.Message}", d.SourceFile, d.Line);
                    break;
                case AssuranceLevel.RestsOnSorry:
                    Add("sorry", $"{d.Name} rests on sorry.", d.SourceFile, d.Line);
                    break;
            }
            foreach (string a in d.Axioms)
            {
                bool allowed = r.Policy.AllowedAxioms.Contains(a);
                Add("axiom", $"{d.Name} rests on the axiom {a}" + (allowed ? " (allowed)." : "."), d.SourceFile, d.Line, counts: !allowed);
            }
            if (d.CompiledCode.Count > 0)
            {
                Add("native", $"{d.Name} trusts compiled code ({string.Join(", ", d.CompiledCode)}).", d.SourceFile, d.Line);
            }
        }
        foreach (TrustMark m in r.Marks)
        {
            Add(Category(m.Kind), $"{m.Name} is {Keyword(m.Kind)}: {Meaning(m.Kind)}.", m.SourceFile, m.Line);
        }
        foreach (DeclarationRef d in r.UnstatedDefinitions)
        {
            Add("unstated", $"No theorem's statement mentions {d.Name}.", d.SourceFile, d.Line);
        }
        var sarif = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["driver"] = new JsonObject
                    {
                        ["name"] = "Lean Studio assurance",
                        ["informationUri"] = "https://github.com/keithadler/leanstudio",
                        ["semanticVersion"] = r.TenetVersion,
                        ["rules"] = new JsonArray(rules.Select(x => (JsonNode?)new JsonObject
                        {
                            ["id"] = x.Id,
                            ["name"] = x.Name,
                            ["shortDescription"] = new JsonObject { ["text"] = x.Text },
                        }).ToArray()),
                    },
                },
                ["results"] = results,
            }),
        };
        return sarif.ToJsonString(Indented) + "\n";
    }

    /// <summary>
    /// A shields.io endpoint badge (<c>https://img.shields.io/endpoint?url=…</c>): "proofs: 412 proved, 3 sorry".
    /// Green when it passes with everything proved, yellow when it passes with something not proved, red when it fails.
    /// </summary>
    public static string ToBadge(AssuranceReport r)
    {
        var parts = new List<string> { $"{r.Count(AssuranceLevel.Proved).ToString("N0", CultureInfo.InvariantCulture)} proved" };
        void Part(AssuranceLevel l, string word)
        {
            if (r.Count(l) > 0)
            {
                parts.Add($"{r.Count(l).ToString("N0", CultureInfo.InvariantCulture)} {word}");
            }
        }
        Part(AssuranceLevel.RestsOnSorry, "sorry");
        Part(AssuranceLevel.RestsOnAxiom, "on axioms");
        Part(AssuranceLevel.TrustsCompiledCode, "native");
        Part(AssuranceLevel.Rejected, "rejected");
        string color = !r.Passed ? "red" : parts.Count == 1 ? "brightgreen" : "yellow";
        var badge = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["label"] = "proofs",
            ["message"] = string.Join(", ", parts),
            ["color"] = color,
        };
        return badge.ToJsonString() + "\n";
    }

    /// <summary>
    /// The report as one self-contained HTML page: the page an auditor reads. It prints on its own (a browser's
    /// Print ▸ Save as PDF makes the PDF), with no scripts and nothing fetched.
    /// </summary>
    public static string ToHtml(AssuranceReport r)
    {
        static string H(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            """);
        sb.Append(CultureInfo.InvariantCulture, $"<title>Assurance report: {H(r.Project)}</title>\n");
        sb.Append("""
            <style>
            :root { --ink:#1b1f24; --muted:#59636e; --rule:#d8dee4; --pass:#1a7f37; --fail:#cf222e; --note:#9a6700; --bg:#ffffff; --band:#f6f8fa; }
            @media (prefers-color-scheme: dark) { :root { --ink:#e6edf3; --muted:#9198a1; --rule:#3d444d; --pass:#3fb950; --fail:#f85149; --note:#d29922; --bg:#0d1117; --band:#151b23; } }
            * { box-sizing: border-box; }
            body { margin: 0; background: var(--bg); color: var(--ink); font: 14px/1.5 -apple-system, BlinkMacSystemFont, "Segoe UI", Inter, sans-serif; }
            main { max-width: 960px; margin: 0 auto; padding: 32px 16px 48px; }
            h1 { font-size: 24px; margin: 0 0 4px; }
            h2 { font-size: 15px; margin: 28px 0 8px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted); }
            .meta { color: var(--muted); margin: 0 0 20px; }
            .verdict { padding: 14px 16px; border-radius: 8px; background: var(--band); border-left: 5px solid var(--pass); font-size: 16px; font-weight: 600; }
            .verdict.fail { border-left-color: var(--fail); }
            .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 10px; margin-top: 16px; }
            .tile { border: 1px solid var(--rule); border-radius: 8px; padding: 10px 12px; }
            .tile b { display: block; font-size: 22px; font-variant-numeric: tabular-nums; }
            .tile span { color: var(--muted); font-size: 12px; }
            .tile.bad b { color: var(--fail); } .tile.warn b { color: var(--note); } .tile.good b { color: var(--pass); }
            table { width: 100%; border-collapse: collapse; }
            th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--rule); vertical-align: top; }
            th { font-weight: 600; color: var(--muted); font-size: 12px; }
            td.n { text-align: right; font-variant-numeric: tabular-nums; }
            code { font: 12.5px ui-monospace, SFMono-Regular, Menlo, monospace; overflow-wrap: anywhere; }
            .scroll { overflow-x: auto; }
            td.nw code { white-space: nowrap; overflow-wrap: normal; }
            .none { color: var(--muted); }
            footer { margin-top: 32px; color: var(--muted); font-size: 12px; }
            @media print { body { background: #fff; color: #000; } main { padding: 0; } .verdict { background: #fff; } h2 { break-after: avoid; } tr { break-inside: avoid; } }
            </style></head><body><main>
            """);
        sb.Append(CultureInfo.InvariantCulture, $"<h1>Assurance report: {H(r.Project)}</h1>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<p class=\"meta\">{(r.Commit is null ? "Not in git" : $"Commit <code>{H(r.Commit)}</code>{(r.Dirty ? " with uncommitted changes" : "")}")} · Lean {H(r.LeanVersion)} · re-checked by Tenet {H(r.TenetVersion)} · {H(r.Generated.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture))}</p>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<div class=\"verdict{(r.Passed ? "" : " fail")}\">{H(Headline(r))}</div>\n");
        sb.Append("<div class=\"tiles\">");
        void Tile(int n, string label, string tone) =>
            sb.Append(CultureInfo.InvariantCulture, $"<div class=\"tile {(n == 0 ? "" : tone)}\"><b>{n:N0}</b><span>{H(label)}</span></div>");
        Tile(r.Count(AssuranceLevel.Proved), "proved outright", "good");
        Tile(r.Count(AssuranceLevel.TrustsCompiledCode), "trust compiled code", "warn");
        Tile(r.Count(AssuranceLevel.RestsOnAxiom), "rest on project axioms", "warn");
        Tile(r.Count(AssuranceLevel.RestsOnSorry), "rest on sorry", "bad");
        Tile(r.Count(AssuranceLevel.Rejected), "rejected by Tenet", "bad");
        Tile(r.Marks.Count, "widen the trust surface", "warn");
        if (r.Definitions > 0)
        {
            Tile(r.UnstatedDefinitions.Count, $"of {r.Definitions:N0} definitions no theorem mentions", "warn");
        }
        sb.Append("</div>\n");

        sb.Append("<h2>What this means</h2><p>Every declaration was re-checked by Tenet, a second Lean kernel written independently of Lean's own. <em>Proved outright</em> means it needs nothing beyond Lean's three standard axioms (<code>propext</code>, <code>Classical.choice</code>, <code>Quot.sound</code>). Everything else is listed below with what it rests on. ");
        sb.Append(CultureInfo.InvariantCulture, $"This check fails on: {H(r.Policy.FailOn.Count == 0 ? "nothing" : string.Join(", ", AssurancePolicy.Categories.Where(r.Policy.FailOn.Contains)))}.</p>\n");

        sb.Append("<h2>Axioms the project introduces</h2>");
        if (r.Axioms.Count == 0)
        {
            sb.Append("<p class=\"none\">None.</p>\n");
        }
        else
        {
            sb.Append("<div class=\"scroll\"><table><tr><th>Axiom</th><th>Rested on by</th><th></th></tr>");
            foreach (AxiomUse a in r.Axioms)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<tr><td><code>{H(a.Name)}</code></td><td class=\"n\">{a.Declarations:N0}</td><td>{(a.Allowed ? "allowed" : "")}</td></tr>");
            }
            sb.Append("</table></div>\n");
        }

        sb.Append("<h2>Trust surface</h2>");
        if (r.Marks.Count == 0)
        {
            sb.Append("<p class=\"none\">None: no <code>@[implemented_by]</code>, <code>@[extern]</code>, <code>unsafe</code>, <code>partial</code> or <code>opaque</code> declarations.</p>\n");
        }
        else
        {
            sb.Append("<div class=\"scroll\"><table><tr><th>Declaration</th><th>Kind</th><th>Where</th><th>Meaning</th></tr>");
            foreach (TrustMark m in r.Marks.OrderBy(m => m.Kind))
            {
                sb.Append(CultureInfo.InvariantCulture, $"<tr><td><code>{H(m.Name)}</code></td><td class=\"nw\"><code>{H(Keyword(m.Kind))}</code></td><td class=\"nw\"><code>{H(Where(r, m.SourceFile, m.Line))}</code></td><td>{H(Meaning(m.Kind))}</td></tr>");
            }
            sb.Append("</table></div>\n");
        }

        if (r.Definitions > 0)
        {
            sb.Append("<h2>What the theorems are about</h2>");
            sb.Append(CultureInfo.InvariantCulture, $"<p>{r.Definitions - r.UnstatedDefinitions.Count:N0} of {r.Definitions:N0} definitions have a theorem whose statement mentions them.");
            if (r.UnstatedDefinitions.Count == 0)
            {
                sb.Append("</p>\n");
            }
            else
            {
                sb.Append(" No theorem mentions these, so nothing proved depends on them being right:</p>\n<div class=\"scroll\"><table><tr><th>Definition</th><th>Where</th></tr>");
                foreach (DeclarationRef d in r.UnstatedDefinitions)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<tr><td><code>{H(d.Name)}</code></td><td class=\"nw\"><code>{H(Where(r, d.SourceFile, d.Line))}</code></td></tr>");
                }
                sb.Append("</table></div>\n");
            }
        }

        sb.Append("<h2>Not proved outright</h2>");
        List<AssuredDeclaration> notProved = r.Declarations.Where(d => d.Level != AssuranceLevel.Proved).ToList();
        if (notProved.Count == 0)
        {
            sb.Append("<p class=\"none\">None.</p>\n");
        }
        else
        {
            sb.Append("<div class=\"scroll\"><table><tr><th>Declaration</th><th>Status</th><th>Rests on</th><th>Where</th></tr>");
            foreach (AssuredDeclaration d in notProved)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<tr><td><code>{H(d.Name)}</code></td><td>{H(Describe(d.Level))}</td><td>{H(RestsOn(d).Replace("`", "", StringComparison.Ordinal).Replace("\\|", "|", StringComparison.Ordinal))}</td><td class=\"nw\"><code>{H(Where(r, d.SourceFile, d.Line))}</code></td></tr>");
            }
            sb.Append("</table></div>\n");
        }
        sb.Append(CultureInfo.InvariantCulture,
            $"<footer>{Plural(r.Declarations.Count, "declaration")} in {Plural(r.ModulesChecked, "module")}, checked in {r.Elapsed.TotalSeconds:F1}s. Made by Lean Studio (<code>leanstudio --verify</code>).</footer>\n");
        sb.Append("</main></body></html>\n");
        return sb.ToString();
    }

    // ---- the command line ----

    /// <summary>What <c>leanstudio --verify --help</c> prints.</summary>
    public const string Usage = """
        leanstudio --verify [options]

        Builds the project, re-checks every declaration with Tenet (an independent Lean kernel) and reports what
        can be relied on: what is proved outright, what rests on sorry or on axioms the project adds, which proofs
        trust compiled code (native_decide), what Tenet rejects, and every @[implemented_by], @[extern], unsafe,
        partial and opaque declaration, and which definitions no theorem talks about. Prints the report as
        Markdown, and fails when the policy is broken.

          --project DIR        the project (default: the current folder)
          --no-build           do not run lake build first (the project must already be built)
          --no-cache           check every declaration again, even those that passed before unchanged
                               (by default only what changed, or rests on what changed, is re-checked)
          --fail-on LIST       what fails the check, comma-separated, from: rejected, sorry, axiom, native,
                               implemented_by, extern, unsafe, partial, opaque, unstated (a definition
                               no theorem's statement mentions); or all, or none (default: rejected,sorry)
          --fail-on-sorry      the same as adding sorry to --fail-on
          --allow-axiom LIST   axioms the project documents and accepts; they do not count as axiom
          --markdown FILE      also write the report as Markdown ($GITHUB_STEP_SUMMARY is used when set)
          --json FILE          write every fact as JSON
          --sarif FILE         write SARIF 2.1.0, for GitHub code scanning
          --html FILE          write a one-page HTML report (print it to PDF)
          --badge FILE         write a shields.io endpoint badge

        Exit code: 0 passed, 1 failed, 2 could not run.

        """;

    /// <summary>
    /// Run the check from the command line (<c>leanstudio --verify …</c>): parse the arguments, build, verify, write
    /// the report as Markdown to <paramref name="output"/> (and to any files asked for), progress to
    /// <paramref name="error"/>, and return the exit code: 0 passed, 1 failed, 2 could not run.
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
        AssurancePolicy policy;
        try
        {
            var failOn = new HashSet<string>(Value("--fail-on") is string f ? AssurancePolicy.ParseCategories(f) : AssurancePolicy.Default.FailOn, StringComparer.Ordinal);
            if (list.Contains("--fail-on-sorry"))
            {
                failOn.Add("sorry");
            }
            var allowed = new HashSet<string>(
                (Value("--allow-axiom") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);
            policy = new AssurancePolicy(failOn, allowed);
        }
        catch (FormatException e)
        {
            await error.WriteLineAsync(e.Message + "\n\n" + Usage).ConfigureAwait(false);
            return 2;
        }
        string dir = Path.GetFullPath(Value("--project") ?? Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(dir))
        {
            await error.WriteLineAsync($"No folder {dir}.").ConfigureAwait(false);
            return 2;
        }
        var project = new LeanProject(dir);
        if (!list.Contains("--no-build"))
        {
            await error.WriteLineAsync("Building (lake build)…").ConfigureAwait(false);
            var build = await Lake.BuildAsync(project, onLine: error.WriteLine, ct: ct).ConfigureAwait(false);
            if (!build.Success)
            {
                await error.WriteLineAsync($"lake build failed (exit {build.ExitCode}); nothing to verify.").ConfigureAwait(false);
                return 2;
            }
        }
        AssuranceReport report;
        try
        {
            await error.WriteLineAsync("Re-checking with Tenet…").ConfigureAwait(false);
            int lastModule = -1;
            var progress = new SyncProgress<VerificationProgress>(p =>
            {
                if (p.ModuleIndex != Interlocked.Exchange(ref lastModule, p.ModuleIndex))
                {
                    error.WriteLine($"  [{p.ModuleIndex}/{p.ModuleCount}] {p.Module}");
                }
            });
            report = await RunAsync(project, policy, progress, ct, useCache: !list.Contains("--no-cache")).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or Tenet.Kernel.KernelException or Tenet.Olean.OleanFormatException)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }
        string markdown = ToMarkdown(report);
        await output.WriteAsync(markdown).ConfigureAwait(false);
        foreach (string? file in new[] { Value("--markdown"), Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") }.Distinct())
        {
            if (!string.IsNullOrEmpty(file))
            {
                await File.AppendAllTextAsync(file, markdown + "\n", ct).ConfigureAwait(false);
            }
        }
        foreach ((string flag, Func<AssuranceReport, string> write) in new (string, Func<AssuranceReport, string>)[]
                 { ("--json", ToJson), ("--sarif", ToSarif), ("--html", ToHtml), ("--badge", ToBadge) })
        {
            if (Value(flag) is string path)
            {
                string full = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, write(report), ct).ConfigureAwait(false);
            }
        }
        return report.Passed ? 0 : 1;
    }

    /// <summary>An <see cref="IProgress{T}"/> that reports on the caller's thread, for a console with no synchronization context to post to.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
