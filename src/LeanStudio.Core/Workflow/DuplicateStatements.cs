using System.Text;
using System.Text.RegularExpressions;
using LeanStudio.Core.Editing;

namespace LeanStudio.Core.Workflow;

/// <summary>One theorem's statement, as found in a file.</summary>
/// <param name="Path">The file.</param>
/// <param name="Line">0-based line of the declaration.</param>
/// <param name="Name">The theorem's name, as written.</param>
/// <param name="Statement">Its statement as written (binders and type, up to <c>:=</c>), on one line.</param>
public sealed record TheoremStatement(string Path, int Line, string Name, string Statement);

/// <summary>
/// Theorems that say the same thing under different names, found by comparing their statements with the names of the
/// variables they bind and the white space left out (<c>(a b : ℕ) : a + b = b + a</c> is <c>(x y : ℕ) : x + y = y + x</c>).
/// Mathlib asks that a result is stated once; a project that grew from several people's work often states it twice.
/// Statements are compared as text, so two that are equal only by unfolding are not found.
/// </summary>
public static class DuplicateStatements
{
    private static readonly Regex Declaration = new(@"^(?:@\[[^\]]*\]\s*)*(?:(?:protected|private|nonrec)\s+)*(?:theorem|lemma)\s+(?<name>[^\s:({\[]+)(?<rest>.*)$", RegexOptions.Compiled);

    /// <summary>The theorems and lemmas in <paramref name="text"/> that start at the margin and whose statement ends with <c>:=</c> within a few lines.</summary>
    public static IReadOnlyList<TheoremStatement> Statements(string path, string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        bool[] code = LeanText.CodeMask(string.Join('\n', lines));
        var found = new List<TheoremStatement>();
        int offset = 0;
        for (int i = 0; i < lines.Length; offset += lines[i].Length + 1, i++)
        {
            if (offset < code.Length && !code[offset])
            {
                continue; // inside a comment or a string
            }
            Match m = Declaration.Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }
            var statement = new StringBuilder(m.Groups["rest"].Value);
            for (int k = i + 1; k < lines.Length && k <= i + 12 && !statement.ToString().Contains(":=", StringComparison.Ordinal); k++)
            {
                statement.Append(' ').Append(lines[k].Trim());
            }
            string s = statement.ToString();
            int end = s.IndexOf(":=", StringComparison.Ordinal);
            if (end >= 0)
            {
                found.Add(new TheoremStatement(path, i, m.Groups["name"].Value, Regex.Replace(s[..end], @"\s+", " ").Trim()));
            }
        }
        return found;
    }

    /// <summary>
    /// <paramref name="statement"/> with the variables it binds named <c>_0</c>, <c>_1</c>… in the order they are bound and
    /// its white space made single spaces, so statements that differ only in those compare equal.
    /// </summary>
    public static string Normalize(string statement)
    {
        string s = Regex.Replace(statement, @"\s+", " ").Trim().Replace("->", "→", StringComparison.Ordinal);
        var names = new List<string>();
        foreach (Match binder in Regex.Matches(s, @"[({](?<n>[^:(){}\[\]]+?)\s:\s")) // (a b : T) and {a b : T}
        {
            names.AddRange(binder.Groups["n"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        foreach (Match q in Regex.Matches(s, @"[∀∃λ]\s*(?<n>[^,:]+?)\s*[,:]")) // ∀ a b, …
        {
            names.AddRange(q.Groups["n"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        int next = 0;
        foreach (string name in names.Where(n => Regex.IsMatch(n, @"^[\p{L}_][\p{L}\p{N}_'₀-₉]*$")).Distinct(StringComparer.Ordinal))
        {
            s = Regex.Replace(s, $@"(?<![\p{{L}}\p{{N}}_'.]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}_'₀-₉])", $"_{next++}");
        }
        return s;
    }

    /// <summary>
    /// The groups of two or more theorems with the same statement, across <paramref name="statements"/>; groups and
    /// their members in the order found. Statements too short to say much (<c>True</c>, <c>a = a</c>) are left out.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TheoremStatement>> Find(IEnumerable<TheoremStatement> statements) =>
        [.. statements.Select(s => (Statement: s, Key: Normalize(s.Statement)))
            .Where(x => x.Key.Length >= 12)
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<TheoremStatement>)[.. g.Select(x => x.Statement)])];

    /// <summary>The duplicate statements among the Lean files under <paramref name="root"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<TheoremStatement>> Scan(string root, CancellationToken ct = default)
    {
        var all = new List<TheoremStatement>();
        foreach (string file in ProjectSearch.Files(root, leanOnly: true))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                all.AddRange(Statements(file, File.ReadAllText(file)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a file that can't be read has no statements to compare
            }
        }
        return Find(all);
    }
}
