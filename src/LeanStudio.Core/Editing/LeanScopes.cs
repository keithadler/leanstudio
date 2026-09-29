using System.Text.RegularExpressions;

namespace LeanStudio.Core.Editing;

/// <summary>What kind of scope a <see cref="LeanScope"/> is.</summary>
public enum ScopeKind
{
    /// <summary><c>namespace Foo</c>.</summary>
    Namespace,

    /// <summary><c>section</c>, named or not (also <c>noncomputable section</c> and <c>public section</c>).</summary>
    Section,

    /// <summary>A <c>mutual</c> block.</summary>
    Mutual,

    /// <summary>A declaration: a theorem, definition, instance, structure and so on.</summary>
    Declaration,
}

/// <summary>A scope a line is inside: where it starts, and what to call it.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Name">Its name (<c>Nat.Prime</c>, <c>foo</c>), or what it is when it has none (<c>section</c>, <c>example</c>).</param>
/// <param name="Line">The 0-based line it starts on.</param>
public sealed record LeanScope(ScopeKind Kind, string Name, int Line);

/// <summary>
/// The scopes around a line of Lean, read from the text alone (so at once, and whatever state Lean is in): the
/// <c>namespace</c>, <c>section</c> and <c>mutual</c> blocks it is inside, outermost first, and the declaration it
/// belongs to. For the sticky header at the top of the editor and the breadcrumbs above it.
/// </summary>
public static partial class LeanScopes
{
    [GeneratedRegex(@"^(?:@\[[^\]]*\]\s*)*(?:(?:private|protected|public|noncomputable|partial|unsafe|nonrec|scoped|local)\s+)*(?<kw>theorem|lemma|def|abbrev|instance|example|structure|inductive|class|opaque|axiom|irreducible_def|macro_rules|macro|syntax|elab|notation|infixl|infixr|infix|prefix|postfix)\b(?<rest>.*)$")]
    private static partial Regex DeclarationLine();

    [GeneratedRegex(@"^namespace\s+(?<name>\S+)")]
    private static partial Regex NamespaceLine();

    [GeneratedRegex(@"^(?:@\[[^\]]*\]\s*)*(?:(?:noncomputable|public|private)\s+)*section\b\s*(?<name>[^\s-]*)")]
    private static partial Regex SectionLine();

    [GeneratedRegex(@"^end\b\s*(?<name>[^\s-]*)")]
    private static partial Regex EndLine();

    /// <summary>
    /// The scopes around the 0-based line <paramref name="line"/> of <paramref name="text"/>, outermost first: the
    /// blocks still open there, then the declaration containing it (when the line is inside one, its first line
    /// included). For many questions about one text, make a <see cref="ScopeIndex"/> once.
    /// </summary>
    public static IReadOnlyList<LeanScope> At(string text, int line) => new ScopeIndex(text).At(line);

    /// <summary>
    /// The scopes to pin at the top of the editor when <paramref name="topLine"/> is the first line shown: those
    /// around it whose first line has scrolled out of view, at most <paramref name="max"/> (the innermost kept).
    /// </summary>
    public static IReadOnlyList<LeanScope> Sticky(string text, int topLine, int max = 5) => new ScopeIndex(text).Sticky(topLine, max);

    /// <summary>The scopes of one text, read once (which lines are code) and asked many times, as the editor scrolls.</summary>
    public sealed class ScopeIndex
    {
        private readonly string[] _lines;
        private readonly bool[] _codeStart;

        /// <summary>Read <paramref name="text"/>.</summary>
        public ScopeIndex(string text)
        {
            _lines = text.Split('\n');
            bool[] code = LeanText.CodeMask(text);
            _codeStart = new bool[_lines.Length];
            for (int i = 0, start = 0; i < _lines.Length; start += _lines[i].Length + 1, i++)
            {
                _codeStart[i] = _lines[i].Length > 0 && start < code.Length && code[start];
            }
        }

        /// <summary>How many lines the text has.</summary>
        public int LineCount => _lines.Length;

