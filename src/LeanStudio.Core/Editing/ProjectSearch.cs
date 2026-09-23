using System.Text.RegularExpressions;

namespace LeanStudio.Core.Editing;

public sealed record SearchHit(string Path, int Line, int Column, int Length, string LineText);

/// <summary>Find in files: every match of a text or regular expression in a project's sources.</summary>
public static class ProjectSearch
{
    private static readonly HashSet<string> Skip = new(StringComparer.Ordinal) { ".git", ".lake", "build", "bin", "obj", "node_modules" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".lean", ".md", ".toml", ".json", ".txt", ".yml", ".yaml" };

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

    /// <summary>Source files under a folder, skipping build output, dependencies and hidden folders.</summary>
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
