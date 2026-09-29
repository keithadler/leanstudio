using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Workflow;

namespace LeanStudio.Core.Proofs;

/// <summary>
/// Lean's own profilers, run over a copy of a file and read back into a <see cref="ProfileReport"/>: the cost of
/// each declaration and the tree of steps inside it (<c>trace.profiler</c>), Lean's cumulative time per category and
/// per tactic (<c>profiler</c>), and what each declaration made Lean do (<c>diagnostics</c>). The same numbers
/// Mathlib's maintainers look at when a file is slow, without editing the file or reading raw trace output.
/// </summary>
public static partial class Profiler
{
    /// <summary>Steps faster than this (in seconds) are not reported by Lean at all.</summary>
    public const double Threshold = 0.005;

    /// <summary>
    /// In heartbeats, steps cheaper than this many raw heartbeats (20 in <c>maxHeartbeats</c> units) are not reported.
    /// Much lower and the tracing itself makes Lean crawl.
    /// </summary>
    public const int HeartbeatThreshold = 20_000;

    /// <summary>Counts below this are left out of the counters (Lean's <c>diagnostics.threshold</c>).</summary>
    public const int CounterThreshold = 5;

    [GeneratedRegex(@"^(?<indent>\s*)\[(?<cls>[\w.]+)\] \[(?<s>[0-9.]+(?:[eE][-+]?[0-9]+)?)\]\s*(?<mark>✅️|❌️|💥️|✅|❌|💥)?\s?(?<what>.*)$")]
    private static partial Regex TraceLine();

    [GeneratedRegex(@"^(?<what>\S.*?) took (?<n>[0-9.]+(?:[eE][-+]?[0-9]+)?)(?<u>ms|s|us|µs|μs|ns)\s*$")]
    private static partial Regex ProfilerLine();

    [GeneratedRegex(@"^\t(?<name>.+?) (?<n>[0-9.]+(?:[eE][-+]?[0-9]+)?)(?<u>ms|s|us|µs|μs|ns)\s*$")]
    private static partial Regex CategoryLine();

    [GeneratedRegex(@"^\s*\[(?<cls>[\w.]+)\] (?<what>.+?) \(max: \d+, num: \d+\):\s*$")]
    private static partial Regex CounterHeader();

    [GeneratedRegex(@"^\s*\[(?<cls>[\w.]+)\] (?<name>.+?) ↦ (?<n>\d+)(?:, succeeded: (?<ok>\d+))?\s*$")]
    private static partial Regex CounterLine();

    [GeneratedRegex(@"^(?:@\[[^\]]*\]\s*)*(?:(?:private|protected|noncomputable|partial|unsafe|nonrec|scoped|local)\s+)*(?:theorem|lemma|def|abbrev|instance|structure|inductive|class|opaque|axiom|irreducible_def)\s+(?:\([^)]*\)\s*)?(?<name>[^\s(:{\[⦃]+)")]
    private static partial Regex DeclarationHead();

    /// <summary>
    /// The name a declaration's first line gives it (<c>foo</c> for <c>@[simp] theorem foo (n : Nat) …</c>), or null
    /// for one without a name, such as <c>example</c> or an anonymous <c>instance</c>.
    /// </summary>
    public static string? DeclarationName(string firstLine)
    {
        Match m = DeclarationHead().Match(firstLine.Trim());
        return m.Success && m.Groups["name"].Value.TrimEnd('.') is string n && n.Length > 0 && n is not (":=" or "where") ? n : null;
    }

    // ---- reading Lean's output ----

