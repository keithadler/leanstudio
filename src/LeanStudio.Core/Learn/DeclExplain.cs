using System.Text;
using System.Text.RegularExpressions;

namespace LeanStudio.Core.Learn;

/// <summary>
/// A declaration read aloud, for someone who has not met the syntax yet: <c>def double (n : Nat) : Nat := n + n</c> is "a
/// function that takes n (a natural number) and gives back a natural number". Works from the declaration's header, the way
/// <see cref="PlainEnglish"/> works from a statement.
/// </summary>
public static class DeclExplain
{
    private static readonly Regex Head = new(
        @"^(?:@\[[^\]]*\]\s*)*(?:(?:private|protected|public|noncomputable|partial|unsafe|nonrec)\s+)*(?<kw>def|abbrev|theorem|lemma|example|structure|inductive|class|instance)\b\s*(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["Nat"] = "natural number", ["ℕ"] = "natural number", ["Int"] = "integer", ["ℤ"] = "integer", ["Bool"] = "true/false value",
        ["String"] = "piece of text", ["Char"] = "character", ["Float"] = "decimal number", ["Prop"] = "statement", ["Unit"] = "nothing",
        ["Type"] = "type", ["Rat"] = "fraction", ["ℚ"] = "fraction", ["Real"] = "real number", ["ℝ"] = "real number",
    };

    private static readonly Dictionary<string, string> Plurals = new(StringComparer.Ordinal)
    {
        ["natural number"] = "natural numbers", ["integer"] = "integers", ["true/false value"] = "true/false values", ["piece of text"] = "pieces of text",
        ["character"] = "characters", ["decimal number"] = "decimal numbers", ["statement"] = "statements", ["fraction"] = "fractions", ["real number"] = "real numbers",
    };

