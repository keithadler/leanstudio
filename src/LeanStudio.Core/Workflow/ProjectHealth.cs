using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LeanStudio.Core.Editing;

namespace LeanStudio.Core.Workflow;

/// <summary>What one Lean file holds, counted from its text.</summary>
/// <param name="Path">The file.</param>
/// <param name="Lines">Lines that are not blank.</param>
/// <param name="Theorems">Theorems and lemmas at the margin.</param>
/// <param name="Definitions">Definitions, abbreviations, structures, classes and inductive types at the margin.</param>
/// <param name="Sorries"><c>sorry</c> and <c>admit</c> outside comments.</param>
/// <param name="Todos">TODO, FIXME and XXX in comments.</param>
/// <param name="Undocumented">Public definitions without a doc comment (<see cref="DocCoverage"/>).</param>
/// <param name="Deprecated">Declarations marked deprecated with a date.</param>
/// <param name="StaleDeprecated">Of those, the ones old enough to delete (<see cref="StaleDeprecations"/>).</param>
/// <param name="StyleProblems">Problems of the text rules (<see cref="StyleCheck"/>).</param>
public sealed record FileHealth(string Path, int Lines, int Theorems, int Definitions, int Sorries, int Todos, int Undocumented, int Deprecated, int StaleDeprecated, int StyleProblems);

/// <summary>A project's state at a glance: its size, what is unfinished, and how well it keeps to Mathlib's conventions.</summary>
/// <param name="Files">Each Lean file's counts, in path order.</param>
public sealed record ProjectHealth(IReadOnlyList<FileHealth> Files)
{
    /// <summary>The sum of a count over every file.</summary>
    public int Total(Func<FileHealth, int> count) => Files.Sum(count);

    /// <summary>The share of public definitions with a doc comment, 0 to 100; 100 for a project with none.</summary>
    public int DocCoveragePercent
    {
        get
        {
            int defs = Total(f => f.Definitions);
            return defs == 0 ? 100 : (int)Math.Round(100.0 * (defs - Math.Min(defs, Total(f => f.Undocumented))) / defs);
        }
    }
}

/// <summary>Counting a project's Lean files into a <see cref="ProjectHealth"/>, and reading the result.</summary>
public static class ProjectHealthReport
{
    private static readonly Regex Theorem = new(@"^(?:@\[[^\]]*\]\s*)*(?:(?:protected|private|nonrec)\s+)*(?:theorem|lemma)\s", RegexOptions.Compiled);
    private static readonly Regex Definition = new(@"^(?:@\[[^\]]*\]\s*)*(?:(?:protected|private|public|noncomputable|partial|unsafe|nonrec)\s+)*(?:def|abbrev|structure|class|inductive|opaque)\s", RegexOptions.Compiled);

    /// <summary>What <paramref name="text"/> holds, as of <paramref name="today"/> (for the age of deprecations).</summary>
    public static FileHealth Count(string path, string text, DateOnly today)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        bool[] code = LeanText.CodeMask(string.Join('\n', lines));
        int theorems = 0, definitions = 0, offset = 0;
        for (int i = 0; i < lines.Length; offset += lines[i].Length + 1, i++)
        {
            if (lines[i].Length == 0 || char.IsWhiteSpace(lines[i][0]) || (offset < code.Length && !code[offset]))
            {
                continue;
            }
            theorems += Theorem.IsMatch(lines[i]) ? 1 : 0;
            definitions += Definition.IsMatch(lines[i]) ? 1 : 0;
        }
        List<Marker> markers = [.. Markers.ScanText(path, lines)];
        return new FileHealth(path, lines.Count(l => l.Trim().Length > 0), theorems, definitions,
            markers.Count(m => m.Kind != MarkerKind.Todo), markers.Count(m => m.Kind == MarkerKind.Todo),
            DocCoverage.Find(text).Count, StaleDeprecations.Find(text, today, 0).Count, StaleDeprecations.Find(text, today).Count,
            StyleCheck.Find(text).Count);
    }

    /// <summary>The health of every Lean file under <paramref name="root"/>.</summary>
    public static ProjectHealth Scan(string root, DateOnly today, CancellationToken ct = default)
    {
        var files = new List<FileHealth>();
        foreach (string file in ProjectSearch.Files(root, leanOnly: true).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                files.Add(Count(file, File.ReadAllText(file), today));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a file that can't be read has nothing to count
            }
        }
        return new ProjectHealth(files);
    }

    /// <summary>A Markdown summary of <paramref name="health"/>: totals, then the files with the most still to do.</summary>
    public static string ToMarkdown(ProjectHealth health, string root)
    {
        static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("## Project health\n\n");
        if (health.Files.Count == 0)
        {
            return sb.Append("No Lean files found.\n").ToString();
        }
        sb.Append(CultureInfo.InvariantCulture, $"{N(health.Files.Count)} files, {N(health.Total(f => f.Lines))} lines, {N(health.Total(f => f.Theorems))} theorems, {N(health.Total(f => f.Definitions))} definitions.\n\n");
        sb.Append("| | |\n|---|---:|\n");
        sb.Append(CultureInfo.InvariantCulture, $"| `sorry` / `admit` | {N(health.Total(f => f.Sorries))} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| TODO / FIXME | {N(health.Total(f => f.Todos))} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| Definitions with a doc comment | {health.DocCoveragePercent}% ({N(health.Total(f => f.Undocumented))} without) |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| Deprecated declarations | {N(health.Total(f => f.Deprecated))} ({N(health.Total(f => f.StaleDeprecated))} old enough to delete) |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| Style problems (whitespace, long lines, final newline) | {N(health.Total(f => f.StyleProblems))} |\n");
        var worst = health.Files.Select(f => (File: f, Open: f.Sorries + f.Undocumented + f.StaleDeprecated + f.StyleProblems))
            .Where(x => x.Open > 0).OrderByDescending(x => x.Open).ThenBy(x => x.File.Path, StringComparer.Ordinal).Take(5).ToList();
        if (worst.Count > 0)
        {
            sb.Append("\nMost still to do (sorries, undocumented definitions, old deprecations and style problems together):\n\n");
            foreach ((FileHealth f, int open) in worst)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- `{System.IO.Path.GetRelativePath(root, f.Path)}`: {open} ({f.Sorries} sorry, {f.Undocumented} undocumented, {f.StaleDeprecated} old deprecations, {f.StyleProblems} style)\n");
            }
        }
        return sb.ToString();
    }
}
