using System.Globalization;
using System.Text.RegularExpressions;

namespace LeanStudio.Core.Workflow;

/// <summary>A deprecated declaration old enough to be deleted.</summary>
/// <param name="StartLine">The first line to delete: its doc comment, else its attribute (0-based).</param>
/// <param name="EndLine">The line after its last line (0-based, exclusive).</param>
/// <param name="Name">The deprecated name, as written.</param>
/// <param name="Since">The date in its <c>(since := "…")</c>.</param>
/// <param name="AgeMonths">Whole months from <paramref name="Since"/> to the day it was checked.</param>
public sealed record StaleDeprecation(int StartLine, int EndLine, string Name, DateOnly Since, int AgeMonths);

/// <summary>
/// Mathlib deletes a deprecated alias some months after the rename, so code that still uses it has had time to move.
/// This finds the ones past that age by their <c>(since := "yyyy-mm-dd")</c> and deletes them, with their doc comments.
/// The counterpart of <see cref="Deprecation"/>, which writes them.
/// </summary>
public static class StaleDeprecations
{
    private static readonly Regex Since = new(@"@\[[^\]]*\bdeprecated\b[^\]]*\(since\s*:=\s*""(?<d>\d{4}-\d{2}(?:-\d{2})?)""\)", RegexOptions.Compiled);
    private static readonly Regex Alias = new(@"\balias\s+(?<n>[^\s:=]+)", RegexOptions.Compiled);
    private static readonly Regex Declaration = new(
        @"(?:theorem|lemma|def|abbrev|instance|structure|inductive|class|opaque)\s+(?<n>[^\s:({\[]+)", RegexOptions.Compiled);

    /// <summary>The deprecations in <paramref name="text"/> at least <paramref name="months"/> months old on <paramref name="today"/>, in line order.</summary>
    public static IReadOnlyList<StaleDeprecation> Find(string text, DateOnly today, int months = 6)
    {
        string[] lines = text.Split('\n');
        var found = new List<StaleDeprecation>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match m = Since.Match(lines[i]);
            if (!m.Success || !TryParse(m.Groups["d"].Value, out DateOnly since))
            {
                continue;
            }
            int age = (today.Year - since.Year) * 12 + today.Month - since.Month - (today.Day < since.Day ? 1 : 0);
            if (age < months)
            {
                continue;
            }
            // The declaration is on this line (an alias, or `@[deprecated …] theorem …`) or below the attribute.
            int decl = i;
            while (decl < lines.Length && !Alias.IsMatch(lines[decl]) && !Declaration.IsMatch(lines[decl]))
            {
                if (decl > i + 3)
                {
                    decl = -1;
                    break;
                }
                decl++;
            }
            if (decl < 0 || decl >= lines.Length)
            {
                continue;
            }
            Match name = Alias.Match(lines[decl]);
            if (!name.Success)
            {
                name = Declaration.Match(lines[decl]);
            }
            int start = i;
            if (i > 0 && lines[i - 1].TrimEnd('\r').EndsWith("-/", StringComparison.Ordinal))
            {
                int k = i - 1;
                while (k > 0 && !lines[k].TrimStart().StartsWith("/--", StringComparison.Ordinal))
                {
                    k--;
                }
                if (lines[k].TrimStart().StartsWith("/--", StringComparison.Ordinal))
                {
                    start = k;
                }
            }
            int end = Deprecation.EndOfDeclaration(lines, decl);
            found.Add(new StaleDeprecation(start, end, name.Groups["n"].Value, since, age));
            i = end - 1;
        }
        return found;
    }

    /// <summary><paramref name="text"/> without the declarations of <paramref name="stale"/>, and without the blank line a deletion would double.</summary>
    public static string Remove(string text, IEnumerable<StaleDeprecation> stale)
    {
        string[] lines = text.Split('\n');
        var drop = new HashSet<int>();
        foreach (StaleDeprecation s in stale)
        {
            for (int i = s.StartLine; i < s.EndLine; i++)
            {
                drop.Add(i);
            }
            // Two blank lines would meet where the declaration was: drop the one after it.
            bool blankBefore = s.StartLine == 0 || lines[s.StartLine - 1].Trim().Length == 0;
            if (blankBefore && s.EndLine < lines.Length && lines[s.EndLine].Trim().Length == 0)
            {
                drop.Add(s.EndLine);
            }
        }
        return string.Join('\n', lines.Where((_, i) => !drop.Contains(i)));
    }

    private static bool TryParse(string s, out DateOnly date) =>
        DateOnly.TryParseExact(s.Length == 7 ? s + "-01" : s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
