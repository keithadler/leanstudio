using System.Text;
using System.Text.RegularExpressions;

namespace LeanStudio.Core.Workflow;

/// <summary>
/// A name for a theorem in Mathlib's naming scheme, worked out from its statement: the conclusion first, read left to
/// right with each operation and relation spelled as a word (<c>a + b = b + a</c> is <c>add_comm</c>, <c>0 + a = a</c> is
/// <c>zero_add</c>, <c>a ≤ b → b &lt; c → a &lt; c</c> is <c>lt_of_le_of_lt</c>), then <c>_of_</c> before each thing it assumes. A starting
/// point for a person, who knows the library's own conventions better than a rule can: it handles statements about
/// operations and relations, and says nothing (null) for one it can't read.
/// </summary>
public static class TheoremNamer
{
    private static readonly (string Symbol, string Word)[] Relations =
    [
        ("↔", "iff"), ("≠", "ne"), ("≤", "le"), ("≥", "ge"), ("∣", "dvd"), ("∉", "not_mem"), ("∈", "mem"), ("⊆", "subset"),
        ("<", "lt"), (">", "gt"), ("=", "eq"),
    ];

    private static readonly Dictionary<char, string> Operations = new()
    {
        ['+'] = "add", ['*'] = "mul", ['/'] = "div", ['^'] = "pow", ['∪'] = "union", ['∩'] = "inter", ['∧'] = "and", ['∨'] = "or",
        ['∑'] = "sum", ['∏'] = "prod", ['∘'] = "comp", ['•'] = "smul", ['%'] = "mod", ['⊔'] = "sup", ['⊓'] = "inf",
    };

    /// <summary>
    /// The suggested name for the statement <paramref name="statement"/>: what follows the theorem's name, such as
    /// <c>(a b : ℕ) : a + b = b + a</c> or just <c>a ≤ b → b &lt; c → a &lt; c</c>. Null when no relation can be read from it.
    /// </summary>
    public static string? Suggest(string statement)
    {
        var hypotheses = new List<string>();
        string rest = statement.Trim();
        // Binders come before the colon that starts the conclusion; those whose type states something are assumptions.
        int colon = rest.StartsWith('∀') ? -1 : TopLevelColon(rest);
        if (colon >= 0)
        {
            foreach (string binder in Groups(rest[..colon]))
            {
                int c = binder.IndexOf(':', StringComparison.Ordinal);
                if (c >= 0 && FindRelation(binder[(c + 1)..]) is not null)
                {
                    hypotheses.Add(binder[(c + 1)..]);
                }
            }
            rest = rest[(colon + 1)..];
        }
        rest = StripForall(rest.Trim());
        List<string> parts = SplitTop(rest, "→");
        string conclusion = parts[^1];
        hypotheses.AddRange(parts[..^1]);
        string? head = Words(conclusion);
        if (head is null)
        {
            return null;
        }
        string[] assumed = [.. hypotheses.Select(Words).Where(w => w is not null).Select(w => w!)];
        return head + string.Concat(assumed.Select(w => "_of_" + w));
    }

    private static readonly Regex Declaration = new(@"^\s*(?:@\[[^\]]*\]\s*)*(?:(?:protected|private|nonrec)\s+)*(?:theorem|lemma)\s+(?<name>\S+)", RegexOptions.Compiled);

    /// <summary>
    /// The theorem or lemma whose declaration contains 0-based <paramref name="line"/> of <paramref name="text"/>: its
    /// name as written, its statement (up to <c>:=</c>, on one line) and the suggested name; null when the line is
    /// not in one or no name can be worked out.
    /// </summary>
    public static (string Current, string Statement, string Suggested)? SuggestAt(string text, int line)
    {
        string[] lines = text.Split('\n');
        if (line < 0 || line >= lines.Length || lines[line].Trim().Length == 0)
        {
            return null; // a blank line between declarations belongs to none
        }
        for (int start = line; start >= 0; start--)
        {
            Match m = Declaration.Match(lines[start]);
            if (!m.Success)
            {
                if (start < line && lines[start].Length > 0 && !char.IsWhiteSpace(lines[start][0]))
                {
                    return null; // a different command begins above the line
                }
                continue;
            }
            var statement = new StringBuilder(lines[start][(m.Index + m.Length)..]);
            for (int i = start + 1; i < lines.Length && !statement.ToString().Contains(":=", StringComparison.Ordinal) && i <= start + 12; i++)
            {
                statement.Append(' ').Append(lines[i].Trim());
            }
            string stmt = statement.ToString();
            int end = stmt.IndexOf(":=", StringComparison.Ordinal);
            stmt = (end >= 0 ? stmt[..end] : stmt).Trim();
            return Suggest(stmt) is string name ? (m.Groups["name"].Value, stmt, name) : null;
        }
        return null;
    }

