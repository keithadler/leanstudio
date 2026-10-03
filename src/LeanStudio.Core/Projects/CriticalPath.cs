using System.Text;

namespace LeanStudio.Core.Projects;

/// <summary>The longest chain of modules that must be built one after the other, and what it says about the build.</summary>
/// <param name="Chain">The modules of the chain, from the first built to the last.</param>
/// <param name="Length">The chain's cost: the least a build can take, however many machines it has.</param>
/// <param name="TotalCost">The cost of every module together: what a single machine takes.</param>
public sealed record BuildPath(IReadOnlyList<string> Chain, double Length, double TotalCost)
{
    /// <summary>The most speed-up that more machines can give: total cost over the chain's.</summary>
    public double MaxParallelism => Length <= 0 ? 1 : TotalCost / Length;
}

/// <summary>
/// A build of thousands of modules is limited by its longest chain of imports, not by how many machines it has: modules
/// that import each other must be built in turn. Finds that chain, so the work of speeding a build up goes where it pays.
/// </summary>
public static class CriticalPath
{
    /// <summary>
    /// The critical path of the import graph <paramref name="imports"/> (each module and the modules it imports; imports of modules
    /// that are not keys are ignored), with <paramref name="cost"/> of a module as the unit: lines of code, or seconds profiled.
    /// A cycle (which Lean does not allow) is cut where it is found.
    /// </summary>
    public static BuildPath Find(IReadOnlyDictionary<string, IReadOnlyList<string>> imports, Func<string, double> cost)
    {
        var best = new Dictionary<string, (double Cost, string? Via)>(StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 in progress, 2 done
        foreach (string root in imports.Keys.Order(StringComparer.Ordinal))
        {
            if (state.ContainsKey(root))
            {
                continue;
            }
            // iterative post-order, since a project's import chain can be thousands deep
            var stack = new Stack<(string Module, int Next)>();
            stack.Push((root, 0));
            state[root] = 1;
            while (stack.Count > 0)
            {
                (string m, int next) = stack.Pop();
                IReadOnlyList<string> deps = imports.TryGetValue(m, out IReadOnlyList<string>? d) ? d : [];
                bool descended = false;
                for (int i = next; i < deps.Count; i++)
                {
                    string dep = deps[i];
                    if (!imports.ContainsKey(dep) || state.ContainsKey(dep))
                    {
                        continue;
                    }
                    stack.Push((m, i + 1));
                    stack.Push((dep, 0));
                    state[dep] = 1;
                    descended = true;
                    break;
                }
                if (descended)
                {
                    continue;
                }
                double via = 0;
                string? viaModule = null;
                foreach (string dep in deps.Where(imports.ContainsKey))
                {
                    if (best.TryGetValue(dep, out var b) && (b.Cost > via || (b.Cost == via && viaModule is not null && string.CompareOrdinal(dep, viaModule) < 0)))
                    {
                        via = b.Cost;
                        viaModule = dep;
                    }
                }
                best[m] = (via + cost(m), viaModule);
                state[m] = 2;
            }
        }
        if (best.Count == 0)
        {
            return new BuildPath([], 0, 0);
        }
        string end = best.OrderByDescending(kv => kv.Value.Cost).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
        var chain = new List<string>();
        for (string? m = end; m is not null; m = best[m].Via)
        {
            chain.Add(m);
        }
        chain.Reverse();
        return new BuildPath(chain, best[end].Cost, imports.Keys.Sum(cost));
    }

    /// <summary>A short account of <paramref name="path"/>: the floor on the build, and the modules on the chain that cost most.</summary>
    /// <param name="path">The critical path.</param>
    /// <param name="cost">The cost of a module (as given to <see cref="Find"/>).</param>
    /// <param name="unit">What the cost counts, such as <c>lines</c> or <c>seconds</c>.</param>
    public static string ToText(BuildPath path, Func<string, double> cost, string unit)
    {
        if (path.Chain.Count == 0)
        {
            return "No modules found.";
        }
        string N(double v) => v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"The build cannot be shorter than its longest chain: {path.Chain.Count} modules in a row, {N(path.Length)} {unit}, out of {N(path.TotalCost)} {unit} in all, so more machines can speed it up {path.MaxParallelism.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} times at most.\n");
        sb.Append("The chain: ").Append(string.Join(" → ", path.Chain.Count <= 8 ? path.Chain : [.. path.Chain.Take(4), "…", .. path.Chain.TakeLast(3)])).Append('\n');
        sb.Append("Speeding these up shortens the build the most:\n");
        foreach (string m in path.Chain.OrderByDescending(cost).ThenBy(m => m, StringComparer.Ordinal).Take(5))
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"  {m}: {N(cost(m))} {unit} ({(100 * cost(m) / path.Length).ToString("0", System.Globalization.CultureInfo.InvariantCulture)}% of the chain)\n");
        }
        return sb.ToString().TrimEnd();
    }
}
