using System.Text.RegularExpressions;

namespace LeanStudio.Core.Editing;

/// <summary>One match found by <see cref="ProjectSearch.Search"/>.</summary>
/// <param name="Path">The file's full path.</param>
/// <param name="Line">The 0-based line of the match.</param>
/// <param name="Column">The 0-based column where the match starts.</param>
/// <param name="Length">The match's length in characters.</param>
/// <param name="LineText">The whole line the match is on.</param>
public sealed record SearchHit(string Path, int Line, int Column, int Length, string LineText);

/// <summary>Find in files: every match of a text or regular expression in a project's sources.</summary>
public static class ProjectSearch
{
    private static readonly HashSet<string> Skip = new(StringComparer.Ordinal) { ".git", ".lake", "build", "bin", "obj", "node_modules" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".lean", ".md", ".toml", ".json", ".txt", ".yml", ".yaml" };

    /// <summary>
    /// Search every file <see cref="Files"/> finds under <paramref name="root"/>, line by line. Runs synchronously and
    /// reads from disk, so call it off the UI thread. Files that cannot be read are skipped.
    /// </summary>
    /// <param name="root">The folder to search.</param>
    /// <param name="query">The text or pattern; an empty query finds nothing.</param>
    /// <param name="caseSensitive">Whether case must match.</param>
    /// <param name="regex">
    /// Whether <paramref name="query"/> is a .NET regular expression; an invalid one finds nothing, and each match is
    /// limited to one second.
    /// </param>
    /// <param name="limit">Stop once this many hits are found (checked after each line, so it can be slightly exceeded).</param>
    /// <param name="ct">Checked before each file.</param>
    public static IReadOnlyList<SearchHit> Search(string root, string query, bool caseSensitive = false, bool regex = false, int limit = 2000, CancellationToken ct = default)
    {
        var hits = new List<SearchHit>();
        if (query.Length == 0)
        {
            return hits;
        }
        Regex? re = null;
        if (regex)
        {
            try
            {
                re = new Regex(query, (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                return hits;
            }
        }
        StringComparison cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (string file in Files(root))
        {
            ct.ThrowIfCancellationRequested();
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (re is not null)
                {
                    foreach (Match m in re.Matches(line))
                    {
                        hits.Add(new SearchHit(file, i, m.Index, m.Length, line));
                    }
                }
                else
                {
                    for (int at = line.IndexOf(query, cmp); at >= 0; at = line.IndexOf(query, at + Math.Max(1, query.Length), cmp))
                    {
                        hits.Add(new SearchHit(file, i, at, query.Length, line));
                    }
                }
                if (hits.Count >= limit)
                {
                    return hits;
                }
            }
        }
        return hits;
    }

    /// <summary>
    /// Source files under a folder, skipping build output, dependencies and hidden folders: Lean, Markdown, TOML,
    /// JSON, YAML and text files and <c>lean-toolchain</c>, or only <c>.lean</c> files when <paramref name="leanOnly"/>.
    /// Enumerated lazily, depth first, in ordinal order; unreadable folders are skipped.
    /// </summary>
    public static IEnumerable<string> Files(string root, bool leanOnly = false)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (string f in files.OrderBy(f => f, StringComparer.Ordinal))
            {
                string ext = Path.GetExtension(f);
                if (leanOnly ? ext.Equals(".lean", StringComparison.OrdinalIgnoreCase) : Extensions.Contains(ext) || ext.Length == 0 && Path.GetFileName(f) == "lean-toolchain")
                {
                    yield return f;
                }
            }
            foreach (string d in dirs.OrderByDescending(d => d, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(d);
                if (!Skip.Contains(name) && !name.StartsWith('.'))
                {
                    pending.Push(d);
                }
            }
        }
    }
}
