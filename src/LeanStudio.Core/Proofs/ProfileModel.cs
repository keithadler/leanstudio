using System.Globalization;

namespace LeanStudio.Core.Proofs;

/// <summary>What a profile measures: wall-clock time, or Lean's heartbeats (its deterministic count of work).</summary>
public enum ProfileUnit
{
    /// <summary>Seconds of wall-clock time, as Lean's profiler measures them. Varies a little from run to run.</summary>
    Seconds,

    /// <summary>
    /// Heartbeats, in thousands: the unit of <c>maxHeartbeats</c> (whose default limit is 200000). The same on every
    /// run and every machine, so the right unit for comparing a change or for staying under the limit.
    /// </summary>
    Heartbeats,
}

/// <summary>How to run a profile.</summary>
/// <param name="Unit">Time or heartbeats.</param>
/// <param name="Runs">
/// How many times to run Lean (1 to 9). Each declaration's figure is then the median, with the smallest and largest
/// kept as its spread. Only useful for time: heartbeats are the same on every run.
/// </param>
/// <param name="Counters">
/// Also run Lean once with <c>diagnostics</c> on, for what each declaration made Lean do: the simp lemmas it
/// tried, the instances it used, the definitions it unfolded. A separate run, so the counting does not skew the times.
/// </param>
/// <param name="Line">
/// Profile only the declaration at this 0-based line: the file is cut after it, so nothing below it is checked,
/// and only it is reported. Null profiles the whole file.
/// </param>
public sealed record ProfileOptions(ProfileUnit Unit = ProfileUnit.Seconds, int Runs = 1, bool Counters = false, int? Line = null);

/// <summary>
/// One step in Lean's trace profile: what Lean was doing, for how long, and the steps inside it. The tree is
/// Lean's own <c>trace.profiler</c> output, each entry the time (or heartbeats) it took including its children.
/// </summary>
/// <param name="Category">Lean's trace class, such as <c>Elab.step</c>, <c>Meta.synthInstance</c> or <c>Kernel</c>.</param>
/// <param name="Text">What Lean says the step is: a tactic, a term and its expected type, an instance problem.</param>
/// <param name="Value">Its cost including everything inside it, in the profile's unit.</param>
/// <param name="Failed">Lean marked it as failed (❌) or as throwing (💥): work that was tried and thrown away.</param>
/// <param name="Children">The steps inside it, in the order Lean ran them.</param>
public sealed record ProfileNode(string Category, string Text, double Value, bool Failed, IReadOnlyList<ProfileNode> Children)
{
    /// <summary>The cost of the step itself: its value less its children's (never below zero).</summary>
    public double Self => Math.Max(0, Value - Children.Sum(c => c.Value));

    /// <summary>The first line of <see cref="Text"/>, cut to 100 characters, with <c>…</c> when there is more.</summary>
    public string Label
    {
        get
        {
            string[] lines = Text.Split('\n');
            string first = lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            bool more = lines.Count(l => l.Trim().Length > 0) > 1;
            if (first.Length > 100)
            {
                return first[..100] + "…";
            }
            return more ? first + " …" : first;
        }
    }

    /// <summary>A short, friendly name for the trace class: <c>tactic</c>, <c>instance</c>, <c>kernel</c> and so on.</summary>
    public string Kind => KindOf(Category);

    /// <summary>A short, friendly name for a trace class.</summary>
    public static string KindOf(string category) => category switch
    {
        "Elab.step" => "elaboration",
        "Elab.command" => "command",
        "Elab.async" => "proof",
        "Kernel" => "kernel",
        "addDecl" => "kernel",
        _ when category.StartsWith("Meta.synthInstance", StringComparison.Ordinal) => "instances",
        _ when category.StartsWith("Meta.Tactic.simp", StringComparison.Ordinal) => "simp",
        _ when category.StartsWith("Meta.isDefEq", StringComparison.Ordinal) => "unification",
        _ when category.StartsWith("Meta.whnf", StringComparison.Ordinal) => "reduction",
        _ when category.StartsWith("Compiler", StringComparison.Ordinal) => "compiler",
        _ when category.StartsWith("Elab.definition", StringComparison.Ordinal) || category.StartsWith("Elab.def", StringComparison.Ordinal) => "definition",
        _ when category.StartsWith("Elab", StringComparison.Ordinal) => "elaboration",
        _ when category.StartsWith("Meta", StringComparison.Ordinal) => "meta",
        _ => category,
    };