    /// <summary>
    /// The declaration of <paramref name="text"/> that contains 0-based <paramref name="line"/> (it starts at the margin, and
    /// runs to the next thing at the margin), read aloud; null when the line is not in one.
    /// </summary>
    public static string? ReadAt(string text, int line)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (line < 0 || line >= lines.Length || lines[line].Trim().Length == 0)
        {
            return null;
        }
        if (!char.IsWhiteSpace(lines[line][0]) && !Head.IsMatch(lines[line]) && !lines[line].StartsWith("@[", StringComparison.Ordinal) && !lines[line].StartsWith('|'))
        {
            return null; // something else at the margin: a comment, a command
        }
        int start = line;
        while (start >= 0 && (lines[start].Length == 0 || char.IsWhiteSpace(lines[start][0]) || !Head.IsMatch(lines[start])))
        {
            if (start < line && lines[start].Length > 0 && !char.IsWhiteSpace(lines[start][0]) && !Head.IsMatch(lines[start]) && !lines[start].StartsWith("@[", StringComparison.Ordinal))
            {
                return null;
            }
            start--;
        }
        if (start < 0)
        {
            return null;
        }
        int end = start + 1;
        while (end < lines.Length && (lines[end].Length == 0 || char.IsWhiteSpace(lines[end][0]) || lines[end].StartsWith('|')))
        {
            end++;
        }
        return Read(string.Join('\n', lines[start..end]));
    }

    /// <summary>The first declaration in <paramref name="declaration"/> read aloud, or null when it does not start with one.</summary>
    public static string? Read(string declaration)
    {
        Match m = Head.Match(declaration.Trim());
        if (!m.Success)
        {
            return null;
        }
        string kw = m.Groups["kw"].Value, rest = m.Groups["rest"].Value.Trim();
        return kw switch
        {
            "structure" or "class" => Structure(kw, rest),
            "inductive" => Inductive(rest),
            "instance" => $"An instance: it tells Lean how {Cut(rest)} works, so the rest of the code can use it automatically.",
            _ => Function(kw, rest),
        };
    }

    private static string Function(string kw, string rest)
    {
        int assign = IndexOfTop(rest, ":=");
        string head = assign >= 0 ? rest[..assign] : rest;
        int pipe = IndexOfTop(head, "\n|");
        head = (pipe >= 0 ? head[..pipe] : head).Trim();
        string name = "";
        if (kw != "example")
        {
            Match n = Regex.Match(head, @"^[^\s:({\[]+");
            name = n.Value;
            head = head[n.Length..].Trim();
        }
        int colon = IndexOfTop(head, ":");
        string binders = colon >= 0 ? head[..colon] : head;
        string result = colon >= 0 ? head[(colon + 1)..].Trim() : "";
        var givens = new List<string>();
        var assumptions = new List<string>();
        var propNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string group in Groups(binders))
        {
            int c = group.IndexOf(':', StringComparison.Ordinal);
            if (c < 0)
            {
                continue;
            }
            string[] names = group[..c].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string type = group[(c + 1)..].Trim();
            if (type == "Prop")
            {
                propNames.UnionWith(names);
                givens.Add($"{Join(names)} (each a statement)");
            }
            else if (IsStatement(type) || propNames.Contains(type))
            {
                assumptions.Add(propNames.Contains(type) ? type : ReadInSentence(type).TrimEnd('.'));
            }
            else
            {
                givens.Add($"{Join(names)} ({(names.Length > 1 ? "each " : "")}{A(type)})");
            }
        }
        string subject = name.Length > 0 ? $"`{name}`" : "This example";
        if (kw is "theorem" or "lemma" or "example")
        {
            string claim = result.Length > 0 ? ReadInSentence(result) : "something";
            var sb = new StringBuilder(subject).Append(kw == "example" ? " states" : " is a theorem. It says");
            sb.Append(givens.Count > 0 ? $" that for any {Join(givens)}," : " that");
            if (assumptions.Count > 0)
            {
                sb.Append(" if ").Append(string.Join(" and ", assumptions)).Append(", then");
            }
            return sb.Append(' ').Append(claim.TrimEnd('.')).Append('.').ToString();
        }
        if (givens.Count == 0)
        {
            return result.Length > 0 ? $"{subject} is a value: {A(result)}." : $"{subject} is a definition.";
        }
        return $"{subject} is a function. Given {Join(givens)}, it gives back {(result.Length > 0 ? A(result) : "a result")}.";
    }

    private static string Structure(string kw, string rest)
    {
        string[] lines = rest.Split('\n');
        string name = Regex.Match(lines[0], @"^[^\s:({\[]+").Value;
        var fields = new List<string>();
        foreach (string l in lines.Skip(1))
        {
            Match f = Regex.Match(l, @"^\s+([\p{L}_][\p{L}\p{N}_']*)\s*:\s*(.+?)\s*(?::=.*)?$");
            if (f.Success)
            {
                fields.Add($"{f.Groups[1].Value} ({A(f.Groups[2].Value)})");
            }
        }
        if (kw == "class")
        {
            return fields.Count > 0 ? $"`{name}` is a class: a promise a type can make, with {(fields.Count == 1 ? "this member" : "these members")}: {Join(fields)}." : $"`{name}` is a class: a promise a type can make.";
        }
        return fields.Count > 0 ? $"`{name}` is a structure: a record that bundles together {Join(fields)}." : $"`{name}` is a structure: a record.";
    }

    private static string Inductive(string rest)
    {
        string[] lines = rest.Split('\n');
        string name = Regex.Match(lines[0], @"^[^\s:({\[]+").Value;
        List<string> ctors = [.. lines.Skip(1).Select(l => Regex.Match(l.Trim(), @"^\|\s*([^\s:({\[]+)")).Where(x => x.Success).Select(x => x.Groups[1].Value)];
        return ctors.Count > 0
            ? $"`{name}` is a type with {ctors.Count} way{(ctors.Count == 1 ? "" : "s")} to build a value: {string.Join(", ", ctors.Select(c => "`" + c + "`"))}."
            : $"`{name}` is a type defined by listing the ways to build its values.";
    }

    private static string TypeWords(string type)
    {
        string t = type.Trim().Trim('(', ')').Trim();
        if (Types.TryGetValue(t, out string? w))
        {
            return w;
        }
        Match list = Regex.Match(t, @"^(List|Array|Option)\s+(.+)$");
        if (list.Success)
        {
            string inner = TypeWords(list.Groups[2].Value);
            string many = Plurals.TryGetValue(inner, out string? pl) ? pl : inner + "s";
            return list.Groups[1].Value switch { "Option" => $"value that may be missing ({inner})", "Array" => $"array of {many}", _ => $"list of {many}" };
        }
        Match fn = Regex.Match(t, @"^(.+?)\s*(?:→|->)\s*(.+)$");
        if (fn.Success)
        {
            return $"function from {A(fn.Groups[1].Value)} to {A(fn.Groups[2].Value)}";
        }
        return t.Length is 1 && char.IsLetter(t[0]) && !char.IsUpper(t[0]) ? $"{t} (any type)" : $"`{t}`";
    }

    private static bool IsStatement(string type) =>
        Regex.IsMatch(type, @"[=≤<≥>≠∧∨↔¬∀∃∈⊆∣]|→") && !Regex.IsMatch(type, @"^[\p{L}\p{N}_ ]+\s*(→|->)\s*[\p{L}\p{N}_ ]+$") || type is "True" or "False";

    /// <summary>The type in words with its article: "a natural number", "an integer".</summary>
    private static string A(string type)
    {
        string w = TypeWords(type);
        return w.StartsWith('`') || w.Contains("(any type)", StringComparison.Ordinal) ? w : $"{Article(w)} {w}";
    }

    private static string Join(IEnumerable<string> items)
    {
        List<string> l = [.. items];
        return l.Count <= 1 ? string.Concat(l) : string.Join(", ", l[..^1]) + " and " + l[^1];
    }

    private static string Article(string word) => word.Length > 0 && "aeiou".Contains(word[0], StringComparison.Ordinal) ? "an" : "a";

    private static string Lower(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    /// <summary>
    /// A statement read aloud for the middle of a sentence. <see cref="PlainEnglish.Read"/> starts its reading with a
    /// capital; that capital goes, unless the statement itself starts with it: <c>IsEven (double n)</c> stays
    /// <c>IsEven</c>, which is a name, not the start of a sentence.
    /// </summary>
    private static string ReadInSentence(string statement)
    {
        string reading = PlainEnglish.Read(statement);
        string start = statement.TrimStart();
        return start.Length > 0 && reading.Length > 0 && char.IsUpper(start[0]) && reading[0] == start[0] ? reading : Lower(reading);
    }

    private static string Cut(string s) => s.Length > 50 ? s[..50] + "…" : s;

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

    private static int IndexOfTop(string s, string token)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            depth += s[i] is '(' or '[' or '{' ? 1 : s[i] is ')' or ']' or '}' ? -1 : 0;
            if (depth == 0 && string.CompareOrdinal(s, i, token, 0, token.Length) == 0 && !(token == ":" && i + 1 < s.Length && s[i + 1] == '='))
            {
                return i;
            }
        }
        return -1;
    }
}
