using System.Text.RegularExpressions;

namespace LeanStudio.Core.Workflow;

/// <summary>
/// The file conventions Mathlib asks of a contributor beyond the text rules in <see cref="StyleCheck"/>: the
/// copyright header at the top, a module docstring (<c>/-! … -/</c>) before the first declaration, and theorem names that
/// do not start with a capital (a theorem is named in snake_case) and structure, class and inductive names that do not
/// (types are UpperCamelCase). Rules reported: <c>copyright-header</c>, <c>module-doc</c>, <c>theorem-name</c> and <c>type-name</c>.
/// </summary>
public static class MathlibConventions
{
    private static readonly Regex Copyright = new(@"^Copyright \(c\) \d{4}(?:, \d{4})* .+\. All rights reserved\.$", RegexOptions.Compiled);
    private static readonly Regex TypeDecl = new(@"^\s*(?:@\[[^\]]*\]\s*)*(?:(?:protected|private|public|nonrec|unsafe)\s+)*(?:structure|class|inductive)\s+(?<n>[^\s:({\[]+)", RegexOptions.Compiled);
    private static readonly Regex Theorem = new(@"^\s*(?:@\[[^\]]*\]\s*)*(?:(?:protected|private|nonrec)\s+)*(?:theorem|lemma)\s+(?<n>[^\s:({\[]+)", RegexOptions.Compiled);

    /// <summary>The convention problems in <paramref name="text"/>, in line order.</summary>
    public static IReadOnlyList<StyleProblem> Find(string text)
    {
        var problems = new List<StyleProblem>();
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (!HasHeader(lines))
        {
            problems.Add(new StyleProblem(0, "copyright-header",
                "The file should start with a header: `/-`, `Copyright (c) YEAR Name. All rights reserved.`, `Released under Apache 2.0 license as described in the file LICENSE.`, `Authors: Name`, `-/`."));
        }
        if (!lines.Any(l => l.StartsWith("/-!", StringComparison.Ordinal)))
        {
            problems.Add(new StyleProblem(0, "module-doc", "The file has no module docstring (`/-! … -/`) saying what it is about."));
        }
        for (int i = 0; i < lines.Length; i++)
        {
            Match t = TypeDecl.Match(lines[i]);
            if (t.Success)
            {
                string typeName = t.Groups["n"].Value.Split('.')[^1];
                if (typeName.Length > 0 && char.IsLower(typeName[0]))
                {
                    problems.Add(new StyleProblem(i, "type-name", $"`{typeName}`: a structure, class or inductive type is named in UpperCamelCase."));
                }
            }
            Match m = Theorem.Match(lines[i]);
            if (m.Success)
            {
                string last = m.Groups["n"].Value.Split('.')[^1];
                if (last.Length > 0 && char.IsUpper(last[0]))
                {
                    problems.Add(new StyleProblem(i, "theorem-name", $"`{last}`: a theorem is named in snake_case, starting with a lowercase letter."));
                }
            }
        }
        return problems;
    }

    private static bool HasHeader(string[] lines) =>
        lines.Length >= 5 && lines[0] == "/-" && Copyright.IsMatch(lines[1])
        && lines[2] == "Released under Apache 2.0 license as described in the file LICENSE."
        && lines[3].StartsWith("Authors: ", StringComparison.Ordinal) && lines[3].Length > "Authors: ".Length
        && lines[4] == "-/";

    /// <summary>
    /// <paramref name="text"/> with Mathlib's copyright header put at the top, followed by a blank line, unless the file
    /// already starts with a comment (the header may only be mistyped: that is for a person to look at).
    /// </summary>
    public static string AddHeader(string text, int year, string author)
    {
        if (text.TrimStart().StartsWith("/-", StringComparison.Ordinal))
        {
            return text;
        }
        return $"/-\nCopyright (c) {year} {author}. All rights reserved.\nReleased under Apache 2.0 license as described in the file LICENSE.\nAuthors: {author}\n-/\n{text}";
    }
}
