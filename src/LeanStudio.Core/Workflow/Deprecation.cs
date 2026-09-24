using System.Text.RegularExpressions;

namespace LeanStudio.Core.Workflow;

/// <summary>
/// Keeping a renamed declaration's old name working, as Mathlib asks of every rename: a deprecated alias after the
/// declaration, so code that still uses the old name compiles, with a warning that names the new one.
/// </summary>
public static class Deprecation
{
    private static readonly Regex Declaration = new(
        @"^\s*(?:@\[[^\]]*\]\s*)*(?:(?:private|protected|public|noncomputable|partial|unsafe|nonrec)\s+)*(?<kw>theorem|lemma|def|abbrev|instance|structure|inductive|class|opaque)\s+(?<name>[^\s:({\[]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// The declaration keyword (<c>theorem</c>, <c>def</c>…) if <paramref name="line"/> declares
    /// <paramref name="name"/>, else null.
    /// </summary>
    public static string? DeclarationKeyword(string line, string name)
    {
        Match m = Declaration.Match(line);
        return m.Success && m.Groups["name"].Value == name ? m.Groups["kw"].Value : null;
    }

    /// <summary>
    /// The alias line. With Batteries (Mathlib has it), Mathlib's own form: <c>@[deprecated (since := "…")] alias
    /// old := new</c>. Without it, the same in core Lean: a deprecated theorem or def whose type is the new one's.
    /// </summary>
    /// <param name="oldName">The old name, as the declaration wrote it.</param>
    /// <param name="newName">The new name, written the same way.</param>
    /// <param name="keyword">The declaration's keyword.</param>
    /// <param name="batteries">Whether <c>alias</c> (from Batteries) is available.</param>
    /// <param name="since">The date of the rename.</param>
    public static string AliasLine(string oldName, string newName, string keyword, bool batteries, DateOnly since)
    {
        string date = since.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (batteries)
        {
            return $"@[deprecated (since := \"{date}\")] alias {oldName} := {newName}";
        }
        string kind = keyword is "theorem" or "lemma" ? "theorem" : "def";
        return $"@[deprecated {newName} (since := \"{date}\")] {kind} {oldName} : type_of% @{newName} := @{newName}";
    }

    private static readonly Regex Continuation = new(@"^(?:\||where\b|termination_by\b|decreasing_by\b|deriving\b|with\b)", RegexOptions.Compiled);

    /// <summary>
    /// The 0-based line after the last line of the declaration that starts at <paramref name="start"/>: its body is
    /// indented, so it ends before the next line that starts at the margin (other than <c>|</c>, <c>where</c> and
    /// the like), with trailing blank lines left out.
    /// </summary>
    public static int EndOfDeclaration(IReadOnlyList<string> lines, int start)
    {
        int end = start + 1;
        for (int i = start + 1; i < lines.Count; i++)
        {
            string l = lines[i].TrimEnd('\r');
            if (l.Length == 0)
            {
                continue;
            }
            if (!char.IsWhiteSpace(l[0]) && !Continuation.IsMatch(l))
            {
                break;
            }
            end = i + 1;
        }
        return end;
    }

    /// <summary>
    /// <paramref name="text"/> with the alias inserted after the declaration of <paramref name="newName"/> that starts
    /// on <paramref name="declarationLine"/> (0-based), separated by a blank line. Returns the text unchanged when
    /// that line doesn't declare it.
    /// </summary>
    public static string AddAlias(string text, int declarationLine, string oldName, string newName, bool batteries, DateOnly since)
    {
        string[] lines = text.Split('\n');
        if (declarationLine < 0 || declarationLine >= lines.Length || DeclarationKeyword(lines[declarationLine], newName) is not string keyword)
        {
            return text;
        }
        int end = EndOfDeclaration(lines, declarationLine);
        var result = new List<string>(lines);
        // Keep the file's line endings: a CRLF file gets CRLF lines.
        string cr = lines[Math.Max(0, end - 1)].EndsWith('\r') ? "\r" : "";
        result.InsertRange(end, [cr, AliasLine(oldName, newName, keyword, batteries, since) + cr]);
        return string.Join('\n', result);
    }
}