    /// <summary>
    /// Read Lean's <c>--json</c> output with the profilers on into a report. <paramref name="lines"/> is the file's
    /// text, for naming each declaration and matching tactic steps to lines.
    /// </summary>
    public static ProfileReport Parse(string output, IReadOnlyList<string> lines, ProfileUnit unit = ProfileUnit.Seconds)
    {
        var traces = new List<(int Line, string Text)>();
        var steps = new List<(int Line, string Text)>();
        var diagnostics = new List<(int Line, string Text)>();
        var categories = new List<ProfileCategory>();
        double import = 0;
        int errors = 0;
        bool inCategories = false;
        foreach (string raw in output.Split('\n'))
        {
            string l = raw.TrimEnd('\r');
            if (!l.StartsWith('{'))
            {
                if (l.StartsWith("cumulative profiling times", StringComparison.Ordinal))
                {
                    inCategories = true;
                }
                else if (inCategories && CategoryLine().Match(l) is { Success: true } c)
                {
                    categories.Add(new ProfileCategory(c.Groups["name"].Value, Seconds(c.Groups["n"].Value, c.Groups["u"].Value)));
                }
                else if (ProfilerLine().Match(l) is { Success: true } p && p.Groups["what"].Value == "import")
                {
                    import = Seconds(p.Groups["n"].Value, p.Groups["u"].Value);
                    inCategories = false;
                }
                else
                {
                    inCategories = false;
                }
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(l);
                JsonElement r = doc.RootElement;
                if (r.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.String && sev.GetString() == "error")
                {
                    errors++;
                }
                if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String
                    || !r.TryGetProperty("pos", out var pos) || pos.ValueKind != JsonValueKind.Object || !pos.TryGetProperty("line", out var line))
                {
                    continue;
                }
                string text = data.GetString() ?? "";
                int at = line.GetInt32() - 1;
                if (IsDiagnostics(text))
                {
                    diagnostics.Add((at, text));
                }
                else if (TraceLine().IsMatch(text.Split('\n')[0]))
                {
                    traces.Add((at, text));
                }
                else
                {
                    steps.Add((at, text));
                }
            }
            catch (JsonException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
        IReadOnlyList<DeclarationTiming> declarations = Build(traces, steps, diagnostics, lines, unit);
        return new ProfileReport(declarations, unit)
        {
            Categories = categories.OrderByDescending(c => c.Seconds).ToList(),
            ImportSeconds = import,
            Errors = errors,
        };
    }

    /// <summary>Timings from trace messages, each at the 0-based line Lean reported it, costliest first.</summary>
    public static IReadOnlyList<DeclarationTiming> Parse(IEnumerable<(int Line, string Text)> messages, IReadOnlyList<string> lines, ProfileUnit unit = ProfileUnit.Seconds) =>
        Build(messages, [], [], lines, unit);

    /// <summary>The counters in Lean's <c>--json</c> output with <c>diagnostics</c> on, by the 0-based line of the declaration they belong to.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<ProfileCounter>> ParseCounters(string output, IReadOnlyList<string> lines)
    {
        var byOwner = new Dictionary<int, Dictionary<(string, string), (long N, long? Ok)>>();
        foreach (string l in output.Split('\n'))
        {
            if (!l.StartsWith('{'))
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(l);
                JsonElement r = doc.RootElement;
                if (r.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String && IsDiagnostics(data.GetString() ?? "")
                    && r.TryGetProperty("pos", out var pos) && pos.ValueKind == JsonValueKind.Object && pos.TryGetProperty("line", out var line))
                {
                    int owner = OwnerOf(lines, line.GetInt32() - 1);
                    if (!byOwner.TryGetValue(owner, out var counts))
                    {
                        byOwner[owner] = counts = [];
                    }
                    AddCounters(data.GetString()!, counts);
                }
            }
            catch (JsonException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
        return byOwner.ToDictionary(kv => kv.Key, kv => Counters(kv.Value));
    }

    private static bool IsDiagnostics(string text) => text.StartsWith('[') && text.Split('\n')[0].EndsWith("] Diagnostics", StringComparison.Ordinal);

    private static void AddCounters(string text, Dictionary<(string, string), (long N, long? Ok)> counts)
    {
        string? kind = null;
        foreach (string l in text.Split('\n'))
        {
            if (CounterHeader().Match(l) is { Success: true } h)
            {
                kind = CounterKind(h.Groups["cls"].Value, h.Groups["what"].Value);
            }
            else if (kind is not null && CounterLine().Match(l) is { Success: true } c)
            {
                long n = long.Parse(c.Groups["n"].Value, CultureInfo.InvariantCulture);
                long? ok = c.Groups["ok"].Success ? long.Parse(c.Groups["ok"].Value, CultureInfo.InvariantCulture) : null;
                var key = (kind, c.Groups["name"].Value);
                (long n0, long? ok0) = counts.GetValueOrDefault(key);
                counts[key] = (n0 + n, ok is null && ok0 is null ? null : (ok0 ?? 0) + (ok ?? 0));
            }
        }
    }

    private static IReadOnlyList<ProfileCounter> Counters(Dictionary<(string Kind, string Name), (long N, long? Ok)> counts) =>
        counts.Select(kv => new ProfileCounter(kv.Key.Kind, kv.Key.Name, kv.Value.N, kv.Value.Ok)).OrderByDescending(c => c.Count).ToList();

    /// <summary>Lean's heading for a group of counters, in plain words.</summary>
    private static string CounterKind(string cls, string what) => (cls, what) switch
    {
        ("simp", "used theorems") => "simp lemmas used",
        ("simp", "tried theorems") => "simp lemmas tried",
        ("reduction", "unfolded declarations") => "definitions unfolded",
        ("reduction", "unfolded instances") => "instances unfolded",
        ("reduction", "unfolded reducible declarations") => "reducible definitions unfolded",
        ("type_class", "used instances") => "instances used",
        ("kernel", "unfolded declarations") => "unfolded by the kernel",
        ("def_eq", _) => "unification heuristics",
        _ => $"{cls}: {what}",
    };

    private static IReadOnlyList<DeclarationTiming> Build(
        IEnumerable<(int Line, string Text)> traces,
        IEnumerable<(int Line, string Text)> steps,
        IEnumerable<(int Line, string Text)> diagnostics,
        IReadOnlyList<string> lines,
        ProfileUnit unit)
    {
        var trees = new Dictionary<int, List<ProfileNode>>();
        var lineCosts = new Dictionary<int, Dictionary<int, double>>();
        var cursors = new Dictionary<int, int>();
        foreach ((int line, string text) in traces)
        {
            if (line < 0 || ParseTree(text, unit) is not ProfileNode root)
            {
                continue;
            }
            // Some work is reported where it happened (the kernel checking a proof, at its tactic): it belongs
            // to the declaration around it.
            int owner = OwnerOf(lines, line);
            if (!trees.TryGetValue(owner, out var list))
            {
                trees[owner] = list = [];
                lineCosts[owner] = [];
            }
            list.Add(root);
            int end = NextTopLevel(lines, owner);
            int? at = line != owner && line < lines.Count ? line : null;
            if (at is int a)
            {
                lineCosts[owner][a] = lineCosts[owner].GetValueOrDefault(a) + root.Value;
            }
            int cursor = cursors.GetValueOrDefault(owner, owner);
            Attribute(root, null, at, ref cursor, end, lines, lineCosts[owner]);
            cursors[owner] = cursor;
        }
        var stepsBy = new Dictionary<int, Dictionary<string, (double S, int N)>>();
        foreach ((int line, string text) in steps)
        {
            if (line < 0)
            {
                continue;
            }
            int owner = OwnerOf(lines, line);
            foreach (string l in text.Split('\n'))
            {
                if (ProfilerLine().Match(l.TrimEnd('\r')) is { Success: true } m)
                {
                    if (!stepsBy.TryGetValue(owner, out var d))
                    {
                        stepsBy[owner] = d = [];
                    }
                    string what = m.Groups["what"].Value.Replace("Lean.Parser.Tactic.", "", StringComparison.Ordinal);
                    (double s, int n) = d.GetValueOrDefault(what);
                    d[what] = (s + Seconds(m.Groups["n"].Value, m.Groups["u"].Value), n + 1);
                }
            }
        }
        var countersBy = new Dictionary<int, Dictionary<(string, string), (long, long?)>>();
        foreach ((int line, string text) in diagnostics)
        {
            int owner = OwnerOf(lines, Math.Max(0, line));
            if (!countersBy.TryGetValue(owner, out var c))
            {
                countersBy[owner] = c = [];
            }
            AddCounters(text, c);
        }
        var result = new List<DeclarationTiming>();
        foreach ((int owner, List<ProfileNode> roots) in trees)
        {
            double total = roots.Sum(r => r.Value);
            (string? hot, double hotValue) = HotSpot(roots, total);
            result.Add(new DeclarationTiming(owner, NameAt(lines, owner), total, hot, hotValue)
            {
                Unit = unit,
                Trace = roots,
                Steps = stepsBy.TryGetValue(owner, out var s)
                    ? s.Select(kv => new ProfileStep(kv.Key, kv.Value.S, kv.Value.N)).OrderByDescending(x => x.Seconds).ToList()
                    : [],
                Counters = countersBy.TryGetValue(owner, out var c) ? Counters(c) : [],
                LineCosts = lineCosts[owner].Where(kv => kv.Key != owner && kv.Value > 1e-9).ToDictionary(kv => kv.Key, kv => kv.Value),
            });
        }
        return result.OrderByDescending(t => t.Value).ToList();
    }

    /// <summary>
    /// One trace message as a tree. Each entry is <c>[class] [cost] ✅️ what</c>, indented under the entry it is part
    /// of; lines that are not entries continue the text of the entry above them. Null if the message is not a trace.
    /// </summary>
    public static ProfileNode? ParseTree(string text, ProfileUnit unit = ProfileUnit.Seconds)
    {
        string[] ml = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var stack = new List<(int Indent, Builder B)>();
        Builder? root = null, last = null;
        foreach (string l in ml)
        {
            Match m = TraceLine().Match(l);
            if (!m.Success)
            {
                if (last is not null)
                {
                    // A continuation: strip the indentation the entry's text starts at.
                    int strip = Math.Min(l.Length - l.TrimStart().Length, last.Indent + 4);
                    last.Text.Append('\n').Append(l.AsSpan(strip));
                }
                continue;
            }
            double value = double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
            if (unit == ProfileUnit.Heartbeats)
            {
                value /= 1000;
            }
            int indent = m.Groups["indent"].Value.Length;
            string mark = m.Groups["mark"].Value;
            var b = new Builder(m.Groups["cls"].Value, value, mark.StartsWith("❌", StringComparison.Ordinal) || mark.StartsWith("💥", StringComparison.Ordinal), indent);
            b.Text.Append(m.Groups["what"].Value);
            while (stack.Count > 0 && stack[^1].Indent >= indent)
            {
                stack.RemoveAt(stack.Count - 1);
            }
            if (stack.Count > 0)
            {
                stack[^1].B.Children.Add(b);
            }
            else if (root is null)
            {
                root = b;
            }
            else
            {
                continue; // a second entry at the root's level: not something Lean writes
            }
            stack.Add((indent, b));
            last = b;
        }
        return root?.Build();
    }

    private sealed class Builder(string category, double value, bool failed, int indent)
    {
        public int Indent { get; } = indent;
        public StringBuilder Text { get; } = new();
        public List<Builder> Children { get; } = [];
        public ProfileNode Build() => new(category, Text.ToString().TrimEnd(), value, failed, Children.Select(c => c.Build()).ToList());
    }

    /// <summary>
    /// The innermost step that still accounts for most of the cost: the deepest one holding at least 40% of the
    /// whole, skipping the wrappers that only restate the declaration or an expected type.
    /// </summary>
    private static (string? What, double Value) HotSpot(IReadOnlyList<ProfileNode> roots, double total)
    {
        (string? What, double Value) best = (null, 0);
        int bestDepth = -1;
        void Visit(ProfileNode n, int depth)
        {
            if (depth > 0 && n.Value >= total * 0.4 && n.Label.Length > 0 && !n.Label.StartsWith("expected type", StringComparison.Ordinal) && depth > bestDepth)
            {
                string label = n.Label.EndsWith(" …", StringComparison.Ordinal) ? n.Label[..^2] : n.Label;
                best = (label.Length > 80 ? label[..80] + "…" : label, n.Value);
                bestDepth = depth;
            }
            foreach (ProfileNode c in n.Children)
            {
                Visit(c, depth + 1);
            }
        }
        foreach (ProfileNode r in roots)
        {
            Visit(r, 0);
        }
        return best;
    }

    /// <summary>
    /// Match the tactic steps under <paramref name="n"/> to the lines they were written on, adding each matched
    /// step's cost to its line and taking it off the line of the step it is inside. Lean runs a proof's tactics in
    /// the order they are written, so each is looked for from the line of the last one found.
    /// </summary>
    private static void Attribute(ProfileNode n, string? parent, int? current, ref int cursor, int end, IReadOnlyList<string> lines, Dictionary<int, double> costs)
    {
        int? mine = current;
        string text = Code(n.Text);
        if (n.Category == "Elab.step" && text.Length > 0 && !n.Text.Trim().Contains('\n') && text != parent && !text.StartsWith("expected type", StringComparison.Ordinal))
        {
            if (current is int c && Code(lines[c]).Contains(text, StringComparison.Ordinal))
            {
                mine = c; // part of the line its parent is on: `· simp` and its `simp`, `t1 <;> t2`
            }
            else
            {
                // The next line written with it; failing that, the line of the last one found (`simp; omega`).
                int from = Math.Max(cursor, 0);
                foreach (int i in Enumerable.Range(from + 1, Math.Max(0, end - from - 1)).Append(from))
                {
                    if (i < lines.Count && Code(lines[i]).StartsWith(text, StringComparison.Ordinal))
                    {
                        mine = i;
                        cursor = i;
                        break;
                    }
                }
            }
        }
        if (mine != current && mine is int m)
        {
            costs[m] = costs.GetValueOrDefault(m) + n.Value;
            if (current is int cur)
            {
                costs[cur] = costs.GetValueOrDefault(cur) - n.Value;
            }
        }
        foreach (ProfileNode child in n.Children)
        {
            Attribute(child, text, mine, ref cursor, end, lines, costs);
        }
    }

    /// <summary>A line's code for matching tactics: trimmed, without a leading bullet or a trailing comment.</summary>
    private static string Code(string line)
    {
        string s = line.Trim().TrimStart('·', '•', ' ', '\t');
        int comment = s.IndexOf("--", StringComparison.Ordinal);
        return (comment >= 0 ? s[..comment] : s).TrimEnd();
    }

    private static double Seconds(string n, string unit)
    {
        double v = double.Parse(n, CultureInfo.InvariantCulture);
        return unit switch
        {
            "s" => v,
            "ms" => v / 1e3,
            "ns" => v / 1e9,
            _ => v / 1e6,
        };
    }

    /// <summary>The line of the top-level command containing <paramref name="line"/>: the nearest one at column 0.</summary>
    public static int OwnerOf(IReadOnlyList<string> lines, int line)
    {
        for (int i = Math.Min(line, lines.Count - 1); i >= 0; i--)
        {
            if (IsTopLevel(lines[i]))
            {
                return i;
            }
        }
        return line;
    }

    /// <summary>The line after the command starting at <paramref name="owner"/>: the next top-level line, or the end.</summary>
    public static int NextTopLevel(IReadOnlyList<string> lines, int owner)
    {
        for (int i = owner + 1; i < lines.Count; i++)
        {
            if (IsTopLevel(lines[i]))
            {
                return i;
            }
        }
        return lines.Count;
    }

    private static bool IsTopLevel(string l) =>
        l.Length > 0 && !char.IsWhiteSpace(l[0]) && !l.StartsWith("--", StringComparison.Ordinal) && l.TrimEnd('\r').Length > 0;

    private static string NameAt(IReadOnlyList<string> lines, int line)
    {
        if (line < 0 || line >= lines.Count)
        {
            return "";
        }
        string s = lines[line].Trim();
        return s.Length > 70 ? s[..70] + "…" : s;
    }

    // ---- running Lean ----

    /// <summary>The arguments that turn Lean's profilers on, for one unit.</summary>
    private static string[] Flags(ProfileUnit unit) => unit == ProfileUnit.Heartbeats
        ? ["--json", "-Dtrace.profiler=true", "-Dtrace.profiler.useHeartbeats=true", "-Dtrace.profiler.threshold=" + HeartbeatThreshold.ToString(CultureInfo.InvariantCulture)]
        // `profiler` (the categories and per-tactic lines) is left off with heartbeats: its threshold would count
        // heartbeats too, and at a millisecond's worth Lean prints so much it barely moves.
        : ["--json", "-Dtrace.profiler=true", "-Dtrace.profiler.threshold=" + ((int)(Threshold * 1000)).ToString(CultureInfo.InvariantCulture),
           "-Dprofiler=true", "-Dprofiler.threshold=1"];

    /// <summary>
    /// The text Lean is given for a profile: the whole file, or with <paramref name="line"/> set, the file cut after
    /// the declaration at that line (so nothing below it is checked). Also returns that declaration's first line.
    /// </summary>
    public static (string Text, int? Owner) Scope(string text, int? line)
    {
        if (line is not int l)
        {
            return (text, null);
        }
        string[] lines = text.Split('\n');
        int owner = OwnerOf(lines, Math.Clamp(l, 0, Math.Max(0, lines.Length - 1)));
        return (string.Join('\n', lines.Take(NextTopLevel(lines, owner))), owner);
    }

    /// <summary>
    /// Profile a file's text with the <c>lean</c> command line (a copy in the project's mirror, so unsaved text is
    /// what is measured). Proofs are elaborated one after another there, so each time is that declaration's own.
    /// Writes the mirror copy and runs one <c>lean</c> process per run, and one more for the counters. Returns the
    /// report, or an empty report and an error message when Lean produced nothing and failed (typically because
    /// the file's imports are not built).
    /// </summary>
    /// <param name="project">The project the file is in; its dependencies are on Lean's path.</param>
    /// <param name="sourcePath">The file, for its place in the mirror (and so its module name).</param>
    /// <param name="text">The text to profile, saved or not.</param>
    /// <param name="options">What to measure, how many runs, counters, and one declaration or the whole file.</param>
    /// <param name="progress">Told what is running, in words, as each run starts.</param>
    /// <param name="ct">Cancelling kills the running Lean process.</param>
    public static async Task<(ProfileReport Report, string? Error)> RunAsync(
        LeanProject project, string sourcePath, string text, ProfileOptions? options = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        options ??= new ProfileOptions();
        string[] lines = text.Split('\n');
        (string scoped, int? owner) = Scope(text, options.Line);
        string file = await LeanCli.MirrorAsync(project, sourcePath, scoped, "profile", ct).ConfigureAwait(false);
        int runs = options.Unit == ProfileUnit.Heartbeats ? 1 : Math.Clamp(options.Runs, 1, 9);
        var reports = new List<ProfileReport>();
        for (int i = 0; i < runs; i++)
        {
            progress?.Report(runs == 1 ? "Lean is checking it with its profiler on" : $"run {i + 1} of {runs}: Lean is checking it with its profiler on");
            var clock = Stopwatch.StartNew();
            ProcessResult r = await LeanCli.RunAsync(project, [.. Flags(options.Unit), file], ct).ConfigureAwait(false);
            ProfileReport report = Parse(r.Output, lines, options.Unit) with { WallSeconds = clock.Elapsed.TotalSeconds };
            if (report.Declarations.Count == 0 && !r.Success)
            {
                string err = string.Join("\n", r.Output.Split('\n').Where(l => !l.StartsWith('{')).Take(20)).Trim();
                return (new ProfileReport([], options.Unit) { Path = sourcePath }, err.Length > 0 ? err : "Lean could not check this file (build its imports first).");
            }
            reports.Add(report);
        }
        ProfileReport merged = Merge(reports);
        if (options.Counters)
        {
            progress?.Report("counting what each declaration makes Lean do");
            ProcessResult r = await LeanCli.RunAsync(project, ["--json", "-Ddiagnostics=true", "-Ddiagnostics.threshold=" + CounterThreshold.ToString(CultureInfo.InvariantCulture), file], ct)
                .ConfigureAwait(false);
            IReadOnlyDictionary<int, IReadOnlyList<ProfileCounter>> counters = ParseCounters(r.Output, lines);
            merged = merged with
            {
                Declarations = merged.Declarations.Select(d => counters.TryGetValue(d.Line, out var c) ? d with { Counters = c } : d).ToList(),
            };
        }
        if (owner is int o)
        {
            IReadOnlyList<DeclarationTiming> only = merged.Declarations.Where(d => d.Line == o).ToList();
            merged = merged with
            {
                Declarations = only,
                // Lean's categories cover everything above the declaration too; its own lines say what it cost.
                Categories = only.SelectMany(d => d.Steps).GroupBy(s => s.Category)
                    .Select(g => new ProfileCategory(g.Key, g.Sum(s => s.Seconds))).OrderByDescending(c => c.Seconds).ToList(),
            };
        }
        return (merged with { Path = sourcePath, Runs = runs, OnlyLine = owner }, null);
    }

    /// <summary>
    /// Several runs' reports as one: each declaration's value the median of its values (with the smallest and
    /// largest as its spread), and everything else (the trees, the categories) from the run whose total was the median.
    /// </summary>
    public static ProfileReport Merge(IReadOnlyList<ProfileReport> reports)
    {
        if (reports.Count == 1)
        {
            return reports[0];
        }
        ProfileReport middle = reports.OrderBy(r => r.Total).ElementAt(reports.Count / 2);
        var byLine = reports.SelectMany(r => r.Declarations).GroupBy(d => d.Line).ToDictionary(g => g.Key, g => g.Select(d => d.Value).OrderBy(v => v).ToList());
        var declarations = middle.Declarations.Select(d =>
        {
            List<double> values = byLine[d.Line];
            // A run where it fell under Lean's threshold counts as nothing for it.
            while (values.Count < reports.Count)
            {
                values.Insert(0, 0);
            }
            double median = values[values.Count / 2];
            double scale = d.Value > 0 ? median / d.Value : 1;
            return d with { Value = median, HotSpotValue = d.HotSpotValue * scale, Spread = (values[0], values[^1]) };
        }).OrderByDescending(d => d.Value).ToList();
        return middle with { Declarations = declarations, WallSeconds = reports.Select(r => r.WallSeconds).OrderBy(s => s).ElementAt(reports.Count / 2) };
    }

    /// <summary>
    /// How each declaration's cost changed from <paramref name="before"/> to <paramref name="after"/>, matched by
    /// name (and by first line for unnamed ones), biggest change first.
    /// </summary>
    public static IReadOnlyList<TimingChange> Compare(ProfileReport before, ProfileReport after)
    {
        static Dictionary<string, double> ByName(ProfileReport r)
        {
            var d = new Dictionary<string, double>();
            foreach (DeclarationTiming t in r.Declarations)
            {
                d[t.Name] = d.GetValueOrDefault(t.Name) + t.Value;
            }
            return d;
        }
        Dictionary<string, double> b = ByName(before), a = ByName(after);
        return b.Keys.Union(a.Keys)
            .Select(k => new TimingChange(k, b.TryGetValue(k, out double x) ? x : null, a.TryGetValue(k, out double y) ? y : null))
            .OrderByDescending(c => Math.Abs(c.Delta))
            .ToList();
    }

    /// <summary>
    /// Run Lean's profiler over a file and write its trace in the Firefox Profiler's format (open it at
    /// profiler.firefox.com, or in any tool that reads that format) to <paramref name="outPath"/>.
    /// </summary>
    /// <returns>Null when the file was written, otherwise what went wrong.</returns>
    public static async Task<string?> ExportFirefoxAsync(LeanProject project, string sourcePath, string text, string outPath, ProfileUnit unit = ProfileUnit.Seconds, CancellationToken ct = default)
    {
        string file = await LeanCli.MirrorAsync(project, sourcePath, text, "profile", ct).ConfigureAwait(false);
        string target = Path.GetFullPath(outPath);
        File.Delete(target);
        string[] flags = unit == ProfileUnit.Heartbeats
            ? ["-Dtrace.profiler.useHeartbeats=true", "-Dtrace.profiler.threshold=" + HeartbeatThreshold.ToString(CultureInfo.InvariantCulture)]
            : ["-Dtrace.profiler.threshold=1"];
        ProcessResult r = await LeanCli.RunAsync(project, ["-Dtrace.profiler=true", .. flags, "-Dtrace.profiler.output=" + target, file], ct).ConfigureAwait(false);
        if (File.Exists(target))
        {
            return null;
        }
        string err = string.Join("\n", r.Output.Split('\n').Take(20)).Trim();
        return err.Length > 0 ? err : "Lean did not write a profile.";
    }

    /// <summary>
    /// Profile every Lean file of a project, one after another (never in parallel, which would skew the times).
    /// Files Lean cannot check (their imports not built, say) are reported with their error and skipped.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="files">The files, in the order to profile them.</param>
    /// <param name="options">What to measure; <see cref="ProfileOptions.Line"/> is ignored.</param>
    /// <param name="progress">Told each file as it starts: how many are done, how many in all, and its path.</param>
    /// <param name="ct">Cancelling stops after killing the Lean process that is running.</param>
    public static async Task<IReadOnlyList<(string Path, ProfileReport Report, string? Error)>> RunProjectAsync(
        LeanProject project, IReadOnlyList<string> files, ProfileOptions? options = null, IProgress<(int Done, int Total, string Path)>? progress = null, CancellationToken ct = default)
    {
        options = (options ?? new ProfileOptions()) with { Line = null };
        var results = new List<(string, ProfileReport, string?)>();
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((i, files.Count, files[i]));
            string text;
            try
            {
                text = await File.ReadAllTextAsync(files[i], ct).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                results.Add((files[i], new ProfileReport([], options.Unit) { Path = files[i] }, e.Message));
                continue;
            }
            (ProfileReport report, string? error) = await RunAsync(project, files[i], text.Replace("\r\n", "\n", StringComparison.Ordinal), options, null, ct).ConfigureAwait(false);
            results.Add((files[i], report, error));
        }
        return results;
    }

    /// <summary>
    /// A report as Markdown, for pasting into an issue or a chat: the total, the categories, and each declaration
    /// with its hot spot, costliest first (at most <paramref name="max"/>). With <paramref name="baseline"/>, the
    /// change from it too.
    /// </summary>
    public static string ToMarkdown(ProfileReport report, ProfileReport? baseline = null, int max = 25)
    {
        var sb = new StringBuilder();
        ProfileUnit u = report.Unit;
        string file = Path.GetFileName(report.Path);
        sb.Append(CultureInfo.InvariantCulture, $"### Lean profile of `{file}`\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"{DeclarationTiming.Format(report.Total, u)} across {report.Declarations.Count} declaration{(report.Declarations.Count == 1 ? "" : "s")}");
        if (u == ProfileUnit.Seconds && report.Runs > 1)
        {
            sb.Append(CultureInfo.InvariantCulture, $" (median of {report.Runs} runs)");
        }
        if (report.ImportSeconds > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"; imports {DeclarationTiming.Format(report.ImportSeconds)}");
        }
        sb.Append(".\n\n");
        Dictionary<string, TimingChange>? changes = baseline is null ? null : Compare(baseline, report).ToDictionary(c => c.Name);
        sb.Append(changes is null ? "| | Declaration | Line | Slowest part |\n|---:|---|---:|---|\n" : "| | Change | Declaration | Line | Slowest part |\n|---:|---:|---|---:|---|\n");
        foreach (DeclarationTiming t in report.Declarations.Take(max))
        {
            string hot = t.HotSpot is string h ? $"`{h.Replace("|", "\\|", StringComparison.Ordinal)}` ({DeclarationTiming.Format(t.HotSpotValue, u)})" : "";
            string change = changes is not null && changes.TryGetValue(t.Name, out TimingChange? c) ? c.Describe(u) + " | " : changes is null ? "" : " | ";
            sb.Append(CultureInfo.InvariantCulture, $"| {t.Time} | {change}`{t.Name.Replace("|", "\\|", StringComparison.Ordinal)}` | {t.Line + 1} | {hot} |\n");
        }
        if (report.Categories.Count > 0)
        {
            sb.Append("\nWhere Lean's time went: ");
            sb.Append(string.Join(", ", report.Categories.Where(c => c.Name is not ("import" or "initialization")).Take(8)
                .Select(c => $"{c.Name} {DeclarationTiming.Format(c.Seconds)}")));
            sb.Append(".\n");
        }
        return sb.ToString();
    }
}