    /// <summary>Every step in the tree below and including this one, depth first.</summary>
    public IEnumerable<ProfileNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (ProfileNode c in Children)
        {
            foreach (ProfileNode d in c.DescendantsAndSelf())
            {
                yield return d;
            }
        }
    }
}

/// <summary>
/// One of Lean's own profiler lines (<c>set_option profiler true</c>) attributed to a declaration, summed by what it
/// names: <c>tactic execution of omega</c>, <c>typeclass inference of Decidable</c>, <c>type checking</c>.
/// </summary>
/// <param name="What">What Lean says took the time, with the <c>Lean.Parser.Tactic.</c> prefix of a tactic dropped.</param>
/// <param name="Seconds">The time, in seconds, summed over every line naming the same thing.</param>
/// <param name="Count">How many lines named it.</param>
public sealed record ProfileStep(string What, double Seconds, int Count)
{
    /// <summary>The category the step belongs to: the part before <c> of </c>, such as <c>tactic execution</c>.</summary>
    public string Category => What.IndexOf(" of ", StringComparison.Ordinal) is int i and > 0 ? What[..i] : What;
}

/// <summary>One of Lean's cumulative profiling categories for the whole run, such as <c>simp</c> or <c>type checking</c>.</summary>
/// <param name="Name">Lean's name for the category.</param>
/// <param name="Seconds">The time spent in it over the whole file, in seconds.</param>
public sealed record ProfileCategory(string Name, double Seconds);

/// <summary>
/// A count from Lean's <c>diagnostics</c> option: how many times one declaration made Lean use a simp lemma, an
/// instance or unfold a definition. Large counts are often why a proof is slow.
/// </summary>
/// <param name="Kind">What was counted, in plain words, such as <c>simp lemmas tried</c> or <c>instances used</c>.</param>
/// <param name="Name">The lemma, instance or definition.</param>
/// <param name="Count">How many times.</param>
/// <param name="Succeeded">For simp lemmas tried, how many of those tries rewrote something; otherwise null.</param>
public sealed record ProfileCounter(string Kind, string Name, long Count, long? Succeeded = null)
{
    /// <summary>The count, and for tried lemmas how many succeeded, for display.</summary>
    public string Detail => Succeeded is long s ? $"{Count:N0} ({s:N0} succeeded)" : Count.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>How long Lean spent on one declaration, where, and on what.</summary>
/// <param name="Line">The 0-based line of the top-level command the time belongs to.</param>
/// <param name="Declaration">That line's text, trimmed and cut to 70 characters.</param>
/// <param name="Value">Its total cost, in the profile's <see cref="Unit"/>: seconds, or thousands of heartbeats.</param>
/// <param name="HotSpot">The innermost step that took at least 40% of the cost, as Lean describes it, or null.</param>
/// <param name="HotSpotValue">The cost of <c>HotSpot</c> (0 when there is none).</param>
public sealed record DeclarationTiming(int Line, string Declaration, double Value, string? HotSpot, double HotSpotValue)
{
    /// <summary>What <see cref="Value"/> counts.</summary>
    public ProfileUnit Unit { get; init; } = ProfileUnit.Seconds;

    /// <summary>The declaration's name (<c>Nat.foo</c> for <c>theorem Nat.foo …</c>), or its first line when it has none.</summary>
    public string Name => Profiler.DeclarationName(Declaration) ?? Declaration;

