using System.Text.RegularExpressions;

namespace LeanStudio.Core.Workflow;

/// <summary>
/// The declarations of a file that have no doc comment, which Mathlib's <c>docBlame</c> linter asks for on every
/// public definition, structure, class and inductive type (a theorem needs none, though it may have one). Found from
/// the text, so it works without building anything. Reported as <c>missing-doc</c>.
/// </summary>
public static class DocCoverage
{
    private static readonly Regex Declaration = new(
        @"^(?:@\[[^\]]*\]\s*)*(?:(?:protected|public|noncomputable|partial|unsafe|nonrec)\s+)*(?<kw>def|abbrev|structure|class|inductive|opaque|theorem|lemma)\s+(?<name>[^\s:({\[]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// The declarations at the start of a line that have no doc comment right above them (attributes on their own
    /// lines in between are skipped). Private ones are left out; theorems and lemmas only with <paramref name="includeTheorems"/>.
    /// </summary>
    public static IReadOnlyList<StyleProblem> Find(string text, bool includeTheorems = false)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        bool[] code = LeanStudio.Core.Editing.LeanText.CodeMask(text.Replace("\r\n", "\n", StringComparison.Ordinal));
        var found = new List<StyleProblem>();
        int offset = 0;
        for (int i = 0; i < lines.Length; offset += lines[i].Length + 1, i++)
        {
            string line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || (offset < code.Length && !code[offset]))
            {
                continue; // indented, or inside a comment or string
            }
            Match m = Declaration.Match(line);
            if (!m.Success || (m.Groups["kw"].Value is "theorem" or "lemma" && !includeTheorems) || line.TrimStart('@').StartsWith("private ", StringComparison.Ordinal) || Regex.IsMatch(line, @"\bprivate\b"))
            {
                continue;
            }
            if (!HasDocAbove(lines, i))
            {
                found.Add(new StyleProblem(i, "missing-doc", $"`{m.Groups["name"].Value}` has no doc comment."));
            }
        }
        return found;
    }

    private static bool HasDocAbove(string[] lines, int declaration)
    {
        int k = declaration - 1;
        while (k >= 0 && lines[k].StartsWith("@[", StringComparison.Ordinal) && lines[k].TrimEnd().EndsWith(']'))
        {
            k--; // an attribute line of its own
        }
        if (k < 0 || !lines[k].TrimEnd().EndsWith("-/", StringComparison.Ordinal))
        {
            return false;
        }
        while (k >= 0 && !lines[k].StartsWith("/-", StringComparison.Ordinal))
        {
            k--;
        }
        return k >= 0 && lines[k].StartsWith("/--", StringComparison.Ordinal);
    }
}