        /// <summary>The 0-based line's text, without a trailing carriage return.</summary>
        public string Line(int line) => line >= 0 && line < _lines.Length ? _lines[line].TrimEnd('\r') : "";

        /// <summary>See <see cref="LeanScopes.At"/>.</summary>
        public IReadOnlyList<LeanScope> At(int line)
        {
            var stack = new List<LeanScope>();
            int last = Math.Min(line, _lines.Length - 1);
            for (int i = 0; i <= last; i++)
            {
                // Commands that open and close scopes start a line, outside comments and strings.
                if (!_codeStart[i])
                {
                    continue;
                }
                string t = Line(i);
                if (NamespaceLine().Match(t) is { Success: true } ns)
                {
                    stack.Add(new LeanScope(ScopeKind.Namespace, ns.Groups["name"].Value, i));
                }
                else if (SectionLine().Match(t) is { Success: true } sec && !t.StartsWith("end", StringComparison.Ordinal))
                {
                    string name = sec.Groups["name"].Value;
                    stack.Add(new LeanScope(ScopeKind.Section, name.Length > 0 ? name : "section", i));
                }
                else if (t == "mutual" || t.StartsWith("mutual ", StringComparison.Ordinal))
                {
                    stack.Add(new LeanScope(ScopeKind.Mutual, "mutual", i));
                }
                else if (EndLine().Match(t) is { Success: true } end && i < line)
                {
                    // `end Foo.Bar` closes a namespace opened as `namespace Foo.Bar`; a bare `end` the last block.
                    string name = end.Groups["name"].Value;
                    int at = name.Length == 0 ? stack.Count - 1 : stack.FindLastIndex(s => s.Name == name || s.Name.EndsWith("." + name, StringComparison.Ordinal));
                    if (at >= 0)
                    {
                        stack.RemoveRange(at, stack.Count - at);
                    }
                }
            }
            if (last >= 0 && Declaration(_lines, last, l => _codeStart[l]) is LeanScope decl)
            {
                stack.Add(decl);
            }
            return stack;
        }

        /// <summary>See <see cref="LeanScopes.Sticky"/>.</summary>
        public IReadOnlyList<LeanScope> Sticky(int topLine, int max = 5)
        {
            List<LeanScope> hidden = At(topLine).Where(s => s.Line < topLine).ToList();
            return hidden.Count > max ? hidden.Skip(hidden.Count - max).ToList() : hidden;
        }
    }

    /// <summary>The declaration the 0-based line <paramref name="line"/> belongs to, or null (between declarations, in a comment…).</summary>
    private static LeanScope? Declaration(string[] lines, int line, Func<int, bool> isCode)
    {
        for (int i = line; i >= 0; i--)
        {
            string l = lines[i].TrimEnd('\r');
            if (l.Length == 0 || char.IsWhiteSpace(l[0]) || !isCode(i))
            {
                continue;
            }
            // The nearest line at column 0: the declaration's own, unless it is another command.
            Match m = DeclarationLine().Match(l);
            if (!m.Success)
            {
                return null;
            }
            string kw = m.Groups["kw"].Value;
            string rest = m.Groups["rest"].Value.Trim();
            string name = Proofs.Profiler.DeclarationName(l) ?? (kw is "instance" or "example" ? kw + (rest.StartsWith(':') ? " " + Short(rest) : "") : kw);
            return new LeanScope(ScopeKind.Declaration, name, i);
        }
        return null;
    }

    /// <summary>A statement up to its <c>:=</c> or <c>where</c>, cut to 40 characters: <c>: Inhabited Nat</c>.</summary>
    private static string Short(string s)
    {
        foreach (string stop in new[] { ":=", " where", " |" })
        {
            int at = s.IndexOf(stop, 1, StringComparison.Ordinal);
            if (at > 0)
            {
                s = s[..at];
            }
        }
        s = s.Trim();
        return s.Length > 40 ? s[..40] + "…" : s;
    }
}