    /// <summary>Lean's trace of the declaration: one tree per piece of work it reported (elaboration, the proof, the kernel).</summary>
    public IReadOnlyList<ProfileNode> Trace { get; init; } = [];

    /// <summary>Lean's own profiler lines for the declaration, costliest first. Always in seconds; empty in heartbeats.</summary>
    public IReadOnlyList<ProfileStep> Steps { get; init; } = [];

    /// <summary>What the declaration made Lean do, most first, when the profile counted it.</summary>
    public IReadOnlyList<ProfileCounter> Counters { get; init; } = [];

    /// <summary>
    /// The cost of each tactic line inside the declaration, by 0-based line: the tactic steps in the trace matched
    /// to the lines they were written on, each without the steps inside it that were matched to other lines.
    /// </summary>
    public IReadOnlyDictionary<int, double> LineCosts { get; init; } = new Dictionary<int, double>();

    /// <summary>With several runs, the smallest and largest values seen; null after a single run.</summary>
    public (double Min, double Max)? Spread { get; init; }

    /// <summary>
    /// The path down the trace where the cost is: from the costliest piece of work, into its costliest step, and so
    /// on while that step holds at least half of the step around it.
    /// </summary>
    public IReadOnlyList<ProfileNode> HotPath()
    {
        var path = new List<ProfileNode>();
        ProfileNode? n = Trace.MaxBy(r => r.Value);
        while (n is not null)
        {
            path.Add(n);
            ProfileNode? next = n.Children.MaxBy(c => c.Value);
            n = next is not null && next.Value >= n.Value * 0.5 ? next : null;
        }
        return path;
    }

    /// <summary>The total for display, in the profile's unit.</summary>
    public string Time => Format(Value, Unit);

    /// <summary>A time in seconds for display: <c>42 ms</c> under a second, <c>1.25 s</c> from one up.</summary>
    public static string Format(double s) => s < 1 ? $"{s * 1000:F0} ms" : $"{s:F2} s";

    /// <summary>A value in the given unit for display: a time, or heartbeats as <c>14,681 hb</c>.</summary>
    public static string Format(double value, ProfileUnit unit) =>
        unit == ProfileUnit.Heartbeats ? value.ToString("N0", CultureInfo.InvariantCulture) + " hb" : Format(value);

    /// <summary>
    /// 0 cool, 1 warm, 2 hot. For time: under 100 ms, under a second, or more. For heartbeats: under 2,000, under
    /// 20,000 (a tenth of the default <c>maxHeartbeats</c>), or more.
    /// </summary>
    public int Heat => HeatOf(Value, Unit);

    /// <summary>The heat of a value: 0 cool, 1 warm, 2 hot, with the thresholds <see cref="Heat"/> describes.</summary>
    public static int HeatOf(double value, ProfileUnit unit) => unit == ProfileUnit.Heartbeats
        ? value >= 20_000 ? 2 : value >= 2_000 ? 1 : 0
        : value >= 1 ? 2 : value >= 0.1 ? 1 : 0;

    /// <summary>The spread over several runs, as <c>± 12 ms</c>, or empty after one run.</summary>
    public string SpreadText => Spread is var (lo, hi) && Unit == ProfileUnit.Seconds ? $"± {Format((hi - lo) / 2)}" : "";

    /// <summary>The time and, when there is one, the slowest part and its time, for a tooltip.</summary>
    public string Detail => HotSpot is null ? Time : $"{Time}   slowest part: {HotSpot} ({Format(HotSpotValue, Unit)})";
}

/// <summary>Everything one profile of a file found.</summary>
/// <param name="Declarations">Each declaration that took measurable time, costliest first.</param>
/// <param name="Unit">What the values count.</param>
public sealed record ProfileReport(IReadOnlyList<DeclarationTiming> Declarations, ProfileUnit Unit)
{
    /// <summary>The file profiled.</summary>
    public string Path { get; init; } = "";

