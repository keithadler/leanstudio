using System.Text.RegularExpressions;

namespace LeanStudio.Core.Workflow;

/// <summary>
/// Places where Lean quietly hands back a made-up value instead of failing, so a definition or a statement can compile
/// while describing something that does not exist: <c>sInf</c> and <c>sSup</c> of an empty set (0 on ℕ and ℝ), division
/// by something that may be zero (<c>x / 0 = 0</c>), and the <c>xs[i]!</c>, <c>head!</c>, <c>get!</c> family, which
/// returns <c>default</c> when there is nothing to return. Found from the text, so it works without building anything.
/// It cannot see types, so it says "may": a hit is a place to look, not a bug. Truncated natural subtraction
/// (<c>2 - 3 = 0</c> on ℕ) needs types and is not covered. Reported as <c>junk-infsup</c>, <c>junk-division</c> and
/// <c>junk-default-access</c>.
/// </summary>
public static class JunkValues
{
    private static readonly Regex Start = new(
        @"^(?:@\[[^\]]*\]\s*)*(?:(?:protected|public|private|noncomputable|partial|unsafe|nonrec)\s+)*(?<kw>def|abbrev|instance|theorem|lemma|example|opaque)\b",
        RegexOptions.Compiled);

    private static readonly Regex InfSup = new(@"(?<![\w'])(?:[\w.]*\.)?(?:sInf|sSup|iInf|iSup)(?![\w'])|⨅|⨆", RegexOptions.Compiled);
    private static readonly Regex Division = new(@"(?<![/\-])/(?![/\-])\s*(?<den>[A-Za-z_][\w.']*)", RegexOptions.Compiled);
    private static readonly Regex DefaultAccess = new(@"\b(?:head|tail|getLast|get|back|getD)!|\]!|\.get!|\.head!|\.getLast!|\.back!", RegexOptions.Compiled);

    /// <summary>
    /// Every place in <paramref name="text"/> where a junk value could stand in. Division is off unless
    /// <paramref name="divisions"/> is set: on Mathlib it fires on field identities like <c>conj (x / y) = conj x / conj y</c>
    /// that are meant to hold at zero, so it is for application proofs, where an unguarded <c>/</c> is worth a look. For a definition the whole body is read,
    /// since the body is what the name means; for a theorem, lemma or example only the statement, up to its first
    /// <c>:=</c>, since the proof says nothing about what is claimed.
    /// </summary>
    public static IReadOnlyList<StyleProblem> Find(string text, bool divisions = false)
    {
        string norm = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = norm.Split('\n');
        bool[] code = LeanStudio.Core.Editing.LeanText.CodeMask(norm);
        var found = new List<StyleProblem>();

        int offset = 0;
        var starts = new List<(int Line, int Offset, string Kind)>();
        for (int i = 0; i < lines.Length; offset += lines[i].Length + 1, i++)
        {
            string line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || (offset < code.Length && !code[offset]))
            {
                continue;
            }
            Match m = Start.Match(line);
            if (m.Success)
            {
                starts.Add((i, offset, m.Groups["kw"].Value));
            }
            else if (IsTopLevelCommand(line))
            {
                starts.Add((i, offset, ""));
            }
        }

        for (int s = 0; s < starts.Count; s++)
        {
            if (starts[s].Kind.Length == 0)
            {
                continue;
            }
            int from = starts[s].Offset;
            int to = s + 1 < starts.Count ? starts[s + 1].Offset : norm.Length;
            bool statementOnly = starts[s].Kind is "theorem" or "lemma" or "example";
            int end = to;
            if (statementOnly)
            {
                int assign = IndexOfCode(norm, code, ":=", from, to);
                int by = IndexOfCode(norm, code, " by", from, to);
                int cut = new[] { assign, by }.Where(x => x >= 0).DefaultIfEmpty(-1).Min();
                if (cut >= 0)
                {
                    end = cut;
                }
            }
            Scan(norm, code, from, end, found, divisions);
        }

        found.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : string.CompareOrdinal(a.Rule, b.Rule));
        return found;
    }

    private static bool IsTopLevelCommand(string line) =>
        line.StartsWith("namespace ", StringComparison.Ordinal) || line.StartsWith("section", StringComparison.Ordinal)
        || line.StartsWith("end ", StringComparison.Ordinal) || line == "end" || line.StartsWith("variable ", StringComparison.Ordinal)
        || line.StartsWith("open ", StringComparison.Ordinal) || line.StartsWith("structure ", StringComparison.Ordinal)
        || line.StartsWith("class ", StringComparison.Ordinal) || line.StartsWith("inductive ", StringComparison.Ordinal)
        || line.StartsWith("#", StringComparison.Ordinal);

    private static int IndexOfCode(string text, bool[] code, string needle, int from, int to)
    {
        int at = from;
        while (at < to)
        {
            int i = text.IndexOf(needle, at, to - at, StringComparison.Ordinal);
            if (i < 0)
            {
                return -1;
            }
            if (i < code.Length && code[i])
            {
                return i;
            }
            at = i + 1;
        }
        return -1;
    }

    private static void Scan(string text, bool[] code, int from, int to, List<StyleProblem> found, bool divisions)
    {
        string block = text.Substring(from, to - from);
        foreach (Match m in InfSup.Matches(block))
        {
            int abs = from + m.Index;
            if (abs < code.Length && code[abs])
            {
                found.Add(new StyleProblem(LineOf(text, abs), "junk-infsup",
                    $"`{m.Value}` returns a made-up value (0 on ℕ and ℝ) when the set is empty or unbounded, so this can compile without the thing it names existing."));
            }
        }
        foreach (Match m in Division.Matches(divisions ? block : ""))
        {
            int abs = from + m.Index;
            if (abs >= code.Length || !code[abs])
            {
                continue;
            }
            string den = m.Groups["den"].Value;
            if (den.Length == 0 || den is "by" or "fun" or "if" or "then" or "else" or "at" or "with" || Guarded(block, den))
            {
                continue;
            }
            found.Add(new StyleProblem(LineOf(text, abs), "junk-division",
                $"Division by `{den}`: in Lean `x / 0 = 0`, so this may quietly mean 0 when `{den}` is zero, and no hypothesis here says `{den} ≠ 0`."));
        }
        foreach (Match m in DefaultAccess.Matches(block))
        {
            int abs = from + m.Index;
            if (abs < code.Length && code[abs])
            {
                found.Add(new StyleProblem(LineOf(text, abs), "junk-default-access",
                    $"`{m.Value.TrimStart('.')}` stands in `default` when there is nothing to return, so a statement about it can hold of an empty list or an out-of-range index."));
            }
        }
    }

    private static bool Guarded(string block, string den)
    {
        string d = Regex.Escape(den);
        return Regex.IsMatch(block, $@"\b{d}\s*≠\s*0|0\s*≠\s*{d}\b|0\s*<\s*{d}\b|\b{d}\s*>\s*0|\b{d}\s*≠\s*\(?0|NeZero\s*\(?{d}\b|\b{d}\s*=\s*[1-9]|\b{d}\s*:=\s*[1-9]|Nat\.pos_of_ne_zero\s*{d}\b");
    }

    private static int LineOf(string text, int offset)
    {
        int line = 0;
        for (int i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }
        return line;
    }
}