    /// <summary>The words of one proposition (<c>a + b = b + a</c> → <c>add_comm</c>), or null without a relation.</summary>
    private static string? Words(string prop)
    {
        string p = prop.Trim();
        bool negated = false;
        if (p.StartsWith('¬'))
        {
            negated = true;
            p = p[1..].Trim();
        }
        (int at, string symbol, string word)? rel = FindRelation(p);
        if (rel is not { } r)
        {
            return null;
        }
        string left = p[..r.at].Trim(), right = p[(r.at + r.symbol.Length)..].Trim();
        List<string> l = Terms(left), rt = Terms(right);
        string? result = Special(r.word, left, right, l, rt);
        if (result is null)
        {
            bool eqWithAtom = r.word == "eq" && (rt.Count == 0 || l.Count == 0);
            var words = new List<string>();
            if (eqWithAtom)
            {
                words.AddRange(l.Count > 0 ? l : rt);
            }
            else
            {
                words.AddRange(l);
                words.Add(r.word);
                words.AddRange(rt);
            }
            if (SelfApplied(left) || SelfApplied(right) || (l.Count == 0 && rt.Count > 0 && Identifiers(right).Contains(left)))
            {
                words.Add("self");
            }
            result = string.Join('_', words);
        }
        return negated ? "not_" + result : result;
    }

    /// <summary>Names Mathlib spells specially: commutativity and associativity.</summary>
    private static string? Special(string relation, string left, string right, List<string> l, List<string> r)
    {
        if (relation != "eq" || l.Count == 0 || !l.SequenceEqual(r))
        {
            return null;
        }
        List<string> a = Identifiers(left), b = Identifiers(right);
        if (a.Count >= 2 && a.SequenceEqual(b.AsEnumerable().Reverse()) && l.Distinct().Count() == 1 && l.Count == 1)
        {
            return l[0] + "_comm";
        }
        if (a.Count == 3 && a.SequenceEqual(b) && l.Count == 2 && l.Distinct().Count() == 1)
        {
            return l[0] + "_assoc";
        }
        return null;
    }

    /// <summary>The operation and constant words of a term, in the order written; empty for a bare variable.</summary>
    private static List<string> Terms(string term)
    {
        var words = new List<string>();
        bool operandNext = true;
        foreach (char c in term)
        {
            if (Operations.TryGetValue(c, out string? op))
            {
                words.Add(op);
                operandNext = true;
            }
            else if (c == '-')
            {
                words.Add(operandNext ? "neg" : "sub");
                operandNext = true;
            }
            else if (c is '0' or '1' or '2' && words.Count > 0 | !term.Trim().All(char.IsDigit))
            {
                words.Add(c switch { '0' => "zero", '1' => "one", _ => "two" });
                operandNext = false;
            }
            else if (!char.IsWhiteSpace(c) && c is not '(' and not ')')
            {
                operandNext = false;
            }
            else if (c == '(')
            {
                operandNext = true;
            }
        }
        // A bare numeral (`= 0`) is a constant, not an operation: it names nothing by itself.
        return term.Trim().All(char.IsDigit) ? [] : words;
    }

    private static bool SelfApplied(string term)
    {
        List<string> ids = Identifiers(term);
        return ids.Count == 2 && ids[0] == ids[1] && Terms(term).Count == 1;
    }

    private static List<string> Identifiers(string term)
    {
        var ids = new List<string>();
        var sb = new StringBuilder();
        foreach (char c in term + " ")
        {
            if (char.IsLetter(c) || c == '_' || (sb.Length > 0 && (char.IsDigit(c) || c == '\'')))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0)
            {
                ids.Add(sb.ToString());
                sb.Clear();
            }
        }
        return ids;
    }

    private static (int At, string Symbol, string Word)? FindRelation(string text)
    {
        int depth = 0;
        foreach ((string symbol, string word) in Relations.Where(x => x.Symbol == "↔").Concat(Relations.Where(x => x.Symbol != "↔")))
        {
            depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                depth += c is '(' or '[' or '{' ? 1 : c is ')' or ']' or '}' ? -1 : 0;
                if (depth == 0 && string.CompareOrdinal(text, i, symbol, 0, symbol.Length) == 0
                    && !(symbol == "=" && i > 0 && text[i - 1] is '<' or '>' or '≠' or ':' or '=') && !(symbol is "<" or ">" && i + 1 < text.Length && text[i + 1] == '-'))
                {
                    return (i, symbol, word);
                }
            }
        }
        return null;
    }

    private static int TopLevelColon(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            depth += text[i] is '(' or '[' or '{' ? 1 : text[i] is ')' or ']' or '}' ? -1 : 0;
            if (depth == 0 && text[i] == ':' && (i + 1 >= text.Length || text[i + 1] != '='))
            {
                return i;
            }
        }
        return -1;
    }

    private static IEnumerable<string> Groups(string binders)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < binders.Length; i++)
        {
            if (binders[i] is '(' or '{' or '[')
            {
                if (depth++ == 0)
                {
                    start = i + 1;
                }
            }
            else if (binders[i] is ')' or '}' or ']' && --depth == 0)
            {
                yield return binders[start..i];
            }
        }
    }

    private static List<string> SplitTop(string text, string separator)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            depth += text[i] is '(' or '[' or '{' ? 1 : text[i] is ')' or ']' or '}' ? -1 : 0;
            if (depth == 0 && string.CompareOrdinal(text, i, separator, 0, separator.Length) == 0)
            {
                parts.Add(text[start..i]);
                start = i + separator.Length;
                i += separator.Length - 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    private static string StripForall(string text)
    {
        if (!text.StartsWith('∀'))
        {
            return text;
        }
        int comma = text.IndexOf(',', StringComparison.Ordinal);
        return comma < 0 ? text : text[(comma + 1)..].Trim();
    }
}