    /// <summary>How many times Lean ran for the figures (the median of those runs is shown).</summary>
    public int Runs { get; init; } = 1;

    /// <summary>Lean's cumulative time per category over the whole file, largest first (empty in heartbeats).</summary>
    public IReadOnlyList<ProfileCategory> Categories { get; init; } = [];

    /// <summary>How long loading the file's imports took, in seconds (not counted in any declaration).</summary>
    public double ImportSeconds { get; init; }

    /// <summary>How long Lean ran in all, from start to exit, in seconds (the median run's).</summary>
    public double WallSeconds { get; init; }

    /// <summary>How many errors Lean reported. Declarations after an error may be cheaper than they would be.</summary>
    public int Errors { get; init; }

    /// <summary>
    /// From the running server as the file is edited (<see cref="LiveProfiler"/>), rather than a separate run of
    /// Lean: the figures are from the check the editor just did.
    /// </summary>
    public bool Live { get; init; }

    /// <summary>Only the declaration at this 0-based line was profiled; null for the whole file.</summary>
    public int? OnlyLine { get; init; }

    /// <summary>The sum over every declaration.</summary>
    public double Total => Declarations.Sum(d => d.Value);

    /// <summary>
    /// Every step of every declaration's trace summed by what it is (its trace class and text), by the cost of the
    /// step itself: where the time goes across the file, whichever declaration spent it. Costliest first.
    /// </summary>
    public IReadOnlyList<(string Category, string Label, double Self, int Count)> HotSteps(int max = 40) =>
        Declarations.SelectMany(d => d.Trace).SelectMany(n => n.DescendantsAndSelf())
            .Where(n => n.Self > 0)
            .GroupBy(n => (n.Category, n.Label))
            .Select(g => (g.Key.Category, g.Key.Label, g.Sum(n => n.Self), g.Count()))
            .OrderByDescending(x => x.Item3)
            .Take(max)
            .ToList();

    /// <summary>Every declaration's counters summed by kind and name, most first.</summary>
    public IReadOnlyList<ProfileCounter> Counters(int max = 60) =>
        Declarations.SelectMany(d => d.Counters)
            .GroupBy(c => (c.Kind, c.Name))
            .Select(g => new ProfileCounter(g.Key.Kind, g.Key.Name, g.Sum(c => c.Count), g.Any(c => c.Succeeded is not null) ? g.Sum(c => c.Succeeded ?? 0) : null))
            .OrderByDescending(c => c.Count)
            .Take(max)
            .ToList();
}

/// <summary>How one declaration's cost changed between two profiles.</summary>
/// <param name="Name">The declaration's name, which matches it between the profiles.</param>
/// <param name="Before">Its cost in the baseline, or null if it was not there (or too cheap to be reported).</param>
/// <param name="After">Its cost now, or null if it is gone (or now too cheap to be reported).</param>
public sealed record TimingChange(string Name, double? Before, double? After)
{
    /// <summary>After less before, counting a missing side as zero.</summary>
    public double Delta => (After ?? 0) - (Before ?? 0);

    /// <summary>The change as a fraction of the baseline (0.5 is 50% slower), or null when there was no baseline.</summary>
    public double? Ratio => Before is double b && b > 0 ? Delta / b : null;

    /// <summary>The change for display: <c>+120 ms (+35%)</c>, <c>−2,100 hb (−12%)</c>, <c>new</c> or <c>gone</c>.</summary>
    public string Describe(ProfileUnit unit)
    {
        if (Before is null)
        {
            return "new";
        }
        if (After is null)
        {
            return "gone";
        }
        string sign = Delta >= 0 ? "+" : "−";
        string pct = Ratio is double r ? $" ({sign}{Math.Abs(r) * 100:F0}%)" : "";
        return sign + DeclarationTiming.Format(Math.Abs(Delta), unit) + pct;
    }
}
