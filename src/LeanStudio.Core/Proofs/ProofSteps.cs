using System.Text.RegularExpressions;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Proofs;

/// <summary>One line of a tactic proof, and where to ask Lean for the state after it.</summary>
public sealed record ProofStep(int Line, string Text, int Indent)
{
    /// <summary>The start of the tactic: Lean reports the state before it there.</summary>
    public Position Before => new(Line, Indent);

    /// <summary>The end of the line: Lean reports the state after the tactic there.</summary>
    public Position After => new(Line, Indent + Text.Length);
}

/// <summary>The tactic proof around a line: the declaration it belongs to and each tactic line in order.</summary>
public sealed record TacticProof(string Declaration, int DeclarationLine, Position Start, IReadOnlyList<ProofStep> Steps);

/// <summary>
/// Finds the tactic block a cursor is in, by layout. Lean decides where a proof ends by indentation too, so a
/// line-based reading gets the steps a person sees, including those inside case arms and bullets.
/// </summary>
public static partial class ProofSteps
{
    [GeneratedRegex(@"^(@\[[^\]]*\]\s*)*((private|protected|noncomputable|partial|unsafe|nonrec|scoped|local)\s+)*(theorem|lemma|example|def|instance|abbrev|opaque|axiom)\b")]
    private static partial Regex DeclarationStart();

    [GeneratedRegex(@"^(theorem|lemma|example|def|instance|abbrev|opaque|axiom)\s+([^\s:({\[]+)")]
    private static partial Regex DeclarationName();

    [GeneratedRegex(@"(^|[\s(:=])by(\s*$|\s+)")]
    private static partial Regex By();

    public static TacticProof? Find(IReadOnlyList<string> lines, int cursorLine)
    {
        if (cursorLine < 0 || cursorLine >= lines.Count)
        {
            return null;
        }
        int start = -1;
        for (int i = cursorLine; i >= 0; i--)
        {
            if (DeclarationStart().IsMatch(lines[i]))
            {
                start = i;
                break;
            }
            // A column-0 line that is not a declaration (a `namespace`, `open`, `end`) above the cursor
            // means the cursor is not inside a declaration.
            if (i < cursorLine && lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]) && !IsComment(lines[i]) && !lines[i].StartsWith('@'))
            {
                return null;
            }
        }
        if (start < 0)
        {
            return null;
        }
        int end = lines.Count;
        for (int i = start + 1; i < lines.Count; i++)
        {
            string l = lines[i];
            if (l.Length > 0 && !char.IsWhiteSpace(l[0]) && !IsComment(l))
            {
                end = i;
                break;
            }
        }
        if (cursorLine >= end)
        {
            return null;
        }

        // Find `by`; everything after it in the declaration is the tactic block.
        bool inBlockComment = false;
        for (int i = start; i < end; i++)
        {
            string code = StripComments(lines[i], ref inBlockComment);
            Match m = By().Match(code);
            if (!m.Success)
            {
                continue;
            }
            int afterBy = m.Index + m.Length;
            var steps = new List<ProofStep>();
            string rest = code[afterBy..];
            if (rest.Trim().Length > 0)
            {
                // `:= by simp` on the declaration line itself.
                int indent = afterBy + (rest.Length - rest.TrimStart().Length);
                steps.Add(new ProofStep(i, rest.Trim(), indent));
            }
            for (int j = i + 1; j < end; j++)
            {
                string c = StripComments(lines[j], ref inBlockComment);
                string t = c.Trim();
                if (t.Length == 0)
                {
                    continue;
                }
                int indent = c.Length - c.TrimStart().Length;
                steps.Add(new ProofStep(j, t, indent));
            }
            string declName = NameOf(lines[start]);
            var byPos = new Position(i, m.Index + m.Value.IndexOf("by", StringComparison.Ordinal) + 2);
            return new TacticProof(declName, start, byPos, steps);
        }
        return null;
    }

    /// <summary>
    /// Whether a step's tactic carries on into the lines after it (`cases h with`, `calc`, `induction n with`):
    /// then the end of its line is not the end of the tactic, and there is no "after" to report yet.
    /// </summary>
    public static bool ContinuesBelow(IReadOnlyList<ProofStep> steps, int index) =>
        index + 1 < steps.Count && (steps[index + 1].Indent > steps[index].Indent || steps[index + 1].Text.StartsWith('|'));

    /// <summary>
    /// Lean records a declaration's range from its doc comment and attributes; the line people mean is the one
    /// with the keyword, so step past a leading <c>/-- … -/</c> and any <c>@[…]</c> lines. Both lines are 1-based.
    /// </summary>
    public static int DeclarationLine(IReadOnlyList<string> lines, int oneBased)
    {
        int i = oneBased - 1;
        if (i < 0 || i >= lines.Count)
        {
            return oneBased;
        }
        if (lines[i].TrimStart().StartsWith("/--", StringComparison.Ordinal))
        {
            while (i < lines.Count && !lines[i].Contains("-/", StringComparison.Ordinal))
            {
                i++;
            }
            i++;
        }
        while (i < lines.Count && (lines[i].TrimStart().StartsWith("@[", StringComparison.Ordinal) || lines[i].Trim().Length == 0))
        {
            i++;
        }
        return i < lines.Count ? i + 1 : oneBased;
    }

    private static string NameOf(string line)
    {
        string s = Regex.Replace(line, @"^(@\[[^\]]*\]\s*)*((private|protected|noncomputable|partial|unsafe|nonrec|scoped|local)\s+)*", "");
        Match m = DeclarationName().Match(s);
        if (m.Success)
        {
            return m.Groups[2].Value;
        }
        return s.StartsWith("example", StringComparison.Ordinal) ? "example" : s.Split(' ')[0];
    }

    private static bool IsComment(string line) => line.TrimStart().StartsWith("--", StringComparison.Ordinal);

    /// <summary>The code on a line with <c>--</c> and <c>/- -/</c> comments blanked out, keeping columns.</summary>
    public static string StripComments(string line, ref bool inBlock)
    {
        var chars = line.ToCharArray();
        for (int k = 0; k < chars.Length; k++)
        {
            if (inBlock)
            {
                if (chars[k] == '-' && k + 1 < chars.Length && chars[k + 1] == '/')
                {
                    chars[k] = chars[k + 1] = ' ';
                    k++;
                    inBlock = false;
                    continue;
                }
                chars[k] = ' ';
            }
            else if (chars[k] == '/' && k + 1 < chars.Length && chars[k + 1] == '-')
            {
                inBlock = true;
                chars[k] = chars[k + 1] = ' ';
                k++;
            }
            else if (chars[k] == '-' && k + 1 < chars.Length && chars[k + 1] == '-')
            {
                for (int r = k; r < chars.Length; r++)
                {
                    chars[r] = ' ';
                }
                break;
            }
            else if (chars[k] == '"')
            {
                // Skip string literals so `"--"` is not a comment.
                for (k++; k < chars.Length && chars[k] != '"'; k++)
                {
                    if (chars[k] == '\\')
                    {
                        k++;
                    }
                }
            }
        }
        return new string(chars).TrimEnd();
    }
}

