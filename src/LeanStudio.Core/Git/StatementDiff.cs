using System.Globalization;
using System.Text;
using LeanStudio.Core.Workflow;

namespace LeanStudio.Core.Git;

/// <summary>A theorem whose statement is not the one it had.</summary>
/// <param name="Name">The theorem's name.</param>
/// <param name="Before">Its statement before.</param>
/// <param name="After">Its statement after.</param>
public sealed record ChangedStatement(string Name, string Before, string After);

/// <summary>What a change did to the theorems a project states, leaving out how they are proved.</summary>
/// <param name="Added">Theorems that were not there before.</param>
/// <param name="Removed">Theorems that are gone.</param>
/// <param name="Changed">Theorems of the same name whose statement differs by more than white space and the names of bound variables.</param>
public sealed record StatementChanges(IReadOnlyList<TheoremStatement> Added, IReadOnlyList<TheoremStatement> Removed, IReadOnlyList<ChangedStatement> Changed)
{
    /// <summary>Nothing was added, removed or restated.</summary>
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}

/// <summary>
/// What a change means mathematically: the theorems it adds, removes and restates, found by comparing statements
/// (not proofs) before and after. A reviewer of a pull request reads these first. Theorems are matched by name, so
/// moving one to another file is no change, and renaming one is a removal and an addition.
/// </summary>
public static class StatementDiff
{
    /// <summary>Compare the statements <paramref name="before"/> and <paramref name="after"/>, each sorted by name.</summary>
    public static StatementChanges Compare(IEnumerable<TheoremStatement> before, IEnumerable<TheoremStatement> after)
    {
        Dictionary<string, TheoremStatement> was = Index(before), now = Index(after);
        return new StatementChanges(
            [.. now.Values.Where(t => !was.ContainsKey(t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal)],
            [.. was.Values.Where(t => !now.ContainsKey(t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal)],
            [.. now.Values.Where(t => was.TryGetValue(t.Name, out TheoremStatement? old)
                    && DuplicateStatements.Normalize(old.Statement) != DuplicateStatements.Normalize(t.Statement))
                .OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new ChangedStatement(t.Name, was[t.Name].Statement, t.Statement))]);
    }

    private static Dictionary<string, TheoremStatement> Index(IEnumerable<TheoremStatement> statements)
    {
        var index = new Dictionary<string, TheoremStatement>(StringComparer.Ordinal);
        foreach (TheoremStatement t in statements)
        {
            index.TryAdd(t.Name, t);
        }
        return index;
    }

    /// <summary>
    /// The statement changes of the Lean files that differ between <paramref name="baseRef"/> and <paramref name="headRef"/>
    /// in <paramref name="repo"/> (<c>git diff</c> over them, the way a pull request compares), or null when git cannot compare them.
    /// </summary>
    public static async Task<StatementChanges?> ReadAsync(GitRepository repo, string baseRef, string headRef, CancellationToken ct = default)
    {
        Processes.ProcessResult names = await repo.RunAsync(["diff", "--name-only", "--no-renames", baseRef, headRef, "--", "*.lean"], ct: ct).ConfigureAwait(false);
        if (!names.Success)
        {
            return null;
        }
        var before = new List<TheoremStatement>();
        var after = new List<TheoremStatement>();
        foreach (string file in names.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            ct.ThrowIfCancellationRequested();
            Processes.ProcessResult was = await repo.RunAsync(["show", $"{baseRef}:{file}"], ct: ct).ConfigureAwait(false);
            Processes.ProcessResult now = await repo.RunAsync(["show", $"{headRef}:{file}"], ct: ct).ConfigureAwait(false);
            if (was.Success)
            {
                before.AddRange(DuplicateStatements.Statements(file, was.Output));
            }
            if (now.Success)
            {
                after.AddRange(DuplicateStatements.Statements(file, now.Output));
            }
        }
        return Compare(before, after);
    }

    /// <summary>
    /// Where the current branch left the project's main line: the merge base of <c>HEAD</c> with the first of
    /// <c>origin/main</c>, <c>main</c>, <c>origin/master</c> and <c>master</c> that exists, or null when there is none (or
    /// the branch is that line, and so has nothing of its own).
    /// </summary>
    public static async Task<string?> DefaultBaseAsync(GitRepository repo, CancellationToken ct = default)
    {
        foreach (string candidate in new[] { "origin/main", "main", "origin/master", "master" })
        {
            Processes.ProcessResult exists = await repo.RunAsync(["rev-parse", "--verify", "--quiet", candidate + "^{commit}"], ct: ct).ConfigureAwait(false);
            if (!exists.Success)
            {
                continue;
            }
            Processes.ProcessResult mergeBase = await repo.RunAsync(["merge-base", candidate, "HEAD"], ct: ct).ConfigureAwait(false);
            Processes.ProcessResult head = await repo.RunAsync(["rev-parse", "HEAD"], ct: ct).ConfigureAwait(false);
            string hash = mergeBase.Output.Trim();
            return mergeBase.Success && hash.Length > 0 && hash != head.Output.Trim() ? hash : null;
        }
        return null;
    }

    /// <summary>The changes as Markdown, to paste into a pull request: what is new, what is gone and what is restated.</summary>
    public static string ToMarkdown(StatementChanges changes, string baseRef, string headRef)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"## Statements changed between `{baseRef}` and `{headRef}`\n\n");
        if (changes.IsEmpty)
        {
            return sb.Append("No theorem was added, removed or restated. Only proofs and other code changed.\n").ToString();
        }
        if (changes.Added.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"**Added ({changes.Added.Count})**\n\n");
            foreach (TheoremStatement t in changes.Added)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- `{t.Name}` {t.Statement}\n");
            }
            sb.Append('\n');
        }
        if (changes.Changed.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"**Restated ({changes.Changed.Count})**\n\n");
            foreach (ChangedStatement c in changes.Changed)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- `{c.Name}`\n  - before: {c.Before}\n  - after: {c.After}\n");
            }
            sb.Append('\n');
        }
        if (changes.Removed.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"**Removed ({changes.Removed.Count})**\n\n");
            foreach (TheoremStatement t in changes.Removed)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- `{t.Name}` {t.Statement}\n");
            }
        }
        return sb.ToString().TrimEnd() + "\n";
    }
}