/// <summary>What one tactic changed: hypotheses added, removed or retyped, and the goals it opened or closed.</summary>
public sealed record StepChange(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Changed,
    int GoalsBefore,
    int GoalsAfter,
    bool TargetChanged)
{
    public bool ClosedAll => GoalsAfter == 0;

    public string Summary
    {
        get
        {
            if (GoalsBefore > 0 && GoalsAfter == 0)
            {
                return "goals accomplished";
            }
            var parts = new List<string>();
            if (GoalsAfter != GoalsBefore)
            {
                parts.Add(GoalsAfter > GoalsBefore ? $"+{GoalsAfter - GoalsBefore} goal{(GoalsAfter - GoalsBefore == 1 ? "" : "s")}"
                                                   : $"closed {GoalsBefore - GoalsAfter} goal{(GoalsBefore - GoalsAfter == 1 ? "" : "s")}");
            }
            parts.AddRange(Added.Select(a => "+" + a));
            parts.AddRange(Removed.Select(r => "−" + r));
            parts.AddRange(Changed.Select(c => "~" + c));
            if (TargetChanged && parts.Count == 0)
            {
                parts.Add("goal rewritten");
            }
            return parts.Count == 0 ? "no change" : string.Join("  ", parts);
        }
    }

    /// <summary>Compare the main goal before and after a tactic.</summary>
    public static StepChange Between(InteractiveGoals before, InteractiveGoals after)
    {
        InteractiveGoal? b = before.Goals.FirstOrDefault();
        InteractiveGoal? a = after.Goals.FirstOrDefault();
        var bh = Hyps(b);
        var ah = Hyps(a);
        var added = ah.Keys.Where(k => !bh.ContainsKey(k)).ToList();
        var removed = b is not null && a is not null ? bh.Keys.Where(k => !ah.ContainsKey(k)).ToList() : [];
        var changed = ah.Keys.Where(k => bh.TryGetValue(k, out string? t) && t != ah[k]).ToList();
        bool target = a is not null && b is not null && a.Type.Text != b.Type.Text;
        return new StepChange(added, removed, changed, before.Goals.Count, after.Goals.Count, target);
    }

    private static Dictionary<string, string> Hyps(InteractiveGoal? g)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (g is null)
        {
            return d;
        }
        foreach (InteractiveHypothesis h in g.Hypotheses)
        {
            foreach (string n in h.Names)
            {
                d[n] = h.Type.Text;
            }
        }
        return d;
    }
}
